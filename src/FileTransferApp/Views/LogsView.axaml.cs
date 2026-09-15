using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using FileTransferApp.ViewModels;

namespace FileTransferApp.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm || string.IsNullOrEmpty(vm.LogText)) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is { } cb)
            {
                await cb.SetTextAsync(vm.LogText);
                ShowCopyHint();
            }
        }
        catch (Exception ex)
        {
            // 剪贴板不可用（部分安卓 ROM）时静默失败
            System.Diagnostics.Trace.WriteLine($"FTA.LOG: copy FAIL: {ex.Message}");
        }
    }

    private async void ShowCopyHint()
    {
        if (CopyHint is null) return;
        CopyHint.IsVisible = true;
        try
        {
            await Task.Delay(1500);
        }
        catch
        {
            // ignore
        }
        if (CopyHint is not null) CopyHint.IsVisible = false;
    }
}