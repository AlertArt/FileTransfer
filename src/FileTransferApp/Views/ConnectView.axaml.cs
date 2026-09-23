using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using FileTransferApp.ViewModels;

namespace FileTransferApp.Views;

public partial class ConnectView : UserControl
{
    public ConnectView()
    {
        InitializeComponent();
    }

    /// <summary>复制本机连接码到剪贴板（供粘贴给对方）。</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectViewModel vm || string.IsNullOrEmpty(vm.SelfCode)) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(vm.SelfCode);
        }
        catch { /* 剪贴板不可用不影响主流程 */ }
    }
}
