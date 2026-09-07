using System.IO;
using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using FileTransferApp.Android.Services;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.Android;

[Activity(
    Label = "FileTransferApp.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int NotificationPermissionRequestCode = 1001;
    private const int StoragePermissionRequestCode = 1002;
    private static int _crashHandlerInstalled;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        const string TAG = "FTA.BOOT";
        global::Android.Util.Log.Info(TAG, "MainActivity.OnCreate ENTER");
        // 1. 尽早安装崩溃捕获器（在 DI 构建和 Service 启动之前，优先保证闪退也能留下证据）
        EnsureCrashLogger();
        global::Android.Util.Log.Info(TAG, "EnsureCrashLogger done");

        // DI 配置已移至 Application.OnCreate()（在 Avalonia App 创建之前执行）
        // 此处验证 DI 是否就绪
        global::Android.Util.Log.Info(TAG, $"ServiceLocator type={ServiceLocator.Services.GetType().Name}");

        // 2. Avalonia 初始化与 View 创建（DI 已在 Application.OnCreate 中配置完毕）
        try
        {
            global::Android.Util.Log.Info(TAG, "base.OnCreate begin");
            base.OnCreate(savedInstanceState);
            global::Android.Util.Log.Info(TAG, "base.OnCreate OK");
        }
        catch (Java.Lang.Throwable t)
        {
            LogFatal("AvaloniaBaseOnCreate", t);
            global::Android.Util.Log.Error(TAG, "base.OnCreate Java throw: " + t);
            throw;
        }
        catch (Exception ex)
        {
            LogFatal("AvaloniaBaseOnCreate", ex);
            global::Android.Util.Log.Error(TAG, "base.OnCreate Exception: " + ex.Message);
            throw;
        }

        // 4. 运行时通知权限申请（Android 13+）
        try
        {
            global::Android.Util.Log.Info(TAG, "RequestNotificationPermissionIfNeeded");
            RequestNotificationPermissionIfNeeded();
        }
        catch (Exception ex)
        {
            // 权限申请失败不影响主流程
            global::Android.Util.Log.Warn("FileTransferApp", $"RequestNotificationPermission failed: {ex.Message}");
        }

        // 5. 运行时存储权限申请（接收文件写入公共 Download 目录）
        try
        {
            global::Android.Util.Log.Info(TAG, "RequestStoragePermissionIfNeeded");
            RequestStoragePermissionIfNeeded();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FileTransferApp", $"RequestStoragePermission failed: {ex.Message}");
        }
        global::Android.Util.Log.Info(TAG, "MainActivity.OnCreate EXIT OK");
    }

    /// <summary>注册跨平台核心服务 + Android 平台服务（保活 / 存储）</summary>
    private void ConfigureServices()
    {
        var services = new ServiceCollection();
        // Android 平台服务：前台保活 + 应用私有目录存储
        services.AddSingleton<IPlatformKeepAliveService>(_ => new AndroidKeepAliveService(this));
        services.AddSingleton<IStorageService>(_ => new AndroidStorageService(this));
        services.AddSingleton<IFileOpenService>(_ => new AndroidFileOpenService(this));
        services.AddFileTransferServices(GetDeviceName(), DeviceType.Android);
        ServiceLocator.Services = services.BuildServiceProvider();
    }

    private static string GetDeviceName()
    {
        try { return Build.Model ?? "Android Device"; }
        catch { return "Android Device"; }
    }

    /// <summary>Android 13 (API 33+) 起前台通知需运行时申请 POST_NOTIFICATIONS</summary>
    private void RequestNotificationPermissionIfNeeded()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
        if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted) return;

        ActivityCompat.RequestPermissions(
            this,
            new[] { global::Android.Manifest.Permission.PostNotifications },
            NotificationPermissionRequestCode);
    }

    /// <summary>
    /// 申请存储权限以将接收的文件写入公共 Download 目录。
    /// Android 10 及以下：运行时申请 WRITE_EXTERNAL_STORAGE。
    /// Android 11+：MANAGE_EXTERNAL_STORAGE 仅在用户首次接收文件时引导授权，不在 OnCreate 中跳转设置页。
    /// </summary>
    private void RequestStoragePermissionIfNeeded()
    {
        if (Build.VERSION.SdkInt <= BuildVersionCodes.Q)
        {
            // Android 10 及以下：运行时申请 WRITE_EXTERNAL_STORAGE
            if (CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted) return;
            ActivityCompat.RequestPermissions(
                this,
                new[] { global::Android.Manifest.Permission.WriteExternalStorage },
                StoragePermissionRequestCode);
        }
        else if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Android 11+：不在此处跳转设置页（会干扰 Activity 生命周期和 Avalonia 渲染）
            // 模拟器 permissive SELinux 默认放行；真机需在用户首次接收文件时引导
            var granted = global::Android.OS.Environment.IsExternalStorageManager;
            global::Android.Util.Log.Info("FTA.BOOT", $"MANAGE_EXTERNAL_STORAGE granted={granted}");
        }
    }

    // ==================== 崩溃捕获（遵循 1498720 经验：无 logcat 时优先落盘私有目录） ====================

    private void EnsureCrashLogger()
    {
        if (Interlocked.CompareExchange(ref _crashHandlerInstalled, 1, 0) != 0) return;

        // (a) 托管异常：未处理的 Task / async void / Dispatcher 异常统一入口
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception
                     ?? new Exception($"Unhandled non-exception object: {args.ExceptionObject?.GetType()}");
            LogFatal("AppDomain.UnhandledException", ex);
        };

        // (b) 未观察到的任务异常（Task.Run 里未 await 的路径）
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved(); // 防止 .NET Runtime 强制进程退出（Android 上我们要写日志）
        };

        // (c) Android 侧 Java 运行时全局崩溃：如果 AvaloniaMainActivity 没有覆盖，则再兜底观察
        try
        {
            Java.Lang.Thread.DefaultUncaughtExceptionHandler = new LoggingUncaughtExceptionHandler(
                Java.Lang.Thread.DefaultUncaughtExceptionHandler);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("FileTransferApp", $"Java UE handler hook failed: {ex.Message}");
        }
    }

    /// <summary>崩溃日志路径：应用私有 files/ 目录（adb run-as 可直接读取，仅 debuggable 包）</summary>
    internal static string CrashLogPathPrivate
    {
        get
        {
            var ctx = global::Android.App.Application.Context;
            var dir = ctx?.FilesDir?.AbsolutePath ?? "/data/local/tmp";
            return Path.Combine(dir, "crash.log");
        }
    }

    /// <summary>崩溃日志路径：外部 files/ 目录（/sdcard/Android/data/&lt;pkg&gt;/files/crash.log）
    /// 即使 Release 包也能通过 `adb pull /sdcard/Android/data/com.CompanyName.FileTransferApp/files/crash.log` 直接取，
    /// 避免 run-as 依赖 debuggable 属性让我们拿不到崩溃证据。
    /// 注意：Android 4.4+ 外部私有目录无需 WRITE_EXTERNAL_STORAGE 权限即可写入。</summary>
    internal static string CrashLogPathPublic
    {
        get
        {
            var ctx = global::Android.App.Application.Context;
            try
            {
                var dir = ctx?.GetExternalFilesDir(null)?.AbsolutePath;
                if (Directory.Exists(dir))
                    return Path.Combine(dir!, "crash.log");
            }
            catch { /* ignore: 存储不可用 */ }
            // 最终回退：私有目录
            return CrashLogPathPrivate;
        }
    }

    /// <summary>写崩溃到 crash.log（私有 + 公共双份，同时输出到 logcat）</summary>
    internal static void LogFatal(string stage, Exception ex)
    {
        var entry =
            $"[{DateTimeOffset.Now:O}] stage={stage} thread={Thread.CurrentThread.ManagedThreadId}\n" +
            $"{ex}\n---\n";
        global::Android.Util.Log.Error("FileTransferApp.Crash", entry);
        try
        {
            // 两份文件都追加（公共路径优先、私有路径兜底）
            try { File.AppendAllText(CrashLogPathPublic, entry); }
            catch { /* ignore */ }
            try { File.AppendAllText(CrashLogPathPrivate, entry); }
            catch { /* ignore */ }
        }
        catch
        {
            // 文件系统不可用时退化：只走 logcat
        }
    }

    internal static void LogFatal(string stage, Java.Lang.Throwable t)
    {
        var entry =
            $"[{DateTimeOffset.Now:O}] stage={stage} JavaThrowable\n" +
            $"{t}\n---\n";
        global::Android.Util.Log.Error("FileTransferApp.Crash", entry);
        try
        {
            try { File.AppendAllText(CrashLogPathPublic, entry); }
            catch { /* ignore */ }
            try { File.AppendAllText(CrashLogPathPrivate, entry); }
            catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    /// <summary>Java 全局未捕获异常处理器：先写文件再转发给原处理器</summary>
    private sealed class LoggingUncaughtExceptionHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _original;

        public LoggingUncaughtExceptionHandler(Java.Lang.Thread.IUncaughtExceptionHandler? original)
            => _original = original;

        public void UncaughtException(Java.Lang.Thread? thread, Java.Lang.Throwable? throwable)
        {
            try
            {
                // Java 侧回调实际必传非空；Thread.Id 在 Android 36+ 标记过时，仅用于崩溃日志诊断
#pragma warning disable CA1422 // 日志用途，无需改版
                LogFatal(
                    $"JavaUncaught:thread={thread?.Id}-{thread?.Name}",
                    throwable ?? new Java.Lang.RuntimeException("no throwable"));
#pragma warning restore CA1422
            }
            catch
            {
                // 崩溃处理器自身绝不能抛，否则就没有后续处理器机会了
            }
            _original?.UncaughtException(thread!, throwable!);
        }
    }
}
