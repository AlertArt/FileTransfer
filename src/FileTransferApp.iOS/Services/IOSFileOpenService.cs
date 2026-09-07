using Foundation;
using UIKit;

namespace FileTransferApp.iOS.Services;

/// <summary>
/// iOS 文件打开：通过 UIApplication.OpenUrl 使用系统关联程序打开已接收的文件。
/// 适用场景：PDF/图片/视频/Office 文档等均可被 iOS 关联应用打开。
/// </summary>
public sealed class IOSFileOpenService : FileTransferApp.Services.IFileOpenService
{
    public Task<bool> OpenFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.FromResult(false);

        try
        {
            var nsUrl = NSUrl.FromFilename(filePath);
            var options = new UIApplicationOpenUrlOptions();
            var tcs = new TaskCompletionSource<bool>();
            UIApplication.SharedApplication.OpenUrl(nsUrl, options, opened =>
            {
                tcs.TrySetResult(opened);
            });
            return tcs.Task;
        }
        catch
        {
            // 无关联程序 / 文件损坏 → 静默失败
            return Task.FromResult(false);
        }
    }
}
