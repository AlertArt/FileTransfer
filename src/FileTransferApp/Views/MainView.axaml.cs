using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    /// <summary>底栏齿轮：打开设置页（主题/语言/日志/关于 统一入口）。</summary>
    private void OnOpenSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsView is null)
        {
            _settingsView = new SettingsView();
            // 设置页内的"日志 / 关于"子入口，导航到对应子页面
            _settingsView.LogsRequested += (_, _) => OpenLogs();
            _settingsView.AboutRequested += (_, _) => OpenAbout();
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
