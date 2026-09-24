using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using FileTransferApp.Services;

namespace FileTransferApp.Android.Services;

/// <summary>
/// 文件传输前台服务。Android 8.0+ 必须以前台服务形式驻留才能在后台持续监听
/// UDP 多播与 HTTP 传输端口，否则系统会在后台 1 分钟内冻结进程。
/// 通过持久通知告知用户传输进行中，同时保活应用进程。
/// ------ 崩溃防御（严格遵循 Android 平台契约）：
/// 1) OnStartCommand 内任何步骤抛异常都**不会阻止** 5s 窗口内调用 StartForeground
///    （避免 Android 8+ 的 RemoteServiceException「startForegroundService 但 5 秒内未 startForeground」闪退）。
/// 2) EnsureNotificationChannel / BuildNotification / StartForeground 三者各自都有独立 try/catch，
///    链式失败时退化成「空通道 + 系统默认图标 + 基础 Notification」，但始终完成 StartForeground 调用。
/// 3) 前台服务权限缺失（厂商 ROM 的 SecurityException）被吞掉，只写 Trace 不崩溃。
/// </summary>
[Service(
    Name = "com.CompanyName.FileTransferApp.TransferForegroundService",
    ForegroundServiceType = ForegroundService.TypeDataSync,
    Exported = false)]
public sealed class TransferForegroundService : Service
{
    public const string ChannelId = "filetransfer_foreground";
    public const int NotificationId = 4205953; // "FT01" 自定义 ID

    /// <summary>传输完成/失败等一次性状态通知的渠道与 ID（与保活通知分离，可自动消失）。</summary>
    public const string StatusChannelId = "filetransfer_status";
    public const int StatusNotificationId = 4205954; // "FT02" 自定义 ID

    /// <summary>通知强调色 = 品牌紫 #6366F1（明暗主题下均协调）。</summary>
    public const int BrandColor = unchecked((int)0xFF6366F1);

    // 当前通知文案：供 UpdateContent 静态调用保持上下文一致
    private static string? s_currentTitle;
    private static string? s_currentContent;

    public const string ExtraTitle = "title";
    public const string ExtraContent = "content";

    // 通知操作按钮（由 NotificationActionReceiver 处理）
    public const string ActionPause = "com.CompanyName.FileTransferApp.action.PAUSE";
    public const string ActionCancel = "com.CompanyName.FileTransferApp.action.CANCEL";
    public const string ActionOpenFile = "com.CompanyName.FileTransferApp.action.OPEN_FILE";
    public const string ExtraFileId = "fileId";
    public const string ExtraOpenPath = "openPath";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var title = intent?.GetStringExtra(ExtraTitle) ?? s_currentTitle ?? "FileTransferApp";
        var content = intent?.GetStringExtra(ExtraContent) ?? s_currentContent ?? LocalizationService.Instance.GetString("KeepAliveIdle");

        s_currentTitle = title;
        s_currentContent = content;

        Notification? notification = null;
        try
        {
            // ① 先尝试建通知渠道（失败不致命：通知仍可显示，只是 Android 8+ 上可能无渠道会被丢弃）
            try { EnsureNotificationChannelSafe(); }
            catch (Exception ex) { LogError("CreateNotificationChannel", ex); }

            // ② 构建 Notification：失败时使用 Fallback 最小化通知
            try { notification = BuildNotificationSafe(this, title, content); }
            catch (Exception ex)
            {
                LogError("BuildNotification", ex);
                notification = BuildFallbackNotification(title, content);
            }
        }
        catch (Exception ex)
        {
            LogError("FGS_Prepare", ex);
            notification ??= BuildFallbackNotification(title, content);
        }
        finally
        {
            // -------- Android 8+ 5 秒窗口硬性契约：无论上面成功/失败，必须调 StartForeground --------
            CallStartForegroundGuaranteed(notification, title, content);
        }

