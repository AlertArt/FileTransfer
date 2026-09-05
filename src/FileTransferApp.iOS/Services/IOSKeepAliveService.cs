using CoreFoundation;
using Foundation;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using UIKit;

namespace FileTransferApp.iOS.Services;

/// <summary>
/// iOS 平台前台保活服务实现。
/// iOS 严限制后台进程，无法如 Android 那样常驻前台服务；策略为：
/// 1) 禁用屏幕闲置定时器，防止传输中熄屏导致进程冻结；
/// 2) 弹出一次性提示，建议用户保持应用在前台；
/// 3) 启动后台任务标识，争取熄屏后的有限后台执行时间（最长 30s）。
/// </summary>
public sealed class IOSKeepAliveService : IPlatformKeepAliveService
{
    private const string DefaultTitle = "FileTransferApp";
    private const string DefaultContent = "请保持应用在前台以确保文件传输不被中断";

    private nint? _backgroundTaskId;
    private bool _idleTimerOriginallyDisabled;
    // 平台无关的状态机：跟踪 Running / 文案回退 / 提示去重
    internal readonly KeepAliveStatus Status = new();

    public void StartKeepAlive(string title, string content)
    {
        // 委托共享状态机做文案回退与去重判定
        var result = Status.Start(title, content, DefaultTitle, DefaultContent);

        var app = UIApplication.SharedApplication;

        // 新周期首次进入运行态：记录原始 IdleTimer 值并禁用熄屏
        // .NET for iOS 与 UIKit 一致使用 IdleTimerDisabled（true = 禁止熄屏）
        if (result.ShouldShowPrompt)
        {
            _idleTimerOriginallyDisabled = app.IdleTimerDisabled;
            app.IdleTimerDisabled = true;
        }

        // 申请后台执行时间窗（用户熄屏后仍可短暂完成当前切片）
        if (_backgroundTaskId is null)
        {
            _backgroundTaskId = app.BeginBackgroundTask(() =>
            {
                // 系统即将终止任务，清理状态
                EndBackgroundTask();
            });
        }

        // 一次性提示用户保持前台（避免每次进度更新都弹窗）
        if (result.ShouldShowPrompt)
        {
            ShowKeepAliveAlert(result.EffectiveTitle, result.EffectiveContent);
        }
    }

    public void StopKeepAlive()
    {
        Status.Stop();
        var app = UIApplication.SharedApplication;
        app.IdleTimerDisabled = _idleTimerOriginallyDisabled;
        EndBackgroundTask();
    }

    private void EndBackgroundTask()
    {
        if (_backgroundTaskId is not { } id) return;
        try { UIApplication.SharedApplication.EndBackgroundTask(id); }
        catch { /* ignore */ }
        _backgroundTaskId = null;
    }

    /// <summary>在当前最上层 ViewController 上展示一次性 UIAlertController</summary>
    private static void ShowKeepAliveAlert(string title, string content)
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            var vc = UIApplication.SharedApplication.KeyWindow?.RootViewController;
            if (vc is null) return;
            // 取最上层 presented VC，避免被模态遮挡
            while (vc.PresentedViewController is not null) vc = vc.PresentedViewController;

            var alert = UIAlertController.Create(title, content, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("我知道了", UIAlertActionStyle.Default, null));
            vc.PresentViewController(alert, true, null);
        });
    }
}
