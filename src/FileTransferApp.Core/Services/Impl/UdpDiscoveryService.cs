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
    private int _recvErrorStreak;

    public DeviceNode Self { get; }

    public UdpDiscoveryService(
        IMessenger messenger,
        string deviceName,
        DeviceType deviceType,
        IDeviceIdentityStore? identityStore = null)
    {
        _messenger = messenger;
        // v2：DeviceId 使用安装级持久身份（首次生成，之后跨进程复用），
        // 保证配对记录与设备身份稳定。未注入身份存储时（如旧测试）回退随机 GUID。
        var deviceId = identityStore?.GetOrCreateAsync().GetAwaiter().GetResult().DeviceId
            ?? Guid.NewGuid().ToString("D");
        Self = new DeviceNode
        {
            DeviceId = deviceId,
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
        if (_cts is not null) return Task.CompletedTask; // 幂等：已在运行则不重复绑定 53317
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
        while (!token.IsCancellationRequested)
        {
            var client = _client;
            if (client is null)
            {
                // 重建失败（旧端口未释放等）：稍后重试，避免空转
                try { await Task.Delay(500, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }

            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(token).ConfigureAwait(false);
                _recvErrorStreak = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { continue; } // socket 被重建：下一轮取新实例
            catch (SocketException)
            {
                // 网络切换 / 底层 socket 失效：连续失败达阈值 → 重建并重新加入多播组
                if (++_recvErrorStreak >= 5) { _recvErrorStreak = 0; TryRebindClient(); }
                try { await Task.Delay(200, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            catch
            {
                if (++_recvErrorStreak >= 5) { _recvErrorStreak = 0; TryRebindClient(); }
                continue;
            }

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
            bool changed;
            lock (_lock)
            {
                isNew = !_devices.TryGetValue(node.DeviceId, out var prev);
                // 已知设备的地址/端口/名称变化 → 需要通知 UI 就地刷新（否则列表会一直显示旧 IP）
                changed = !isNew && prev is not null &&
                          (!Equals(prev.IpAddress, node.IpAddress) ||
                           prev.Port != node.Port ||
                           !string.Equals(prev.DeviceName, node.DeviceName, StringComparison.Ordinal));
                _devices[node.DeviceId] = node;
            }

            if (isNew)
            {
                DeviceDiscovered?.Invoke(this, node);
                _messenger.Send(new DeviceDiscoveredMessage(node));
            }
            else if (changed)
            {
                _messenger.Send(new DeviceUpdatedMessage(node));
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

    /// <summary>
    /// 重建接收客户端并重新加入多播组。用于网络切换（WiFi 变更/漫游/前后台）后底层 socket 失效、
    /// 心跳再也收不到导致"设备永久离线"的场景。失败时把 _client 置空，由接收循环稍后重试。
    /// </summary>
    private void TryRebindClient()
    {
        try { _client?.Dispose(); } catch { /* ignore */ }
        try
        {
            var c = new UdpClient(new IPEndPoint(IPAddress.Any, ProtocolConstants.DiscoveryPort))
            {
                EnableBroadcast = true
            };
            try { c.JoinMulticastGroup(IPAddress.Parse(ProtocolConstants.MulticastGroup)); }
            catch { /* 多播受限环境降级：仍可用广播/手动直连 */ }
            _client = c;
        }
        catch
        {
            _client = null; // 立即重建失败（端口未释放等）：接收循环会稍后重试
        }
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
