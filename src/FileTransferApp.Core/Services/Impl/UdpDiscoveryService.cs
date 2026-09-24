using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FileTransferApp.Core.Diagnostics;
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
    /// <summary>累计收到的 UDP 包数（诊断：为 0 说明根本没收到对方广播/多播）。</summary>
    private long _recvCount;
    /// <summary>接收循环 SocketException 次数（诊断：持续增长说明本端 socket 有问题）。</summary>
    private int _socketErrCount;

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
            Capabilities = ProtocolCapabilities.Self,
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
        var joined = false;
        try
        {
            _client.JoinMulticastGroup(IPAddress.Parse(ProtocolConstants.MulticastGroup));
            joined = true;
        }
        catch (Exception ex)
        {
            FtaTrace.Warn("FTA.DISC", $"join multicast {ProtocolConstants.MulticastGroup} FAILED: {ex.Message}");
        }
        FtaTrace.Info("FTA.DISC", $"discovery socket bound 0.0.0.0:{ProtocolConstants.DiscoveryPort}, multicast={(joined ? "ON" : "OFF")}, broadcast=ON");

        _receiveTask = Task.Run(ReceiveLoopAsync, _cts.Token);

        // 1s 调度 tick：按"是否有已知设备"自适应发送间隔（空闲降频省电）
        _heartbeatTimer = new Timer(
            _ => _ = HeartbeatTickAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1000));

        _sweepTimer = new Timer(
            _ => SweepOffline(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        return Task.CompletedTask;
    }

    /// <summary>网络切换后重建 socket 并重新加入多播组（供平台连接性回调调用）。</summary>
    public async Task RestartAsync()
    {
        FtaTrace.Info("FTA.DISC", "restarting discovery (network change)");
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
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
                _recvCount++;
                if (_recvCount == 1)
                    FtaTrace.Info("FTA.DISC", $"first UDP packet received from {result.RemoteEndPoint} ({result.Buffer.Length}B)");
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { continue; } // socket 被重建：下一轮取新实例
            catch (SocketException ex)
            {
                if (++_socketErrCount <= 3)
                    FtaTrace.Warn("FTA.DISC", $"receive SocketException #{_socketErrCount}: {ex.SocketErrorCode} {ex.Message}");
                // 网络切换 / 底层 socket 失效：连续失败达阈值 → 重建并重新加入多播组
                if (++_recvErrorStreak >= 5) { _recvErrorStreak = 0; FtaTrace.Warn("FTA.DISC", "rebinding discovery socket after repeated errors"); TryRebindClient(); }
                try { await Task.Delay(200, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            catch (Exception ex)
            {
                FtaTrace.Verbose("FTA.DISC", $"receive unexpected {ex.GetType().Name}: {ex.Message}");
                if (++_recvErrorStreak >= 5) { _recvErrorStreak = 0; TryRebindClient(); }
                continue;
            }

            DeviceNode? node;
            try
            {
                node = JsonSerializer.Deserialize<DeviceNode>(result.Buffer);
            }
            catch
            {
                FtaTrace.Verbose("FTA.DISC", $"drop bad-JSON from {result.RemoteEndPoint}");
                continue;
            }
            if (node is null) { FtaTrace.Verbose("FTA.DISC", "drop null-node"); continue; }
            if (node.DeviceId == Self.DeviceId) continue;
            if (IPAddress.IsLoopback(result.RemoteEndPoint.Address))
            {
                FtaTrace.Verbose("FTA.DISC", $"drop loopback from {result.RemoteEndPoint}");
                continue;
            }

            node.IpAddress = result.RemoteEndPoint.Address;
            node.LastSeenUtc = DateTime.UtcNow;

            bool isNew;
            bool changed;
            lock (_lock)
            {
                isNew = !_devices.TryGetValue(node.DeviceId, out var prev);
                // 记录到达间隔（排查"发现→丢失→发现"抖动：能看出实际心跳间隔是否 > 阈值）
                if (!isNew && prev is not null)
                {
                    var gapMs = (long)(node.LastSeenUtc - prev.LastSeenUtc).TotalMilliseconds;
                    if (gapMs > ProtocolConstants.HeartbeatIntervalMs * 2)
                        FtaTrace.Verbose("FTA.DISC", $"heartbeat gap {gapMs}ms from {node.DeviceName} ({node.DeviceId})");
                }
                // 已知设备的地址/端口/名称变化 → 需要通知 UI 就地刷新（否则列表会一直显示旧 IP）
                changed = !isNew && prev is not null &&
                          (!Equals(prev.IpAddress, node.IpAddress) ||
                           prev.Port != node.Port ||
                           !string.Equals(prev.DeviceName, node.DeviceName, StringComparison.Ordinal));
                _devices[node.DeviceId] = node;
            }

            FtaTrace.Verbose("FTA.DISC", $"recv {node.DeviceName} {node.IpAddress}:{node.Port} id={node.DeviceId} from {result.RemoteEndPoint}");
            if (isNew)
            {
                FtaTrace.Info("FTA.DISC", $"discovered {node.DeviceName} {node.IpAddress}:{node.Port}");
                DeviceDiscovered?.Invoke(this, node);
                _messenger.Send(new DeviceDiscoveredMessage(node));
            }
            else if (changed)
            {
                _messenger.Send(new DeviceUpdatedMessage(node));
            }
        }
    }

    /// <summary>空闲（无已知设备）时的心跳间隔：降频以省电（有设备时用 3s 保活）。</summary>
    private const int IdleHeartbeatIntervalMs = 10000;
    private long _lastHeartbeatTickMs;

    /// <summary>自适应心跳调度：有已知设备 → 3s（保活/快速发现）；无设备 → 10s（省电）。</summary>
    private async Task HeartbeatTickAsync()
    {
        int intervalMs;
        lock (_lock) intervalMs = _devices.Count > 0 ? ProtocolConstants.HeartbeatIntervalMs : IdleHeartbeatIntervalMs;

        var now = Environment.TickCount64;
        if (_lastHeartbeatTickMs != 0 && now - _lastHeartbeatTickMs < intervalMs) return;
        _lastHeartbeatTickMs = now;
        await BroadcastHeartbeatAsync().ConfigureAwait(false);
    }

    private async Task BroadcastHeartbeatAsync()
    {
        var payload = JsonSerializer.Serialize(Self);
        var bytes = Encoding.UTF8.GetBytes(payload);

        // 1) 多播（支持的路由器/交换机环境下高效）
        try { await _client!.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(ProtocolConstants.MulticastGroup), ProtocolConstants.DiscoveryPort)).ConfigureAwait(false); }
        catch { /* 多播失败忽略 */ }

        // 2) 广播：受限广播 + 各网卡子网定向广播。
        //    多网卡主机（WiFi + 以太网 + 虚拟/VPN 网卡，Windows 常见）下，仅发 255.255.255.255
        //    可能按默认路由走错接口，导致对端收不到——典型症状"对端能看到我、我看不到对端"。
        //    这里对每块活动网卡广播其子网地址（如 192.168.1.255）兜底。
        foreach (var target in GetBroadcastTargets())
        {
            try { await _client!.SendAsync(bytes, bytes.Length, new IPEndPoint(target, ProtocolConstants.DiscoveryPort)).ConfigureAwait(false); }
            catch { /* 单个目标失败忽略 */ }
        }

        // 3) 【核心保活策略】对每个已知对端逐个单播心跳（发往对方发现端口 53317）。
        //    Wi-Fi 省电/组播受限下广播常被过滤，而单播最可靠：即使一方收不到广播，
        //    也能靠对方的单播维持"在线"，避免"发现→丢失→发现"抖动。
        List<DeviceNode> peers;
        lock (_lock) peers = _devices.Values.ToList();
        foreach (var dev in peers)
        {
            if (dev.IpAddress is null) continue;
            try
            {
                await _client!.SendAsync(bytes, bytes.Length,
                    new IPEndPoint(dev.IpAddress, ProtocolConstants.DiscoveryPort)).ConfigureAwait(false);
            }
            catch { /* 单个对端失败忽略 */ }
        }
    }

    private IPAddress[] _broadcastTargets = Array.Empty<IPAddress>();
    private long _broadcastTargetsTick;

    /// <summary>受限广播(255.255.255.255) + 各活动网卡的子网定向广播地址；结果缓存 30s。</summary>
    private IPAddress[] GetBroadcastTargets()
    {
        var now = Environment.TickCount64;
        if (_broadcastTargets.Length > 0 && now - _broadcastTargetsTick < 30_000)
            return _broadcastTargets;

        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask is null || ua.IPv4Mask.GetAddressBytes().Length != 4) continue;

                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | (mask[i] ^ 255));
                    var addr = new IPAddress(b);
                    if (!list.Contains(addr)) list.Add(addr);
                }
            }
        }
        catch { /* 枚举失败退化为仅受限广播 */ }

        _broadcastTargets = list.ToArray();
        _broadcastTargetsTick = now;
        FtaTrace.Verbose("FTA.DISC", "broadcast targets: " + string.Join(", ", list.Select(a => a.ToString())));
        return _broadcastTargets;
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
                FtaTrace.Info("FTA.DISC", $"lost {d.DeviceName} {d.IpAddress}");
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
