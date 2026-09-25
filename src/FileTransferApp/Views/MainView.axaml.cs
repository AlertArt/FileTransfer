using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using FileTransferApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.Views;

public partial class MainView : UserControl
{
    // 响应式阈值（基于主流手机分辨率 dp）：
    //   竖屏手机/窄屏 360-600dp 宽 ≤ 620 → IsCompact (单列上下布局)
    //   平板/桌面 640+ × 500+ → 标准两栏
    private const double CompactWidthThreshold = 620.0;
    private const double ShortHeightThreshold = 500.0;
    // 记录上次应用的模式，避免 LayoutUpdated 重复触发
    private bool _lastAppliedCompact;
    private bool _lastAppliedShort;
    private bool _initialized;
    private IInsetsManager? _insetsManager;

    public MainView()
    {
        InitializeComponent();
        // 注册拖放完成事件：接收文件并发送至选中设备
        AddHandler(DragDrop.DropEvent, OnDrop);
        // 监听控件尺寸变化：驱动响应式布局
        LayoutUpdated += MainView_LayoutUpdated;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // DataContext 就绪后立即根据当前宽高计算一次响应式状态
        ApplyResponsiveMode(Bounds.Width, Bounds.Height);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 【关键·根因】看 Avalonia 源码 TopLevel.InvalidateChildInsetsPadding()：
        //   if (Content is Control child && ...)
        //       if (child.GetValue(AutoSafeAreaPaddingProperty))
        //           child.SetValue(PaddingProperty, insetsManager.SafeAreaPadding, Style);
        // 即该附着属性是**从 TopLevel 的内容(MainView)读取**，并把安全区作为**内容的 Padding** 施加。
        // 之前设在 TopLevel 上完全无效 → Avalonia 始终给 MainView 加了一份 safe.Top 的 Padding，
        // 又与下面手动给 TopBarContentColumn 垫的 safe.Top margin 叠加 → "双倍安全区"，
        // 顶栏因此异常高，且怎么调内容尺寸都看不出变化。必须设在 this（MainView 即 TopLevel.Content）上。
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            TopLevel.SetAutoSafeAreaPadding(this, false);
            // 兜底：Android 上给 TopLevel 铺品牌底色，任何未被内容覆盖的缝隙（含状态栏后）都是紫色而非黑。
            if (OperatingSystem.IsAndroid() && topLevel is ContentControl topControl)
                topControl.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6366F1"));
        }

