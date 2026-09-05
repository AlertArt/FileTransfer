using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Services;

/// <summary>
/// 桌面端弹窗审批服务：以模态 Window 形式询问用户是否接收对端发来的文件，
/// 展示文件名、大小、SHA256 摘要与缩略图。设计文档"等待对方同意"语义的桌面端落地。
/// 移动端无主窗口时回退自动接受，待后续接入原生 UIViewController/BerDialog。
/// </summary>
public sealed class DialogTransferApprovalService : ITransferApprovalService
{
    private readonly IStorageService _storage;

    public DialogTransferApprovalService(IStorageService storage) => _storage = storage;

    public async Task<(bool Accepted, string? SavePath)> RequestApprovalAsync(FileMetadata metadata, string peerName)
    {
        // 移动端无 IClassicDesktopStyleApplicationLifetime，回退自动接受
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return AutoApprove(metadata);
        }

        var owner = desktop.MainWindow;
        bool accepted = false;

        // 调用方位于 HTTP 服务器线程，必须切回 UI 线程构造并显示窗口
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            accepted = await ShowApprovalDialog(owner, metadata, peerName);
        });

        if (!accepted) return (false, null);
        return AutoApprove(metadata);
    }

    private (bool, string?) AutoApprove(FileMetadata metadata)
    {
        // 返回 null 让 IStorageService.OpenWriteStreamAsync 自行拼接收目录 + 清洗文件名，
        // 避免传入完整路径导致 Sanitize 将路径分隔符替换为下划线。
        return (true, null);
    }

    private static async Task<bool> ShowApprovalDialog(Window owner, FileMetadata metadata, string peerName)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dlg = new Window
        {
            Title = "接收文件请求",
            Width = 380,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = true
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10
        };

        panel.Children.Add(new TextBlock
        {
            Text = $"来自：{peerName}",
            FontWeight = FontWeight.SemiBold,
            FontSize = 14
        });
        panel.Children.Add(new TextBlock { Text = $"文件名：{metadata.FileName}" });
        panel.Children.Add(new TextBlock { Text = $"大小：{Core.Services.Impl.SpeedFormatter.FormatSize(metadata.FileSize)}" });
        if (!string.IsNullOrEmpty(metadata.Sha256))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"SHA256：{metadata.Sha256[..Math.Min(16, metadata.Sha256.Length)]}…",
                FontFamily = FontFamily.Parse("Cascadia Mono,Consolas,Courier New"),
                FontSize = 11,
                Opacity = 0.6
            });
        }

        // 缩略图预览（图片类文件）
        if (!string.IsNullOrEmpty(metadata.ThumbnailBase64))
        {
            try
            {
                var bytes = Convert.FromBase64String(metadata.ThumbnailBase64);
                using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                panel.Children.Add(new Image
                {
                    Source = bmp,
                    Width = 96,
                    Height = 96,
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            catch { /* ignore thumbnail errors */ }
        }

        // 按钮
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var acceptBtn = new Button
        {
            Content = "接收",
            Padding = new Thickness(20, 6),
            FontWeight = FontWeight.SemiBold
        };
        var rejectBtn = new Button
        {
            Content = "拒绝",
            Padding = new Thickness(20, 6)
        };
        btnPanel.Children.Add(rejectBtn);
        btnPanel.Children.Add(acceptBtn);
        panel.Children.Add(btnPanel);

        dlg.Content = panel;

        acceptBtn.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            dlg.Close();
        };
        rejectBtn.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            dlg.Close();
        };
        dlg.Closing += (_, e) =>
        {
            // 用户用系统关闭按钮（X）退出时，按拒绝处理
            if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
        };

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }
}
