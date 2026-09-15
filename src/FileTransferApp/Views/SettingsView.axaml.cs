using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FileTransferApp.Services;

namespace FileTransferApp.Views;

/// <summary>
/// 设置页：主题 / 多语言 / 导出日志 / 关于，通过 OverlayPane 全屏展示。
/// 日志、关于 的具体导航由外部订阅 LogsRequested / AboutRequested 处理，
/// 由 MainView 在 ShowOverlay 时挂接。
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>用户点击"导出日志 / 查看日志"。</summary>
    public event EventHandler? LogsRequested;

    /// <summary>用户点击"关于"。</summary>
    public event EventHandler? AboutRequested;

    public SettingsView()
    {
        InitializeComponent();
        InitializeLanguageCombo();
        InitializeThemeCombo();
        // 监听语言切换：实时刷新主题下拉项文案（跟随系统/浅色/深色随语言变化）
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    // ===================== 语言 =====================

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

    // ===================== 主题 =====================

    private void InitializeThemeCombo()
    {
        if (ThemeCombo is null) return;
        RefreshThemeComboItems();
        RefreshThemeSelection();
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo is null || ThemeCombo.SelectedIndex < 0) return;
        var option = ThemeService.SupportedThemes[ThemeCombo.SelectedIndex];
        ThemeService.Instance.Apply(option.Value);
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.CurrentLanguage)) return;
        RefreshThemeComboItems();
        RefreshThemeSelection();
    }

    private void RefreshThemeComboItems()
    {
        if (ThemeCombo is null) return;
        ThemeCombo.ItemsSource = ThemeService.SupportedThemes
            .Select(t => LocalizationService.Instance[t.Key])
            .ToList();
    }

    private void RefreshThemeSelection()
    {
        if (ThemeCombo is null) return;
        var current = ThemeService.Instance.Current;
        var idx = Array.FindIndex(ThemeService.SupportedThemes, t => t.Value == current);
        ThemeCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    // ===================== 导航 =====================

    private void OnLogsClick(object? sender, RoutedEventArgs e)
        => LogsRequested?.Invoke(this, EventArgs.Empty);

    private void OnAboutClick(object? sender, RoutedEventArgs e)
        => AboutRequested?.Invoke(this, EventArgs.Empty);
}
