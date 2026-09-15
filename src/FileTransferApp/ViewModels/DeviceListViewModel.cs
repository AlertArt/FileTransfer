using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.ViewModels;

/// <summary>
/// 设备列表视图模型。监听 DeviceDiscoveredMessage / DeviceLostMessage，
/// 在 UI 线程维护 ObservableCollection，支持手动直连。
/// </summary>
public partial class DeviceListViewModel : ObservableObject,
    IRecipient<DeviceDiscoveredMessage>,
    IRecipient<DeviceLostMessage>
{
    private readonly IMessenger _messenger;
    private readonly IDiscoveryService _discovery;

    public ObservableCollection<DeviceNode> Devices { get; } = new();

    /// <summary>设备列表是否为空（供 XAML 空态提示切换）。</summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>设备列表是否为空（正向属性，供 XAML 空态提示切换）。</summary>
    public bool IsEmpty => Devices.Count == 0;

    [ObservableProperty] public partial DeviceNode? SelectedDevice { get; set; }
    [ObservableProperty] public partial string ManualIp { get; set; } = string.Empty;
    /// <summary>手动直连端口：应该是对方的【传输端口】(默认 53318 = ProtocolConstants.TransferPort)，
    /// 不是发现端口(53317)。UDP 发现端口绑定在对方的 IDiscoveryService 上，它只收心跳；
    /// 而真实的 /prepare /chunk HTTP 请求必须打到对方的 ITransferServer 端口。</summary>
    [ObservableProperty] public partial string ManualPort { get; set; } = "53318";

    public DeviceListViewModel(IMessenger messenger, IDiscoveryService discovery)
    {
        _messenger = messenger;
        _discovery = discovery;
        foreach (var d in _discovery.Devices) Devices.Add(d);
        Devices.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(IsEmpty)); };
        _messenger.RegisterAll(this);
    }

    public void Receive(DeviceDiscoveredMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!Devices.Any(d => d.DeviceId == message.Device.DeviceId))
                Devices.Add(message.Device);
        });
    }

    public void Receive(DeviceLostMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.DeviceId == message.DeviceId);
            if (existing is not null) Devices.Remove(existing);
        });
    }
}