        // 手动接管安全区：监听 InsetsManager 把顶部/底部安全区垫到顶栏/底栏的内容层，
        // 让紫色头部背景真正延伸到屏幕边缘，同时内容（Logo/标题/按钮）避开状态栏与手势条。
        if (topLevel?.InsetsManager is { } insets)
        {
            _insetsManager = insets;
            // 关键：Android 上 DisplaysEdgeToEdge 默认 false → SafeAreaPadding 恒为 0，
            // 且 AndroidInsetsManager 构造时会以 SetDecorFitsSystemWindows(true) 覆盖掉
            // MainActivity.OnCreate 里设置的 false，导致应用没有真正延伸到状态栏后、
            // 顶部留下一段系统栏空白。显式开启 edge-to-edge 后：
            //   1) 状态栏区域透明，紫色顶栏背景可铺满到物理屏幕顶边；
            //   2) SafeAreaPadding 返回真实内边距，顶栏内容能避让状态栏/刘海。
            insets.DisplayEdgeToEdgePreference = true;
            ApplySafeAreaPadding(insets.SafeAreaPadding);
            insets.SafeAreaChanged += OnSafeAreaChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insetsManager is not null)
        {
            _insetsManager.SafeAreaChanged -= OnSafeAreaChanged;
            _insetsManager = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
        => ApplySafeAreaPadding(e.SafeAreaPadding);

    /// <summary>
    /// 把安全区内边距应用到顶栏/底栏/覆盖面板的内容层（而非整棵布局树）。
    /// 这样紫色顶栏 Background 铺满到屏幕最顶，文字/按钮在状态栏/刘海之下；底栏内容避开手势条。
    /// </summary>
    private void ApplySafeAreaPadding(Thickness safe)
    {
        if (TopBarContentColumn is not null)
            TopBarContentColumn.Margin = new Thickness(0, safe.Top + 0, 0, 0);
        if (BottomBarContentColumn is not null)
            BottomBarContentColumn.Margin = new Thickness(0, 0, 0, safe.Bottom);
        // 注意：安全区只垫到**顶栏内容列**（紫色底铺到屏幕最顶 = 品牌延伸），
        // 顶栏 Border 本身保持 Auto 高度由内容决定 —— 若这里把预算成让顶栏
        // Border 长高，就会产生"紫色大块向下挤压、全部 UI 下移"。
        if (OverlayHeaderGrid is not null)
            OverlayHeaderGrid.Margin = new Thickness(
                OverlayHeaderGrid.Margin.Left,
                12 + safe.Top,
                OverlayHeaderGrid.Margin.Right,
                OverlayHeaderGrid.Margin.Bottom);
        // 覆盖面板（日志/设置/关于）底部内容也避让手势条 / 导航栏，避免被系统白条或手势区遮挡
        if (OverlayContent is not null)
            OverlayContent.Margin = new Thickness(
                OverlayContent.Margin.Left,
                OverlayContent.Margin.Top,
                OverlayContent.Margin.Right,
                14 + safe.Bottom);
    }

    private void MainView_LayoutUpdated(object? sender, EventArgs e)
    {
        ApplyResponsiveMode(Bounds.Width, Bounds.Height);
    }

    private void ApplyResponsiveMode(double width, double height)
    {
        if (DataContext is not MainViewModel vm) return;
        bool shouldBeCompact = width > 0 && width <= CompactWidthThreshold;
        // 矮屏(横屏手机)：宽度足够两栏但高度不足，需压缩顶栏/底栏纵向尺寸
        // 与 IsCompact 互斥：IsCompact 优先(更窄约束，单列布局已能容纳)
        bool shouldBeShort = !shouldBeCompact && height > 0 && height < ShortHeightThreshold;
        if (_initialized && _lastAppliedCompact == shouldBeCompact && _lastAppliedShort == shouldBeShort) return;
        _initialized = true;
        _lastAppliedCompact = shouldBeCompact;
        _lastAppliedShort = shouldBeShort;
        vm.IsCompact = shouldBeCompact;
        vm.IsShort = shouldBeShort;
        UpdateLayoutForCompact(shouldBeCompact);
    }

    /// <summary>
    /// 在紧凑 (窄屏) 和标准 (宽屏) 模式之间切分布局结构：
    ///   标准 (宽屏)：设备面板 (列0,行0) / 传输面板 (列1,行0) —— 左右两栏
    ///   紧凑 (窄屏)：设备面板 (列0,行0) / 传输面板 (列0,行1) —— 上下两栏，自动铺满
    /// </summary>
    private void UpdateLayoutForCompact(bool isCompact)
    {
        if (LayoutRoot is null || DevicesPanel is null || TransfersPanel is null) return;

        if (isCompact)
        {
            // 紧凑：一列，设备在上 Auto，传输在下 *
            LayoutRoot.ColumnDefinitions = new ColumnDefinitions("*");
            LayoutRoot.RowDefinitions = new RowDefinitions("Auto,*");
            Grid.SetColumn(DevicesPanel, 0);
            Grid.SetRow(DevicesPanel, 0);
            Grid.SetColumn(TransfersPanel, 0);
            Grid.SetRow(TransfersPanel, 1);

            // 紧凑模式：限制设备面板高度，给传输列表留空间；上边距/下边距
            DevicesPanel.MaxHeight = 260;
            DevicesPanel.Margin = new Thickness(0, 0, 0, 6);
        }
        else
        {
            // 标准：两列 (设备=左 320px，传输=右 *)
            LayoutRoot.ColumnDefinitions = new ColumnDefinitions("320,*");
            LayoutRoot.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(DevicesPanel, 0);
            Grid.SetRow(DevicesPanel, 0);
            Grid.SetColumn(TransfersPanel, 1);
            Grid.SetRow(TransfersPanel, 0);
            DevicesPanel.MaxHeight = double.PositiveInfinity;
            DevicesPanel.Margin = new Thickness(0, 0, 6, 0);
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var paths = new List<string>();
        var files = e.DataTransfer.TryGetFiles();
        if (files is not null)
        {
            foreach (var f in files)
            {
                if (f.Path.IsFile) paths.Add(f.Path.LocalPath);
            }
        }

        e.Handled = true;
        if (paths.Count > 0)
            await vm.SendFilesAsync(paths.ToArray());
    }

    /// <summary>双击已完成的接收卡片：使用系统默认程序打开已保存的文件</summary>
    private async void TransferCard_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not Visual visual) return;
        if (visual.DataContext is not TransferItemViewModel vm) return;
        if (!vm.CanOpenFile || string.IsNullOrEmpty(vm.LocalPath)) return;
        e.Handled = true;
        try
        {
            await vm.OpenFileCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // async void 处理器内任何异常外泄都会导致整个进程闪退（Android），必须全拦
            System.Diagnostics.Trace.WriteLine($"FTA.FILE: DoubleTapped open FAIL: {ex}");
        }
    }

