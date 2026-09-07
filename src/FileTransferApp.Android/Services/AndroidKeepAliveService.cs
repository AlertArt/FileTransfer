using Android.Content;
using Android.OS;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;

namespace FileTransferApp.Android.Services;

/// <summary>
/// Android 平台前台保活服务实现。
/// 启动 <see cref="TransferForegroundService"/> 持久通知保活进程；
/// StopKeepAlive 时停止前台服务释放通知。
/// </summary>
public sealed class AndroidKeepAliveService : IPlatformKeepAliveService
{
    // 兜底文案：调用方传空时才使用，按当前语言动态解析
    private static string DefaultTitle => LocalizationService.Instance.GetString("KeepAliveTitle");
    private static string DefaultContent => LocalizationService.Instance.GetString("KeepAliveIdle");

    private readonly Context _context;
    // 平台无关的状态机：跟踪 Running / 文案回退 / 提示去重
    internal readonly KeepAliveStatus Status = new();

    public AndroidKeepAliveService(Context context)
    {
        _context = context;
    }

    public void StartKeepAlive(string title, string content)
    {
        // 委托共享状态机做文案回退与去重判定
        var result = Status.Start(title, content, DefaultTitle, DefaultContent);

        var intent = new Intent(_context, typeof(TransferForegroundService));
        intent.PutExtra(TransferForegroundService.ExtraTitle, result.EffectiveTitle);
        intent.PutExtra(TransferForegroundService.ExtraContent, result.EffectiveContent);

        try
        {
            // Android 8.0+ 必须使用 StartForegroundService 显式启动 FG 服务
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                _context.StartForegroundService(intent);
            else
#pragma warning disable CA1416 // 旧 API 等价调用
                _context.StartService(intent);
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"启动前台保活服务失败: {ex.Message}");
        }
    }

    public void StopKeepAlive()
    {
        Status.Stop();
        try
        {
            _context.StopService(new Intent(_context, typeof(TransferForegroundService)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"停止前台保活服务失败: {ex.Message}");
        }
    }

    /// <summary>更新通知文案（不重启服务，避免频闪）</summary>
    public void UpdateNotification(string title, string content)
        => TransferForegroundService.UpdateContent(_context, title, content);
}
