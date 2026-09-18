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
}