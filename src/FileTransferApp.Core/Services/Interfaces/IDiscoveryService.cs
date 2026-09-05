using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 局域网设备发现服务接口。
/// 启动后监听 UDP 53317 端口，每 3s 向 239.255.255.250 发送心跳，并维护本地设备列表；
/// 超过 10s 未收到心跳的节点自动标记离线。支持手动 IP 直连。
/// </summary>
public interface IDiscoveryService
{
    /// <summary>当前在线设备列表（不含本机）</summary>
    IReadOnlyList<DeviceNode> Devices { get; }

    /// <summary>本机设备节点</summary>
    DeviceNode Self { get; }

    /// <summary>当设备上线/离线时触发</summary>
    event EventHandler<DeviceNode>? DeviceDiscovered;
    event EventHandler<string>? DeviceLost;

    /// <summary>启动 UDP 发现监听与心跳广播</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>停止发现</summary>
    Task StopAsync();

    /// <summary>手动直连：直接向目标 IP:Port 发送一次本机心跳握手以触发对端加入列表</summary>
    Task<bool> ConnectDirectAsync(System.Net.IPAddress address, int port, CancellationToken ct = default);
}