        return StartCommandResult.Sticky; // 进程被杀后由系统自动重建，保持 UDP/HTTP 长驻可发现
    }

    /// <summary>
    /// 用户从最近任务划掉应用：安排一次服务重启，尽量保持后台发现/接收不中断。
    /// 注意：Android 12+ 对"后台启动前台服务"有限制，可能失败；已全程 catch，失败不影响其它逻辑。
    /// </summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        try
        {
            var restart = new Intent(this, typeof(TransferForegroundService));
            restart.SetPackage(PackageName);
            var flags = PendingIntentFlags.OneShot;
            if (OperatingSystem.IsAndroidVersionAtLeast(23)) flags |= PendingIntentFlags.Immutable;
            var pi = PendingIntent.GetService(this, 1001, restart, flags);

            var am = (AlarmManager?)GetSystemService(AlarmService);
            am?.Set(AlarmType.ElapsedRealtime, SystemClock.ElapsedRealtime() + 1000, pi);
            global::Android.Util.Log.Info("FTA.BOOT", "OnTaskRemoved: scheduled service restart");
        }
        catch (Exception ex)
        {
            LogError("OnTaskRemoved", ex);
        }
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        try
        {
            // StopForeground(StopForegroundFlags) 仅 Android 24+；旧版本回退基础重载
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
            else
                StopForeground(true);
            NotificationManagerCompat.From(this)!.Cancel(NotificationId);
        }
        catch { /* ignore */ }
        base.OnDestroy();
    }

    /// <summary>更新已存在的前台通知文案（无需重启服务），不显示进度条。</summary>
    public static void UpdateContent(Context context, string title, string content)
        => UpdateContent(context, title, content, -1);

    /// <summary>更新前台通知：文案 + 可选进度条（0..100，负值表示不显示进度条）。</summary>
    public static void UpdateContent(Context context, string title, string content, int progressPercent)
        => UpdateContent(context, title, content, progressPercent, null);

    /// <summary>更新前台通知：文案 + 进度条 + 可选"暂停/取消"操作按钮（fileId 非空时挂按钮）。</summary>
    public static void UpdateContent(Context context, string title, string content, int progressPercent, string? fileId)
    {
        s_currentTitle = title;
        s_currentContent = content;
        try
        {
            var n = BuildNotificationSafe(context, title, content, progressPercent, fileId);
            NotificationManagerCompat.From(context)!.Notify(NotificationId, n);
        }
        catch
        {
            // 资源异常：尝试 fallback 后仍失败就跳过
            try
            {
                NotificationManagerCompat.From(context)!
                    .Notify(NotificationId, BuildFallbackNotification(title, content));
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 投放一次性「传输完成 / 失败」状态通知（自动消失，与保活通知互不影响）。
    /// 使用独立渠道（默认重要级，可有提示音）；点击回到 App。
    /// </summary>
    public static void ShowStatusNotification(Context context, string title, string content, string? openPath)
    {
        try
        {
            EnsureStatusChannelSafe(context);
#pragma warning disable CS8600, CS8602, CS8603 // AndroidX 绑定流式 Builder 全链可空标注，运行期非空
            var builder = new NotificationCompat.Builder(context, StatusChannelId)
                .SetSmallIcon(GetSmallIconId())
                .SetContentTitle(title)
                .SetContentText(content)
                .SetStyle(new NotificationCompat.BigTextStyle().BigText(content))
                .SetAutoCancel(true)
                .SetOngoing(false)
                .SetPriority(NotificationCompat.PriorityDefault)
                .SetColor(BrandColor)
                .SetCategory(NotificationCompat.CategoryStatus);
            var intent = BuildContentIntent(context);
            if (intent is not null) builder = builder.SetContentIntent(intent);
            // 完成通知：有本地路径时挂"打开文件"按钮
            if (!string.IsNullOrEmpty(openPath))
            {
                var open = BuildAction(context, ActionOpenFile, ExtraOpenPath, openPath!, 103,
                    LocalizationService.Instance.GetString("Notification.Open"));
                if (open is not null) builder = builder.AddAction(open);
            }
            NotificationManagerCompat.From(context)!.Notify(StatusNotificationId, builder.Build());
#pragma warning restore CS8600, CS8602, CS8603
        }
        catch (Exception ex)
        {
            LogError("ShowStatusNotification", ex);
        }
    }

    // ============================================================
    // 内部实现（均为 Safe：异常只记录，不向上抛）
    // ============================================================

    /// <summary>
    /// 100% 保证 OnStartCommand 返回前调用一次 StartForeground：
    /// - 先尝试 AndroidX ServiceCompat.StartForeground(..., foregroundServiceTypes) 常规路径
    /// - 若抛 SecurityException / IllegalArgumentException / Resources.NotFoundException 等，
    ///   立即退化为无类型参数的基础重载（API 23 以前无需 type），
    /// - 再失败则退化为 Service.StartForeground（无 AndroidX）的基础调用，
    /// - 最终任何异常吞掉，避免 RemoteServiceException。
    /// </summary>
    private void CallStartForegroundGuaranteed(Notification? notification, string title, string content)
    {
        notification ??= BuildFallbackNotification(title, content);
        int typeDataSync;
        try
        {
            typeDataSync = OperatingSystem.IsAndroidVersionAtLeast(29)
                ? (int)ForegroundService.TypeDataSync
                : 0;
        }
        catch
        {
            typeDataSync = 0;
        }

        // 尝试 1：AndroidX + type 参
        try
        {
            ServiceCompat.StartForeground(this, NotificationId, notification, typeDataSync);
            return;
        }
        catch (Java.Lang.SecurityException ex) { LogError("FGS_Start_AndroidX", ex); }
        catch (Java.Lang.IllegalArgumentException ex) { LogError("FGS_Start_AndroidX", ex); }
        catch (Exception ex) { LogError("FGS_Start_AndroidX", ex); }

        // 尝试 2：ServiceCompat 不带 type 参数（回退旧 API）
        try
        {
            StartForeground(NotificationId, notification);
            return;
        }
        catch (Exception ex) { LogError("FGS_Start_Legacy", ex); }
    }

    private void EnsureNotificationChannelSafe()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var mgr = (NotificationManager)GetSystemService(NotificationService)!;
        if (mgr is null) return;
        try
        {
            if (mgr.GetNotificationChannel(ChannelId) is not null) return;
        }
        catch { /* ignore */ }

        var channel = new NotificationChannel(
            ChannelId,
            LocalizationService.Instance.GetString("Notification.ChannelName"),
            NotificationImportance.Low) // 低重要级：无声响，仅在抽屉中显示
        {
            Description = LocalizationService.Instance.GetString("Notification.ChannelDescription")
        };
        // .NET for Android 不同 binding 版本提供 ShowBadge 属性或 SetShowBadge 方法：
        // 这里使用反射优先调用 SetShowBadge，失败时不抛（属性仅外观增强）
        TryApplyNoBadge(channel);
        try { mgr.CreateNotificationChannel(channel); }
        catch { /* ignore duplicate / system exception */ }
    }

    /// <summary>创建「传输状态」通知渠道（完成/失败通知，默认重要级）。</summary>
    private static void EnsureStatusChannelSafe(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        try
        {
            var mgr = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (mgr is null) return;
            try { if (mgr.GetNotificationChannel(StatusChannelId) is not null) return; }
            catch { /* ignore */ }

            var channel = new NotificationChannel(
                StatusChannelId,
                LocalizationService.Instance.GetString("Notification.StatusChannelName"),
                NotificationImportance.Default)
            {
                Description = LocalizationService.Instance.GetString("Notification.StatusChannelDescription")
            };
            mgr.CreateNotificationChannel(channel);
        }
        catch { /* ignore duplicate / system exception */ }
    }

    private static void TryApplyNoBadge(NotificationChannel channel)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        try { channel.SetShowBadge(false); }
        catch (MissingMethodException) { /* ignore on older bindings */ }
        catch (Java.Lang.NoSuchMethodError) { /* ignore */ }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 安全构建 Notification：
    /// - 优先尝试读取应用资源 Id（Resources 里 drawable/Icon，通过反射从应用 Resource/ResourceConstant 类动态取 Id；
    ///   这样不同资源生成策略（大小写/命名）下都能命中，避免 compile-time 常量缺失。
    /// - 若资源 ID 映射失败，退回 Android 系统内置的 SymActionEmail / StatNotifySync 占位图标。
    /// </summary>
    internal static Notification BuildNotificationSafe(Context context, string title, string content, int progressPercent = -1, string? fileId = null)
    {
        // AndroidX 绑定将 Java 流式 Builder 的构造与全链方法返回标注为可空，
        // 运行期必然非空；统一抑制 nullable 告警避免噪音。
#pragma warning disable CS8600, CS8602, CS8603
        NotificationCompat.Builder builder;
        try
        {
            builder = new NotificationCompat.Builder(context, ChannelId)
                .SetSmallIcon(GetSmallIconId())
                .SetContentTitle(title)
                .SetContentText(content)
                .SetOngoing(true)
                .SetPriority(NotificationCompat.PriorityLow)
                .SetColor(BrandColor)
                .SetCategory(NotificationCompat.CategoryService);
        }
        catch (Exception ex)
        {
            // 资源 ID 查找失败：退回系统 drawable（永远存在）
            LogError("BuildNotification_PrimaryIcon", ex);
            builder = new NotificationCompat.Builder(context, ChannelId)
                .SetSmallIcon(GetFallbackIconId())
                .SetContentTitle(title)
                .SetContentText(content)
                .SetOngoing(true)
                .SetPriority(NotificationCompat.PriorityLow)
                .SetColor(BrandColor)
                .SetCategory(NotificationCompat.CategoryService);
        }

        // 进度条：传输中显示 0..100，等待/暂停等 progressPercent<0 时不显示
        if (progressPercent >= 0)
            builder = builder.SetProgress(100, Math.Clamp(progressPercent, 0, 100), false);

        var intent = BuildContentIntent(context);
        if (intent is not null) builder = builder.SetContentIntent(intent);

        // 进行中通知：fileId 非空时挂"暂停 / 取消"操作按钮
        if (!string.IsNullOrEmpty(fileId))
        {
            var pause = BuildAction(context, ActionPause, ExtraFileId, fileId!, 101,
                LocalizationService.Instance.GetString("Notification.Pause"));
            if (pause is not null) builder = builder.AddAction(pause);

            var cancel = BuildAction(context, ActionCancel, ExtraFileId, fileId!, 102,
                LocalizationService.Instance.GetString("Notification.Cancel"));
            if (cancel is not null) builder = builder.AddAction(cancel);
        }

        return builder.Build();
#pragma warning restore CS8600, CS8602, CS8603
    }

    /// <summary>构造通知操作按钮的 PendingIntent（显式指向 NotificationActionReceiver）。</summary>
    private static NotificationCompat.Action? BuildAction(Context context, string action, string extraKey, string extraValue, int requestCode, string title)
    {
        try
        {
            var intent = new Intent(context, typeof(NotificationActionReceiver));
            intent.SetAction(action);
            intent.PutExtra(extraKey, extraValue);
            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
                flags |= PendingIntentFlags.Immutable;
            var pi = PendingIntent.GetBroadcast(context, requestCode, intent, flags);
            if (pi is null) return null;
            return new NotificationCompat.Action.Builder(0, title, pi).Build();
        }
        catch (Exception ex)
        {
            LogError("BuildAction", ex);
            return null;
        }
    }

    /// <summary>通知小图标：优先 app 的 alpha-only 矢量 ic_stat_notify，退回 app Icon，最后系统占位图标。</summary>
    private static int GetSmallIconId()
    {
        var id = GetDrawableResourceId("ic_stat_notify");
        if (id != 0) return id;
        id = GetAppIconResourceId();
        return id != 0 ? id : GetFallbackIconId();
    }

    /// <summary>点击通知回到应用主界面。</summary>
    private static PendingIntent? BuildContentIntent(Context context)
    {
        try
        {
            var intent = new Intent(context, typeof(global::FileTransferApp.Android.MainActivity));
            intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
                flags |= PendingIntentFlags.Immutable; // Android 12+ 强制要求显式可变性
            return PendingIntent.GetActivity(context, 0, intent, flags);
        }
        catch (Exception ex)
        {
            LogError("BuildContentIntent", ex);
            return null;
        }
    }

    /// <summary>通过反射按资源名取 app drawable 的资源 id（兼容 Resource.Drawable / ResourceConstant.Drawable）。</summary>
    private static int GetDrawableResourceId(string fieldName)
    {
        try
        {
            var appAsm = typeof(global::FileTransferApp.Android.MainActivity).Assembly;
            foreach (var container in new[] { "Resource", "ResourceConstant" })
            {
                foreach (var ns in new[] { "FileTransferApp.Android.", "FileTransferApp.", string.Empty })
                {
                    var t = appAsm.GetType(ns + container + "+Drawable");
                    if (t is null) continue;
                    var f = t.GetField(fieldName, System.Reflection.BindingFlags.Public
                                                 | System.Reflection.BindingFlags.Static);
                    if (f is not null && f.FieldType == typeof(int))
                    {
                        var id = (int)(f.GetValue(null) ?? 0);
                        if (id != 0) return id;
                    }
                }
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>
    /// 通过反射从应用程序集动态取出 drawable/icon 的资源 id。
    /// 兼容：Microsoft.Android 生成的 Resource.Drawable、ResourceConstant.Drawable 两种方式。
    /// </summary>
    private static int GetAppIconResourceId()
    {
        try
        {
            var appAsm = typeof(global::FileTransferApp.Android.MainActivity).Assembly;
            foreach (var name in new[] { "Drawable", "Mipmap" })
            {
                foreach (var container in new[] { "Resource", "ResourceConstant" })
                {
                    var t = appAsm.GetType("FileTransferApp.Android." + container + "+" + name)
                             ?? appAsm.GetType("FileTransferApp." + container + "+" + name)
                             ?? appAsm.GetType(container + "+" + name);
                    if (t is null) continue;
                    foreach (var field in t.GetFields(System.Reflection.BindingFlags.Public
                                                      | System.Reflection.BindingFlags.Static))
                    {
                        if (string.Equals(field.Name, "Icon", StringComparison.OrdinalIgnoreCase)
                            && field.FieldType == typeof(int))
                        {
                            var id = (int)(field.GetValue(null) ?? 0);
                            if (id != 0) return id;
                        }
                    }
                }
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    /// <summary>
    /// 最后兜底：BuildNotificationSafe 本身抛异常时，用最小的 NotificationCompat + 系统图标构造
    /// （避免在 OnStartCommand 仍未构建好 Notification）
    /// </summary>
    private static Notification BuildFallbackNotification(string title, string content)
    {
#pragma warning disable CS8600, CS8602, CS8603 // AndroidX 绑定：流式 Builder 全链可空标注，运行期非空
        try
        {
            var ctx = global::Android.App.Application.Context;
            var builder = new NotificationCompat.Builder(ctx, ChannelId)
                .SetSmallIcon(GetFallbackIconId())
                .SetContentTitle(title ?? "FileTransferApp")
                .SetContentText(content ?? LocalizationService.Instance.GetString("KeepAliveIdle"))
                .SetOngoing(true)
                .SetPriority(NotificationCompat.PriorityLow);
            return builder.Build();
        }
        catch (Exception ex)
        {
            // 理论上不会发生，Android system drawable 一定存在
            LogError("BuildFallbackNotification", ex);
#pragma warning disable CA1416
            return new Notification(); // 极简占位通知，至少保证 StartForeground 能入参
#pragma warning restore CA1416
        }
#pragma warning restore CS8600, CS8602, CS8603
    }

    private static int GetFallbackIconId()
    {
        // Android SDK 每个版本一定带有的原生占位 drawable（按优先级尝试）
        // 注意：这些是 Android.Resource.Drawable 成员，不会走 app 的 resources 映射
        // 硬编码 android.R.drawable.stat_notify_sync = 17301659 作为最终兜底。
        try
        {
            return global::Android.Resource.Drawable.StatNotifySync;
        }
        catch
        {
            // Android 12+ 部分包裁剪可能缺上面的常量，直接用常量兜底
            return 17301659;
        }
    }

    private static void LogError(string step, Exception ex)
        => System.Diagnostics.Trace.WriteLine($"[FGS:{step}] {ex.GetType().Name}: {ex.Message}");
}
