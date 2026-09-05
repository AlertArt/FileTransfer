using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 基于 UDP 多播的局域网设备发现服务。
/// 监听 53317 端口，每 3s 向 239.255.255.250 广播心跳 JSON；
/// 节点收到广播后更新本地设备列表，10s 未收到心跳则标记离线。
/// </summary>
public sealed class UdpDiscoveryService : IDiscoveryService, IDisposable
{
    private readonly IMessenger _messenger;
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceNode> _devices = new();

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Timer? _heartbeatTimer;
    private Timer? _sweepTimer;
    private bool _disposed;

    public DeviceNode Self { get; }

    public UdpDiscoveryService(IMessenger messenger, string deviceName, DeviceType deviceType)
    {
        _messenger = messenger;
        Self = new DeviceNode
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            DeviceName = deviceName,
            DeviceType = deviceType,
            Port = ProtocolConstants.TransferPort,
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            IsSelf = true
        };
    }

    public IReadOnlyList<DeviceNode> Devices
    {
        get
        {
            lock (_lock)
            {
                return _devices.Values.Where(d => !d.IsSelf).ToList();
            }
        }
    }

    public event EventHandler<DeviceNode>? DeviceDiscovered;
    public event EventHandler<string>? DeviceLost;

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = new UdpClient(new IPEndPoint(IPAddress.Any, ProtocolConstants.DiscoveryPort));
        _client.EnableBroadcast = true;
        try { _client.JoinMulticastGroup(IPAddress.Parse(ProtocolConstants.MulticastGroup)); }
        catch { /* 多播受限环境降级：仍可手动直连 */ }

        _receiveTask = Task.Run(ReceiveLoopAsync, _cts.Token);

        _heartbeatTimer = new Timer(
            _ => _ = BroadcastHeartbeatAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(ProtocolConstants.HeartbeatIntervalMs));

        _sweepTimer = new Timer(
            _ => SweepOffline(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _heartbeatTimer?.Dispose();
        _sweepTimer?.Dispose();
        _cts.Cancel();
        try { _client?.Dispose(); }
        catch { /* ignore */ }
        if (_receiveTask is not null)
        {
            try { await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
        _cts.Dispose();
        _cts = null;
    }

    public async Task<bool> ConnectDirectAsync(IPAddress address, int port, CancellationToken ct = default)
    {
        try
        {
            await SendHeartbeatToAsync(new IPEndPoint(address, port)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested && _client is not null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            DeviceNode? node;
            try
            {
                node = JsonSerializer.Deserialize<DeviceNode>(result.Buffer);
            }
            catch { continue; }
            if (node is null) continue;
            if (node.DeviceId == Self.DeviceId) continue;
            if (IPAddress.IsLoopback(result.RemoteEndPoint.Address)) continue;

            node.IpAddress = result.RemoteEndPoint.Address;
            node.LastSeenUtc = DateTime.UtcNow;

            bool isNew;
            lock (_lock)
            {
                isNew = !_devices.ContainsKey(node.DeviceId);
                _devices[node.DeviceId] = node;
            }

            if (isNew)
            {
                DeviceDiscovered?.Invoke(this, node);
                _messenger.Send(new DeviceDiscoveredMessage(node));
            }
        }
    }

    private async Task BroadcastHeartbeatAsync()
    {
        var payload = JsonSerializer.Serialize(Self);
        var bytes = Encoding.UTF8.GetBytes(payload);

        // 1) 多播（支持的路由器/交换机环境下高效）
        try { await _client!.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(ProtocolConstants.MulticastGroup), ProtocolConstants.DiscoveryPort)).ConfigureAwait(false); }
        catch { /* 多播失败忽略 */ }

        // 2) 广播（兼容不支持多播的 WiFi 网络，如大部分家用路由器）
        try { await _client!.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, ProtocolConstants.DiscoveryPort)).ConfigureAwait(false); }
        catch { /* 广播失败忽略 */ }
    }

    private async Task SendHeartbeatToAsync(IPEndPoint target)
    {
        if (_client is null) return;
        var json = JsonSerializer.Serialize(Self);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _client.SendAsync(bytes, bytes.Length, target).ConfigureAwait(false);
    }

    private void SweepOffline()
    {
        List<DeviceNode>? lost = null;
        var threshold = DateTime.UtcNow - TimeSpan.FromMilliseconds(ProtocolConstants.OfflineTimeoutMs);
        lock (_lock)
        {
            foreach (var kv in _devices)
            {
                if (kv.Value.LastSeenUtc < threshold)
                    (lost ??= new()).Add(kv.Value);
            }
            if (lost is not null)
                foreach (var d in lost) _devices.Remove(d.DeviceId);
        }
        if (lost is not null)
        {
            foreach (var d in lost)
            {
                DeviceLost?.Invoke(this, d.DeviceId);
                _messenger.Send(new DeviceLostMessage(d.DeviceId));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = StopAsync();
    }
}
