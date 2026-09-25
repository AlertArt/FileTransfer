using CoreFoundation;
using Foundation;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using UIKit;
using UserNotifications;

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
    private readonly ILocalizationService _loc;

    // 兜底前台提示文案：按照当前语言动态解析
    private string DefaultContent => _loc.GetString("KeepAliveForeground");

    // 通知分类与"打开文件"操作
    private const string CategoryId = "fta_status";
    private const string OpenActionId = "fta_open";
    private const string OpenPathKey = "openPath";
    private static NotificationDelegate? _notificationDelegate;

    private nint? _backgroundTaskId;
    private bool _idleTimerOriginallyDisabled;
    // 平台无关的状态机：跟踪 Running / 文案回退 / 提示去重
    internal readonly KeepAliveStatus Status = new();

    public IOSKeepAliveService(ILocalizationService localization)
    {
        _loc = localization;
        EnsureNotificationSetup();
    }

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

    /// <summary>
    /// iOS 不支持像 Android 那样持续更新的前台通知；进行中状态保持 no-op，
    /// 避免与保活 alert 重复打扰用户。
    /// </summary>
    public void UpdateKeepAlive(string title, string content, double? progress, string? fileId)
    {
        // no-op：iOS 无持久通知栏进程级通知。
    }

    /// <summary>传输完成 / 失败：投放本地通知（首次会请求通知权限；未授权则静默忽略）。</summary>
    public void ShowStatusNotification(string title, string content, string? openPath)
    {
        try
        {
            var center = UNUserNotificationCenter.Current;
            center.RequestAuthorization(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound,
                (granted, _) =>
                {
                    if (!granted) return;
                    try
                    {
                        var notifContent = new UNMutableNotificationContent
                        {
                            Title = title,
                            Body = content,
                            Sound = UNNotificationSound.Default,
                        };
                        // 完成且有本地路径：挂"打开文件"操作（路径随 UserInfo 携带）
                        if (!string.IsNullOrEmpty(openPath))
                        {
                            notifContent.CategoryIdentifier = CategoryId;
                            notifContent.UserInfo = NSDictionary.FromObjectAndKey(
                                new NSString(openPath!), new NSString(OpenPathKey));
                        }
                        var request = UNNotificationRequest.FromIdentifier(
                            Guid.NewGuid().ToString("N"), notifContent, null);
                        center.AddNotificationRequest(request, null);
                    }
                    catch { /* ignore */ }
                });
        }
        catch { /* 通知不可用不影响主流程 */ }
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

    /// <summary>注册通知分类（"打开文件"操作）并设置代理，处理前台展示与操作回调。</summary>
    private void EnsureNotificationSetup()
    {
        try
        {
            if (_notificationDelegate is not null) return;
            var center = UNUserNotificationCenter.Current;

            var open = UNNotificationAction.FromIdentifier(
                OpenActionId,
                _loc.GetString("Notification.Open"),
                UNNotificationActionOptions.Foreground);
            var category = UNNotificationCategory.FromIdentifier(
                CategoryId,
                new[] { open },
                Array.Empty<string>(),
                UNNotificationCategoryOptions.None);
            center.SetNotificationCategories(new NSSet<UNNotificationCategory>(new[] { category }));

            _notificationDelegate = new NotificationDelegate();
            center.Delegate = _notificationDelegate;
        }
        catch { /* 通知不可用不影响主流程 */ }
    }

    /// <summary>UNUserNotificationCenter 代理：前台展示横幅 + 处理"打开文件"操作。</summary>
    private sealed class NotificationDelegate : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(
            UNUserNotificationCenter center,
            UNNotification notification,
            Action<UNNotificationPresentationOptions> completionHandler)
        {
            // App 在前台时也展示横幅（否则完成通知不显示）
            completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);
        }

        public override void DidReceiveNotificationResponse(
            UNUserNotificationCenter center,
            UNNotificationResponse response,
            Action completionHandler)
        {
            try
            {
                var action = response.ActionIdentifier;
                if (action == OpenActionId)
                {
                    var path = response.Notification?.Request?.Content?.UserInfo?
                        .ObjectForKey(new NSString(OpenPathKey))?.ToString();
                    if (!string.IsNullOrEmpty(path))
                        _ = OpenFileAsync(path!);
                }
            }
            catch { /* ignore */ }
            completionHandler();
        }

        private static async Task OpenFileAsync(string path)
        {
            try
            {
                if (ServiceLocator.GetService<IFileOpenService>() is { } svc)
                    await svc.OpenFileAsync(path).ConfigureAwait(false);
            }
            catch { /* 打开失败静默 */ }
        }
    }

    /// <summary>在当前最上层 ViewController 上展示一次性 UIAlertController</summary>
    private void ShowKeepAliveAlert(string title, string content)
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            // iOS 13+ 多场景：用 ConnectedScenes 查找前台活跃场景的 KeyWindow，
            // 替代已过时的 UIApplication.KeyWindow（多场景会跨所有 scene 混淆窗口）。
            var vc = UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .FirstOrDefault(s => s.ActivationState == UISceneActivationState.ForegroundActive)
                ?.Windows.FirstOrDefault(w => w.IsKeyWindow)?.RootViewController;
            if (vc is null) return;
            // 取最上层 presented VC，避免被模态遮挡
            while (vc.PresentedViewController is not null) vc = vc.PresentedViewController;

            var alert = UIAlertController.Create(title, content, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create(_loc.GetString("KeepAliveOk"), UIAlertActionStyle.Default, null));
            vc.PresentViewController(alert, true, null);
        });
    }
}
