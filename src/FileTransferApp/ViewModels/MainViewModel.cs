using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;

namespace FileTransferApp.ViewModels;

/// <summary>
/// 主视图模型：组合设备发现列表与传输任务列表，
/// 监听 TransferStatusChangedMessage 自动为任务创建 <see cref="TransferItemViewModel"/>。
/// 通过 <see cref="TransferKeepAliveCoordinator"/> 在首个活动任务出现时启动平台保活、
/// 最后一个活动任务终止时停止保活。
/// </summary>
public partial class MainViewModel : ObservableObject,
    IRecipient<TransferStatusChangedMessage>,
    IRecipient<TransferCompletedMessage>,
    IRecipient<TransferTaskRemovedMessage>
{
    private const string KeepAliveTitle = "FileTransferApp";
    private const string KeepAliveContent = "正在传输文件，保持服务运行";

    private readonly IMessenger _messenger;
    private readonly ITransferEngine _engine;
    private readonly IDiscoveryService _discovery;
    private readonly IFilePickerService _filePicker;
    private readonly TransferKeepAliveCoordinator _keepAlive;

    public DeviceListViewModel Devices { get; }
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();

    [ObservableProperty] public partial string SelfInfo { get; set; } = string.Empty;
    /// <summary>
    /// 响应式布局开关：窄屏(Android 竖屏/小窗口 < 600px)时为 true，
    /// 此时设备面板与传输面板从左右结构切换为上下结构，按钮文字/缩略图也紧凑展示。
    /// View 后台代码根据 Bounds.Width 变化写入此属性，XAML Styles 按属性驱动布局。
    /// </summary>
    [ObservableProperty] public partial bool IsCompact { get; set; }

    /// <summary>
    /// 矮屏开关：宽度≥600(横屏手机两栏)但高度<500(主流手机横屏 360-411dp)时为 true。
    /// 用于压缩顶栏/底栏/卡片纵向 padding，避免横屏手机 body 区域被顶底栏挤占。
    /// 与 IsCompact 互斥：IsCompact 优先(更窄约束)。
    /// 主流手机分辨率参考(dp)：
    ///   竖屏 360-411 × 640-900 → IsCompact=true,  IsShort=false
    ///   横屏 640-900 × 360-411 → IsCompact=false, IsShort=true
    ///   平板/桌面 800+ × 500+  → IsCompact=false, IsShort=false
    /// </summary>
    [ObservableProperty] public partial bool IsShort { get; set; }

    public MainViewModel(
        IMessenger messenger,
        ITransferEngine engine,
        IDiscoveryService discovery,
        IFilePickerService filePicker,
        IPlatformKeepAliveService keepAlive)
    {
        _messenger = messenger;
        _engine = engine;
        _discovery = discovery;
        _filePicker = filePicker;
        _keepAlive = new TransferKeepAliveCoordinator(keepAlive);
        Devices = new DeviceListViewModel(messenger, discovery);

        var self = _discovery.Self;
        // 显示本机所有可达的 LAN IPv4（排除 127/169.254/多播段），
        // 这样 Android 用户在 UDP 广播不可达（AP隔离/随机MAC）环境下可直接把 IP 告知对端手动输入。
        var lanIps = GetLanIPv4Addresses();
        var ipInfo = lanIps.Count > 0 ? $" · LAN IP: {string.Join(", ", lanIps)}" : "";
        SelfInfo = $"{self.DeviceName} ({self.DeviceType}) · 传输端口 {self.Port}{ipInfo}";
        // 构造完毕时设备列表手动选中逻辑：订阅设备列表变化，当有新设备出现时自动选中
        Devices.Devices.CollectionChanged += (_, e) =>
        {
            if (Devices.SelectedDevice is null && Devices.Devices.Count > 0)
                Devices.SelectedDevice = Devices.Devices[0];
        };

        _messenger.RegisterAll(this);
    }

    public void Receive(TransferStatusChangedMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureTransferItem(message.FileId);
            // 手动转发状态变更：防止 TransferItemViewModel 注册前的消息丢失（竞态）
            var item = Transfers.FirstOrDefault(t => t.FileId == message.FileId);
            item?.ApplyStateChange(message.NewState);
            // 委托协调器：根据本次状态变化同步保活开关（内部去重，避免重复调用平台 API）
            _keepAlive.OnTaskStateChanged(message.FileId, message.NewState, KeepAliveTitle, KeepAliveContent);
        });
    }

    public void Receive(TransferCompletedMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureTransferItem(message.FileId);
            // 手动转发完成事件：防止 TransferItemViewModel 注册前的消息丢失（竞态）
            var item = Transfers.FirstOrDefault(t => t.FileId == message.FileId);
            item?.ApplyCompleted(message.Success);
            _keepAlive.OnTaskCompleted(message.FileId, message.Success, KeepAliveTitle, KeepAliveContent);
        });
    }

    public void Receive(TransferTaskRemovedMessage message)
    {
        // 用户删除任务：从 ObservableCollection 中移除，UI 自动消失
        Dispatcher.UIThread.Post(() =>
        {
            var item = Transfers.FirstOrDefault(t => t.FileId == message.FileId);
            if (item is not null) Transfers.Remove(item);
        });
    }

    /// <summary>选择文件并发送至当前选中设备</summary>
    [RelayCommand]
    private async Task SendFileAsync()
    {
        var files = await _filePicker.PickFilesAsync();
        await SendFilesAsync(files);
    }

    /// <summary>将指定文件列表发送至当前选中设备（供文件选择器与拖拽区共用）</summary>
    public async Task SendFilesAsync(string[] files)
    {
        var device = Devices.SelectedDevice;
        if (device is null || device.IpAddress is null || files.Length == 0) return;

        // 不预启动保活：让真实任务的 TransferStatusChangedMessage 驱动 Coordinator。
        // 之前用 "__pending__" 占位会因占位任务永不终结导致 Coordinator 误判 HasActiveTasks=true。
        foreach (var file in files)
        {
            var task = await _engine.CreateSendTaskAsync(file, device);
            EnsureTransferItem(task.FileId);
            _ = _engine.StartSendAsync(task.FileId);
        }
    }

    /// <summary>手动直连：向目标 IP:Port 发送一次心跳握手，
    /// 成功后自动把新加入的设备选中，用户可直接点"选择文件发送"。
    /// 注意：端口应输入对方的【传输端口】=53318（HTTP监听端口），不是发现端口 53317。</summary>
    [RelayCommand]
    private async Task ManualConnectAsync()
    {
        if (!IPAddress.TryParse(Devices.ManualIp, out var ip)) return;
        if (!int.TryParse(Devices.ManualPort, out var port)) return;

        var before = Devices.Devices.Select(d => d.DeviceId).ToHashSet();
        var ok = await _discovery.ConnectDirectAsync(ip, ProtocolConstants.DiscoveryPort); // 心跳必须打 53317
        // 发送后等待 UdpDiscoveryService 心跳回写把节点注册到 Devices 列表（≤ 500ms 一次轮询）
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(200);
            var newly = Devices.Devices.FirstOrDefault(d => !before.Contains(d.DeviceId));
            if (newly is not null)
            {
                // 强制覆盖 Port 为对方传输端口（用户输入的，因为心跳里对方 DeviceNode.Port 也带的是 TransferPort=53318）
                if (newly.Port == 0) newly.Port = port;
                Devices.SelectedDevice = newly;
                break;
            }
        }
        // 兜底：如果对方心跳没回写成功（网络断/UDP不可达），直接注入一个"伪节点"
        // 这样用户依然可以直接点发送——我们构造一条最小合法 DeviceNode，IpAddress=手动输入
        if (Devices.SelectedDevice is null || !Devices.ManualIp.Equals(Devices.SelectedDevice.IpAddress?.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var fallback = new DeviceNode
            {
                DeviceId = "manual:" + Devices.ManualIp + ":" + port,
                DeviceName = $"手动直连 {Devices.ManualIp}:{port}",
                DeviceType = DeviceType.Unknown,
                IpAddress = ip,
                Port = port,
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                LastSeenUtc = DateTime.UtcNow,
            };
            Devices.Devices.Add(fallback);
            Devices.SelectedDevice = fallback;
        }
    }

    private void EnsureTransferItem(string fileId)
    {
        if (Transfers.Any(t => t.FileId == fileId)) return;
        var task = _engine.GetTask(fileId);
        if (task is null) return;
        Transfers.Insert(0, new TransferItemViewModel(_messenger, _engine, task));
    }

    /// <summary>枚举本机所有可达的 LAN IPv4（排除回环/APIPA/多播/链路本地/6to4）。
    /// 返回值用字符串（供 UI 直接展示），按以太网→WiFi 顺序。</summary>
    internal static List<string> GetLanIPv4Addresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is
                    System.Net.NetworkInformation.NetworkInterfaceType.Loopback or
                    System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;

                var props = ni.GetIPProperties();
                foreach (var uip in props.UnicastAddresses)
                {
                    var addr = uip.Address;
                    if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr)) continue;
                    var bytes = addr.GetAddressBytes();
                    // 过滤: 169.254.x.x (APIPA) , 224-239.x.x.x (多播/D类) , 240-255.x.x.x (E类保留) , 0.x.x.x
                    byte a = bytes[0];
                    if (a == 0 || a >= 224) continue;
                    if (a == 169 && bytes[1] == 254) continue;
                    result.Add(addr.ToString());
                }
            }
        }
        catch
        {
            // 平台异常(权限/部分网卡)不影响主流程
        }
        return result;
    }
}

