using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.ViewModels;

/// <summary>
/// 设备列表视图模型。监听 DeviceDiscoveredMessage / DeviceLostMessage，
/// 在 UI 线程维护 ObservableCollection，支持手动直连。
/// 同时维护各设备的配对状态徽标（🔒/🔓），提供解除配对入口；
/// 传输完成后自动按配对记录刷新状态（首次发送会自动完成配对）。
/// </summary>
public partial class DeviceListViewModel : ObservableObject,
    IRecipient<DeviceDiscoveredMessage>,
    IRecipient<DeviceUpdatedMessage>,
    IRecipient<DeviceLostMessage>,
    IRecipient<TransferCompletedMessage>
{
    private readonly IMessenger _messenger;
    private readonly IDiscoveryService _discovery;
    private readonly IPairingService? _pairing;

    public ObservableCollection<DeviceNodeViewModel> Devices { get; } = new();

    /// <summary>设备列表是否为空（供 XAML 空态提示切换）。</summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>设备列表是否为空（正向属性，供 XAML 空态提示切换）。</summary>
    public bool IsEmpty => Devices.Count == 0;

    [ObservableProperty] public partial DeviceNodeViewModel? SelectedDevice { get; set; }
    [ObservableProperty] public partial string ManualIp { get; set; } = string.Empty;
    /// <summary>手动直连端口：应该是对方的【传输端口】(默认 53318 = ProtocolConstants.TransferPort)，
    /// 不是发现端口(53317)。UDP 发现端口绑定在对方的 IDiscoveryService 上，它只收心跳；
    /// 而真实的 /prepare /chunk HTTP 请求必须打到对方的 ITransferServer 端口。</summary>
    [ObservableProperty] public partial string ManualPort { get; set; } = "53318";

    public DeviceListViewModel(IMessenger messenger, IDiscoveryService discovery, IPairingService? pairing = null)
    {
        _messenger = messenger;
        _discovery = discovery;
        _pairing = pairing;
        foreach (var d in _discovery.Devices) Devices.Add(new DeviceNodeViewModel(d));
        Devices.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(IsEmpty)); };
        _messenger.RegisterAll(this);
        RefreshPairing();
    }

    /// <summary>按配对记录刷新全部节点的配对状态（初始化 / 传输完成 / 解除配对后调用）。</summary>
    public void RefreshPairing()
    {
        var paired = _pairing?.GetAllPairRecords().Select(r => r.PeerDeviceId).ToHashSet()
                     ?? new HashSet<string>();
        foreach (var d in Devices) d.IsPaired = paired.Contains(d.DeviceId);
    }

    /// <summary>解除与指定设备的配对（仅需点一下锁形徽标）。</summary>
    [RelayCommand]
    private void Unpair(DeviceNodeViewModel device)
    {
        _pairing?.Unpair(device.DeviceId);
        RefreshPairing();
    }

    public void Receive(DeviceDiscoveredMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!Devices.Any(d => d.DeviceId == message.Device.DeviceId))
            {
                Devices.Add(new DeviceNodeViewModel(message.Device)
                {
                    IsPaired = _pairing?.GetAllPairRecords().Any(r => r.PeerDeviceId == message.Device.DeviceId) == true,
                });
            }
        });
    }

    public void Receive(DeviceUpdatedMessage message)
    {
        // 已知设备 IP/端口/名称变化 → 就地刷新，避免列表显示陈旧地址
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.DeviceId == message.Device.DeviceId);
            existing?.ApplyUpdate(message.Device);
        });
    }

    /// <summary>
    /// 添加（或就地刷新）一个手动/二维码导入的设备节点并选中它。
    /// 供"手动直连兜底"与"连接码导入"复用；已存在则刷新地址并选中。
    /// </summary>
    public void AddOrSelectManual(DeviceNode node)
    {
        var existing = Devices.FirstOrDefault(d => d.DeviceId == node.DeviceId);
        if (existing is null)
        {
            existing = new DeviceNodeViewModel(node);
            Devices.Add(existing);
        }
        else
        {
            existing.ApplyUpdate(node);
        }
        SelectedDevice = existing;
    }

    public void Receive(DeviceLostMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.DeviceId == message.DeviceId);
            if (existing is not null) Devices.Remove(existing);
        });
    }

    public void Receive(TransferCompletedMessage message)
    {
        // 首次传输会触发自动配对；完成后刷新徽标，让 🔓→🔒 即时生效
        Dispatcher.UIThread.Post(RefreshPairing);
    }
}
