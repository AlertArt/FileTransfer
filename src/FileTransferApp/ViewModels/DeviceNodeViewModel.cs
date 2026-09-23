using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using FileTransferApp.Core.Models;

namespace FileTransferApp.ViewModels;

/// <summary>
/// 设备节点的 UI 包装视图模型。Core 的 <see cref="DeviceNode"/> 保持纯 DTO，
/// 此 VM 负责提供绑定所需的属性通知（配对状态徽标等），不污染 Core 模型。
/// </summary>
public partial class DeviceNodeViewModel : ObservableObject
{
    /// <summary>被包装的纯数据模型。</summary>
    public DeviceNode Device { get; }

    public DeviceNodeViewModel(DeviceNode device)
    {
        Device = device;
    }

    /// <summary>是否已与本机配对（加密传输可用）。由 UI 层按配对记录维护，不参与广播/序列化。</summary>
    [ObservableProperty]
    private bool isPaired;

    public string DeviceId => Device.DeviceId;
    public string DeviceName => Device.DeviceName;
    public DeviceType DeviceType => Device.DeviceType;
    public int Port => Device.Port;

    public IPAddress? IpAddress => Device.IpAddress;

    public bool IsOnline(TimeSpan? offlineAfter = null) => Device.IsOnline(offlineAfter);

    public string EndPoint => Device.EndPoint;

    /// <summary>
    /// 用发现层的最新节点信息**就地**刷新被包装的 Device（IP 漂移/重连/改名）。
    /// 就地更新（而非替换对象）很关键：传输任务的 Peer 引用与本 VM 的 Device 是同一对象，
    /// 就地刷新可让"旧任务重试"也拿到新地址。
    /// </summary>
    public void ApplyUpdate(DeviceNode latest)
    {
        Device.IpAddress = latest.IpAddress;
        if (latest.Port > 0) Device.Port = latest.Port;
        if (!string.IsNullOrEmpty(latest.DeviceName)) Device.DeviceName = latest.DeviceName;
        Device.ProtocolVersion = latest.ProtocolVersion;
        Device.LastSeenUtc = latest.LastSeenUtc;

        OnPropertyChanged(nameof(IpAddress));
        OnPropertyChanged(nameof(EndPoint));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(DeviceType));
    }
}