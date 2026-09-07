using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using FileTransferApp.Services;
using FileTransferApp.ViewModels;

namespace FileTransferApp.Views;

public partial class MainView : UserControl
{
    // 响应式阈值（基于主流手机分辨率 dp）：
    //   竖屏手机 360-411dp 宽 < 600 → IsCompact (单列上下布局)
    //   横屏手机 640-900dp 宽 ≥ 600 但高 360-411dp < 500 → IsShort (两栏但压缩纵向 padding)
    //   平板/桌面 800+ × 500+ → 标准两栏
    private const double CompactWidthThreshold = 600.0;
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

        InitializeLanguageCombo();
        InitializeThemeCombo();
    }

    private void InitializeLanguageCombo()
    {
        if (LanguageCombo is null) return;
        LanguageCombo.ItemsSource = LocalizationService.SupportedLanguages
            .Select(l => l.DisplayName)
            .ToList();
        var current = LocalizationService.Instance.CurrentLanguage;
        var idx = Array.FindIndex(LocalizationService.SupportedLanguages, l => l.Code == current);
        LanguageCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo is null || LanguageCombo.SelectedIndex < 0) return;
        var lang = LocalizationService.SupportedLanguages[LanguageCombo.SelectedIndex];
        LocalizationService.Instance.SetLanguage(lang.Code);
    }

    private void InitializeThemeCombo()
    {
        if (ThemeCombo is null) return;
        // 监听语言切换：重新本地化主题选项文本
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        RefreshThemeComboItems();
        RefreshThemeSelection();
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.CurrentLanguage)) return;
        RefreshThemeComboItems();
        RefreshThemeSelection();
    }

    private void RefreshThemeComboItems()
    {
        ThemeCombo.ItemsSource = ThemeService.SupportedThemes
            .Select(t => LocalizationService.Instance[t.Key])
            .ToList();
    }

    private void RefreshThemeSelection()
    {
        var current = ThemeService.Instance.Current;
        var idx = Array.FindIndex(ThemeService.SupportedThemes, t => t.Value == current);
        ThemeCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo is null || ThemeCombo.SelectedIndex < 0) return;
        var option = ThemeService.SupportedThemes[ThemeCombo.SelectedIndex];
        ThemeService.Instance.Apply(option.Value);
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
        bool shouldBeCompact = width > 0 && width < CompactWidthThreshold;
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
        await vm.OpenFileCommand.ExecuteAsync(null);
    }
}
