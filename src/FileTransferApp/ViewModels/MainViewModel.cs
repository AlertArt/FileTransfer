using System.Collections.ObjectModel;
using System.IO;
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
    IRecipient<TransferProgressMessage>,
    IRecipient<TransferCompletedMessage>,
    IRecipient<TransferTaskRemovedMessage>
{
    private readonly IMessenger _messenger;
    private readonly ITransferEngine _engine;
    private readonly IDiscoveryService _discovery;
    private readonly IFilePickerService _filePicker;
    private readonly TransferKeepAliveCoordinator _keepAlive;
    private readonly ITransferHistoryStore _history;

    public DeviceListViewModel Devices { get; }
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();

    /// <summary>传输列表是否为空（供 XAML 空态提示切换）。</summary>
    public bool HasTransfers => Transfers.Count > 0;

    /// <summary>进行中（非终态）任务数。</summary>
    [ObservableProperty] public partial int ActiveCount { get; set; }
    /// <summary>是否存在进行中任务（控制"全部暂停/总进度"显隐）。</summary>
    [ObservableProperty] public partial bool HasActive { get; set; }
    /// <summary>是否存在已暂停任务（控制"全部恢复"显隐）。</summary>
    [ObservableProperty] public partial bool HasPaused { get; set; }
    /// <summary>批量总进度（按字节加权，0-100；仅供参考）。</summary>
    [ObservableProperty] public partial double OverallProgress { get; set; }
    private long _lastAggregateTickMs;

    [ObservableProperty] public partial string SelfName { get; set; } = string.Empty;
    /// <summary>本机设备类型（"PC"/"Android"等，供"关于"页展示）</summary>
    [ObservableProperty] public partial string SelfTypeText { get; set; } = string.Empty;
    /// <summary>本机传输端口（供"关于"页展示）</summary>
    [ObservableProperty] public partial int SelfPort { get; set; }
    /// <summary>本机 LAN IP（供"关于"页展示；多网卡时逗号分隔）</summary>
    [ObservableProperty] public partial string IpText { get; set; } = string.Empty;
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
        IPlatformKeepAliveService keepAlive,
        IPairingService pairing,
        ITransferHistoryStore history)
    {
        _messenger = messenger;
        _engine = engine;
        _discovery = discovery;
        _filePicker = filePicker;
        _keepAlive = new TransferKeepAliveCoordinator(keepAlive);
        _history = history;
        Devices = new DeviceListViewModel(messenger, discovery, pairing);
        Transfers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTransfers));

        var self = _discovery.Self;
        // 记录本机 LAN IPv4（排除 127/169.254/多播段），供"关于"页展示；
        // 用户在 UDP 广播不可达（AP隔离/随机MAC）环境下可把 IP 告知对端手动输入直连。
        var lanIps = GetLanIPv4Addresses();
        _selfName = self.DeviceName;
        _selfType = self.DeviceType.ToString();
        _selfPort = self.Port;
        SelfName = _selfName;
        SelfTypeText = _selfType;
        SelfPort = _selfPort;
        IpText = lanIps.Count > 0 ? string.Join(", ", lanIps) : _selfName;
        // 构造完毕时设备列表手动选中逻辑：订阅设备列表变化，当有新设备出现时自动选中
        Devices.Devices.CollectionChanged += (_, e) =>
        {
            if (Devices.SelectedDevice is null && Devices.Devices.Count > 0)
                Devices.SelectedDevice = Devices.Devices[0];
        };

        _messenger.RegisterAll(this);
    }

    private string _selfName = "";
    private string _selfType = "";
    private int _selfPort;

    public void Receive(TransferStatusChangedMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureTransferItem(message.FileId);
            // 手动转发状态变更：防止 TransferItemViewModel 注册前的消息丢失（竞态）
            var item = Transfers.FirstOrDefault(t => t.FileId == message.FileId);
            item?.ApplyStateChange(message.NewState);
            // 委托协调器：根据本次状态变化同步保活开关（内部去重，避免重复调用平台 API）
            _keepAlive.OnTaskStateChanged(message.FileId, message.NewState,
                LocalizationService.Instance.GetString("KeepAliveTitle"),
                LocalizationService.Instance.GetString("KeepAliveContent"));
            RefreshAggregate();
        });
    }

    /// <summary>实时进度 → 刷新批量聚合（限频 500ms，避免高频重算）。</summary>
    public void Receive(TransferProgressMessage message)
    {
        var now = Environment.TickCount64;
        if (now - _lastAggregateTickMs < 500) return;
        _lastAggregateTickMs = now;
        Dispatcher.UIThread.Post(RefreshAggregate);
    }

    /// <summary>重算"进行中数量 / 总进度 / 暂停态"聚合（供标题栏展示与按钮显隐）。</summary>
    private void RefreshAggregate()
    {
        long total = 0, done = 0;
        var active = 0;
        var paused = 0;
        foreach (var t in Transfers)
        {
            if (t.IsTerminal) continue;
            active++;
            if (t.IsPaused) paused++;
            total += t.TotalBytes;
            done += Math.Min(t.BytesTransferred, t.TotalBytes);
        }
        ActiveCount = active;
        HasActive = active > 0;
        HasPaused = paused > 0;
        OverallProgress = total > 0 ? done * 100.0 / total : 0;
    }

    /// <summary>全部暂停（对所有进行中的任务）。</summary>
    [RelayCommand]
    private async Task PauseAllAsync()
    {
        foreach (var t in Transfers.ToList())
            if (t.IsRunning) await _engine.PauseAsync(t.FileId).ConfigureAwait(false);
    }

    /// <summary>全部恢复（对所有已暂停的任务）。</summary>
    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        foreach (var t in Transfers.ToList())
            if (t.IsPaused) await _engine.ResumeAsync(t.FileId).ConfigureAwait(false);
    }

    public void Receive(TransferCompletedMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureTransferItem(message.FileId);
            // 手动转发完成事件：防止 TransferItemViewModel 注册前的消息丢失（竞态）
            var item = Transfers.FirstOrDefault(t => t.FileId == message.FileId);
            item?.ApplyCompleted(message.Success);
            _keepAlive.OnTaskCompleted(message.FileId, message.Success,
                LocalizationService.Instance.GetString("KeepAliveTitle"),
                LocalizationService.Instance.GetString("KeepAliveContent"));
            PersistHistory(message.FileId);
            RefreshAggregate();
        });
    }

    /// <summary>把终态任务写入历史存储（App 重启后仍可在"传输历史"中查看）。</summary>
    private void PersistHistory(string fileId)
    {
        var t = _engine.GetTask(fileId);
        if (t is null) return;
        _history.AddOrUpdate(new TransferHistoryEntry
        {
            FileId = t.FileId,
            FileName = t.FileName,
            Direction = t.Direction,
            TotalBytes = t.TotalBytes,
            BytesTransferred = t.BytesTransferred,
            State = t.State,
            LocalPath = t.LocalPath,
            PeerName = t.Peer?.DeviceName,
            StartedUtc = t.StartedUtc,
            EndedUtc = t.EndedUtc,
            ErrorMessage = t.ErrorMessage,
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

    /// <summary>发送剪贴板/任意文本：写入临时 .txt 文件后走正常文件传输（对端收到一个文本文件）。</summary>
    public async Task SendTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, text);
            await SendFilesAsync(new[] { path });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"FTA.FILE: SendTextAsync FAIL: {ex.Message}");
        }
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
            var task = await _engine.CreateSendTaskAsync(file, device.Device);
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
                if (newly.Port == 0) newly.Device.Port = port;
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
                DeviceName = LocalizationService.Instance.Format("ManualConnectName", Devices.ManualIp, port),
                DeviceType = DeviceType.Unknown,
                IpAddress = ip,
                Port = port,
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                LastSeenUtc = DateTime.UtcNow,
            };
            Devices.Devices.Add(new DeviceNodeViewModel(fallback));
            Devices.SelectedDevice = Devices.Devices[^1];
        }
    }

    private void EnsureTransferItem(string fileId)
    {
        if (Transfers.Any(t => t.FileId == fileId)) return;
        var task = _engine.GetTask(fileId);
        if (task is null) return;
        Transfers.Insert(0, new TransferItemViewModel(_messenger, _engine, task));
        RefreshAggregate();
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

