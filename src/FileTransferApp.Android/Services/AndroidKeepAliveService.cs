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
        // Android 采用「常驻前台服务」策略：任务结束**不停止服务**，仅把通知切回空闲文案。
        // 若在这里 StopService，进程会在传输结束后失去前台保护，被系统冻结/回收后
        // UDP/HTTP 监听一并失效 → 对端再也发现不了本机、本机也发现不了对端（"断联后连不上"）。
        try
        {
            TransferForegroundService.UpdateContent(_context, DefaultTitle, DefaultContent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"更新空闲通知失败: {ex.Message}");
        }
    }

    /// <summary>刷新通知文案 + 进度条（不重启服务，避免频闪）。progress 为 0..1，null 表示不显示进度条。</summary>
    public void UpdateKeepAlive(string title, string content, double? progress)
    {
        var percent = progress is { } p ? (int)Math.Round(p * 100) : -1;
        TransferForegroundService.UpdateContent(_context, title, content, percent);
    }

    /// <summary>传输完成 / 失败：投放一次性状态通知（自动消失）。</summary>
    public void ShowStatusNotification(string title, string content)
        => TransferForegroundService.ShowStatusNotification(_context, title, content);
}