    // ===================== 设置 / 日志 / 关于 覆盖面板 =====================

    private LogsView? _logsView;
    private AboutView? _aboutView;
    private SettingsView? _settingsView;
    private HistoryView? _historyView;
    private PairingView? _pairingView;

    /// <summary>底栏齿轮：打开设置页（主题/语言/日志/关于 统一入口）。</summary>
    private void OnOpenSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsView is null)
        {
            _settingsView = new SettingsView();
            // 设置页内的"传输历史 / 日志 / 关于"子入口，导航到对应子页面
            _settingsView.LogsRequested += (_, _) => OpenLogs();
            _settingsView.AboutRequested += (_, _) => OpenAbout();
            _settingsView.HistoryRequested += (_, _) => OpenHistory();
            _settingsView.PairingRequested += (_, _) => OpenPairing();
        }
        ShowOverlay(LocalizationService.Instance.GetString("SettingsTitle"), _settingsView);
    }

    private void OpenLogs()
    {
        var vm = ServiceLocator.Services.GetService<LogsViewModel>();
        if (vm is null) return;
        vm.Load();
        if (_logsView is null) _logsView = new LogsView();
        _logsView.DataContext = vm;
        ShowOverlay(LocalizationService.Instance.GetString("LogsTitle"), _logsView);
    }

    private void OpenAbout()
    {
        if (_aboutView is null) _aboutView = new AboutView();
        ShowOverlay(LocalizationService.Instance.GetString("AboutTitle"), _aboutView);
    }

    /// <summary>设置页"传输历史"子入口：展示持久化的终态任务快照。</summary>
    private void OpenHistory()
    {
        var vm = ServiceLocator.Services.GetService<HistoryViewModel>();
        if (vm is null) return;
        vm.Load();
        _historyView ??= new HistoryView();
        _historyView.DataContext = vm;
        ShowOverlay(LocalizationService.Instance.GetString("HistoryTitle"), _historyView);
    }

    /// <summary>设置页"配对管理"子入口：集中展示已配对设备，可逐个/全部解除。</summary>
    private void OpenPairing()
    {
        var vm = ServiceLocator.Services.GetService<PairingViewModel>();
        if (vm is null) return;
        vm.Load();
        _pairingView ??= new PairingView();
        _pairingView.DataContext = vm;
        ShowOverlay(LocalizationService.Instance.GetString("PairingTitle"), _pairingView);
    }

    /// <summary>发送剪贴板文本：读剪贴板 → 写临时 .txt → 走正常传输发送给当前选中设备。</summary>
    private async void OnSendClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return;
            await vm.SendTextAsync(text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"FTA.FILE: send clipboard FAIL: {ex.Message}");
        }
    }

    /// <summary>附近设备标题栏的"连接码"入口：展示本机二维码，并支持粘贴导入对方连接码。</summary>
    private void OnOpenConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var discovery = ServiceLocator.Services?.GetService<IDiscoveryService>();
        if (discovery is null) return;
        var selfIp = MainViewModel.GetLanIPv4Addresses().FirstOrDefault() ?? string.Empty;
        var loc = ServiceLocator.GetService<ILocalizationService>() ?? LocalizationService.Instance;
        var view = new ConnectView
        {
            DataContext = new ConnectViewModel(vm.Devices, discovery.Self, selfIp, loc),
        };
        ShowOverlay(LocalizationService.Instance.GetString("Connect.Title"), view);
    }

    private void OnCloseOverlayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OverlayPane.IsVisible = false;
        OverlayContent.Content = null;
    }

    private void ShowOverlay(string title, Control content)
    {
        OverlayTitle.Text = title;
        OverlayContent.Content = content;
        OverlayPane.IsVisible = true;
    }
}
