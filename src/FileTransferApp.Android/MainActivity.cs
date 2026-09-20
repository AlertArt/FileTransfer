using System.IO;
using Android.App;
using Android.Content;
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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Portrait)]
public class MainActivity : AvaloniaMainActivity
{
    private const int NotificationPermissionRequestCode = 1001;
    private const int StoragePermissionRequestCode = 1002;
    private static int _crashHandlerInstalled;

    /// <summary>供 AndroidFilePickerService 惰性获取当前 Activity（DI 在 Application.OnCreate 中提前构建）。</summary>
    internal static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        const string TAG = "FTA.BOOT";
        global::Android.Util.Log.Info(TAG, "MainActivity.OnCreate ENTER");
        // 1. 尽早安装崩溃捕获器（在 DI 构建和 Service 启动之前，优先保证闪退也能留下证据）
        EnsureCrashLogger();
        global::Android.Util.Log.Info(TAG, "EnsureCrashLogger done");
        // 1.5 挂 Trace 文件监听器：core 的 FTA.CTRL/FTA.HTTP 日志落到公共 files 目录，便于 adb pull 排查
        AttachTraceFileListener();
        // 1.6 把 crash.log / fta.trace.log 复制到公共 Download 目录，用户无需 adb 也能取证
        CopyLogsToPublicDownloads();
        Current = this;

        // DI 配置已移至 Application.OnCreate()（在 Avalonia App 创建之前执行）
        // 此处验证 DI 是否就绪
        global::Android.Util.Log.Info(TAG, $"ServiceLocator type={ServiceLocator.Services.GetType().Name}");

        // 2.5 edge-to-edge 接管系统栏（成熟 App 语义）：保留状态栏与导航栏（充电/时间/电量/导航始终可见），
        //     把两者背景设为透明，让紫色内容自然延伸到屏幕最顶端和最底端实现无缝衔接；
        //     系统图标改为白色以在品牌紫上清晰显示。注意：切记不要再 Hide 系统栏——隐藏后
        //     顶部会露出窗口黑色背景、且系统状态（如充电中）一并消失，这正是本修复要消除的。
        //     全部逻辑收敛到 SetupEdgeToEdgeSystemBars()：OnCreate 前置调用一次、base.OnCreate
        //     之后再跑一次、OnWindowFocusChanged(true) 每次重设一次——防止 Avalonia 的 InsetsManager
        //     或系统把 SetDecorFitsSystemWindows(true) 重新塞回，导致顶部重新出现系统栏空白带。
        try { SetupEdgeToEdgeSystemBars(); }
        catch (Exception ex) { global::Android.Util.Log.Warn(TAG, $"Edge-to-edge setup failed: {ex.Message}"); }

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

        // 2.6 base.OnCreate 之后再次强制 edge-to-edge：Avalonia 视图 / InsetsManager 在此阶段创建，
        //     可能以 SetDecorFitsSystemWindows(true) 复位 —— 必须在其之后重新声明，
        //     否则顶部会重新出现系统栏空白带，紫色顶栏铺不到屏幕最顶。
        try { SetupEdgeToEdgeSystemBars(); }
        catch (Exception ex) { global::Android.Util.Log.Warn(TAG, $"Edge-to-edge re-assert failed: {ex.Message}"); }

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

    /// <summary>
    /// 窗口重新获焦：Android 可能在对话框关闭 / 返回前台时重设系统栏配色，
    /// 重写此方法在每次获焦时重新应用"保留系统栏 + 透明背景 + 白色图标"的 edge-to-edge 语义。
    /// </summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            SetupEdgeToEdgeSystemBars();
        }
    }

    /// <summary>
    /// edge-to-edge 接管（成熟语义，微信/支付宝一致）：**保留**状态栏与导航栏不隐藏——
    /// 这样时间 / 电量 / 充电图标 / 导航始终可见；把二者背景设为透明，让品牌紫色内容
    /// 自然铺到屏幕最顶端与最底端实现无缝衔接；系统图标改白色以在紫底上清晰显示。
    /// 注意：**绝不能调用 Hide()**——隐藏后顶部会露出窗口默认黑色背景、且系统状态（如充电）
    /// 一并消失，这正是本修复要消除的问题。API30+ 用 InsetsController，API21-29 用透明背景色。
    /// </summary>
    private void SetupEdgeToEdgeSystemBars()
    {
        const string TAG = "FTA.FULLSCREEN";
        if (Window is not { } window || Window.DecorView is not { } decor)
        {
            global::Android.Util.Log.Warn(TAG, "Window/DecorView not ready, skip");
            return;
        }

        try
        {
            // (0) 解除 decor 对系统栏的空间预留（全 API）——「内容延伸到状态栏背后」的根本开关。
            //     API35+ 系统已强制 edge-to-edge，这里对所有版本显式声明，
            //     并放在 SetupEdgeToEdgeSystemBars() 内部，确保每次 OnWindowFocusChanged 都能重新生效，
            //     防 Avalonia InsetsManager 或系统以 SetDecorFitsSystemWindows(true) 复位顶部空白带。
            AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(window, false);

            // (0.5) 允许内容铺进刘海/挖孔区域（API28+）：否则打孔屏顶部会保留黑色/白色竖条，
            //     表现为"清晰可见的刘海横条 / 顶不到头"，这正是 purple 顶栏铺不满的常见根因。
            //     与 fitSystemWindows=false 配套：内容 + 状态栏图标都会延伸到刘海两侧。
#pragma warning disable CA1416
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.P &&
                window.Attributes is { } attrs)
                attrs.LayoutInDisplayCutoutMode = global::Android.Views.LayoutInDisplayCutoutMode.ShortEdges;
#pragma warning restore CA1416

            // 关键决策（微信/支付宝一致）：**保留**状态栏与导航栏——绝不 Hide()。
            // 一旦隐藏，顶部会露出窗口默认黑色背景、且系统状态（时间/电量/充电图标/导航）
            // 一并消失，这正是用户反馈的"顶部变黑 + 看不到充电"的根因。改为：
            //   (a) 状态栏与导航栏背景设为**透明**，让品牌紫色内容自然铺到屏幕最顶端/最底端实现无缝；
            //   (b) 系统图标：状态栏始终在品牌紫背景上 → **白色**；
            //       导航栏背景随主题（浅色=白底/深色=深底）→ **跟随主题**，否则浅色主题下白图标在白底上隐形。

            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R)
            {
                // API30+：InsetsController —— 显示系统栏 + 透明背景 + 图标配色
                var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(window, decor)!;
                controller.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
                controller.Show(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());

                // 系统栏背景透明（让紫色透出来）。API35+ 强制 edge-to-edge 时系统已默认透明，
                // 且 SetStatusBarColor/SetNavigationBarColor 已标记过时，仅对低于 35 的版本显式设置。
#pragma warning disable CA1422
                if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.VanillaIceCream)
                {
                    window.SetStatusBarColor(global::Android.Graphics.Color.Transparent);
                    window.SetNavigationBarColor(global::Android.Graphics.Color.Transparent);
                }
#pragma warning restore CA1422

                // 系统图标改白色（在紫色上显示）：Light 意为"图标为深色"，故置 false 得到白色图标
                controller.AppearanceLightStatusBars = false;
                // 导航栏随主题：浅色主题 → 深色图标(白底)，深色主题 → 白色图标(深底)
                var darkMode = IsDarkModeEnabled();
                controller.AppearanceLightNavigationBars = !darkMode;
                global::Android.Util.Log.Info(TAG, "Edge-to-edge OK (InsetsController, API>=30): keep bars + transparent + icons(status=white, nav=" + (darkMode ? "white" : "dark") + ")");
            }
            else
            {
#pragma warning disable CA1416 // 仅 API21-29 路径
#pragma warning disable CA1422
                // API21-29：传统 SystemUiVisibility —— 保留系统栏，仅用 Layout 系列 flags 布局铺满 + 透明背景
#pragma warning disable CA1416
                window.SetStatusBarColor(global::Android.Graphics.Color.Transparent);
                window.SetNavigationBarColor(global::Android.Graphics.Color.Transparent);
#pragma warning restore CA1416
                decor.SystemUiFlags =
                    global::Android.Views.SystemUiFlags.LayoutStable |
                    global::Android.Views.SystemUiFlags.LayoutFullscreen |
                    global::Android.Views.SystemUiFlags.LayoutHideNavigation;
#pragma warning restore CA1422
#pragma warning restore CA1416
                global::Android.Util.Log.Info(TAG, "Edge-to-edge OK (SystemUiVisibility, API21-29): keep bars + transparent");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(TAG, $"SetupEdgeToEdgeSystemBars failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 当前是否深色模式：优先应用内主题偏好（ThemeService），"跟随系统"时回退到系统夜间模式。
    /// 用于导航栏图标配色（底部是应用 Surface 白底/深底），保证图标永不变色隐形。
    /// </summary>
    private bool IsDarkModeEnabled()
    {
        try
        {
            switch (ThemeService.Instance.Current)
            {
                case ThemePreference.Dark:
                    return true;
                case ThemePreference.Light:
                    return false;
            }
            var uiMode = Resources?.Configuration?.UiMode;
            return uiMode is not null &&
                   (uiMode & global::Android.Content.Res.UiMode.NightMask) == global::Android.Content.Res.UiMode.NightYes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>注册跨平台核心服务 + Android 平台服务（保活 / 存储）</summary>
    /// 注意：真实 DI 在 Application.OnCreate() 内联构建；此方法为遗留死代码，已移除防止误导。

    // ==================== 原生文件选择器（SAF / ACTION_OPEN_DOCUMENT） ====================
    // Avalonia 的 StorageProvider 在 Android 上可能拿不到 provider，导致发送时文件窗口打不开；
    // 这里直接用系统文档选择器，并把选择的 content:// URI 复制到应用缓存获得真实本地路径。

    private const int PickDocumentRequestCode = 2002;
    private TaskCompletionSource<string[]>? _pickTcs;

    internal Task<string[]> PickFilesNativeAsync()
    {
        _pickTcs = new TaskCompletionSource<string[]>();
        var intent = new Intent(Intent.ActionOpenDocument)
            .AddCategory(Intent.CategoryOpenable)
            .SetType("*/*")
            .PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(intent, PickDocumentRequestCode);
        return _pickTcs.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickDocumentRequestCode) return;

        var paths = new List<string>();
        if (resultCode == Result.Ok && data != null)
        {
            var uris = new List<global::Android.Net.Uri>();
            var single = data.Data;
            if (single != null) uris.Add(single);
            var clip = data.ClipData;
            if (clip != null)
            {
                for (var i = 0; i < clip.ItemCount; i++)
                {
                    var u = clip.GetItemAt(i)?.Uri;
                    if (u != null && !uris.Contains(u)) uris.Add(u);
                }
            }
            foreach (var uri in uris)
            {
                var path = MaterializePickedUri(uri);
                if (path != null)
                {
                    paths.Add(path);
                    global::Android.Util.Log.Info("FTA.FILE", $"PICK-OK {uri} -> {path}");
                }
                else
                {
                    global::Android.Util.Log.Warn("FTA.FILE", $"PICK-FAIL materialize {uri}");
                }
            }
        }
        _pickTcs?.TrySetResult(paths.ToArray());
    }

    /// <summary>
    /// 把 SAF content:// URI 内容复制到应用私有缓存，返回真实文件路径（传输引擎按路径读文件）。
    /// 文件名必须保留原始名称（含扩展名）：传输文件名取自 Path.GetFileName(filePath)，
    /// 若这里回退成 "file-时间戳" 会导致对端收到无扩展名、无法识别的文件。
    /// </summary>
    private string? MaterializePickedUri(global::Android.Net.Uri uri)
    {
        try
        {
            var display = QueryDisplayName(uri);
            if (string.IsNullOrWhiteSpace(display))
            {
                display = GuessDisplayNameFromUri(uri);
                global::Android.Util.Log.Warn("FTA.FILE", $"PICK-NAME query failed, fallback uri=\"{display ?? "null"}\" for {uri}");
            }

            var safe = SanitizePickedName(display ?? $"file-{DateTime.UtcNow.Ticks}");
            if (string.IsNullOrWhiteSpace(safe)) safe = $"file-{DateTime.UtcNow.Ticks}";
            global::Android.Util.Log.Info("FTA.FILE", $"PICK-NAME uri={uri} name=\"{safe}\"");

            var dir = Path.Combine(CacheDir?.AbsolutePath ?? FilesDir?.AbsolutePath ?? "/data/local/tmp", "picked");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, safe);
            if (File.Exists(dest))
                dest = Path.Combine(dir, $"{DateTime.UtcNow.Ticks}-{safe}");

            using var input = ContentResolver?.OpenInputStream(uri)
                              ?? throw new IOException("OpenInputStream returned null");
            using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            return dest;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FTA.FILE", $"MaterializePickedUri FAIL {uri}: {ex.Message}");
            return null;
        }
    }

    private string? QueryDisplayName(global::Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(uri, null, null, null, null);
            if (cursor is null || cursor.IsAfterLast) return null;
            var idx = cursor.GetColumnIndex(global::Android.Provider.IOpenableColumns.DisplayName);
            if (idx < 0) return null;
            // cursor 初始位于 -1（first row 之前），必须先 MoveToFirst 才能读值，
            // 否则 GetString 抛 NPE 被 catch 吞掉 → 永远回退到 doc-id 推断，
            // 导致对端收到 image_1000004624 这类无扩展名文件、无法识别为图片。
            if (!cursor.MoveToFirst()) return null;
            return cursor.GetString(idx);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FTA.FILE", $"QueryDisplayName FAIL {uri}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从 SAF URI 的文档 ID 推断文件名（含扩展名）。doc-id 形如
    /// "primary:Download/photo.jpg" / "msf:100021" / "image:100034"，
    /// 取最后一个 '/' 之后的段并解码。仅当 QueryDisplayName 拿不到名字时使用。
    /// </summary>
    private static string? GuessDisplayNameFromUri(global::Android.Net.Uri uri)
    {
        try
        {
            var last = uri.LastPathSegment;
            if (string.IsNullOrWhiteSpace(last)) return null;
            var decoded = global::Android.Net.Uri.Decode(last);
            var slash = decoded!.LastIndexOf('/');
            var name = slash >= 0 ? decoded.Substring(slash + 1) : decoded;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FTA.FILE", $"GuessDisplayNameFromUri FAIL {uri}: {ex.Message}");
            return null;
        }
    }

    /// <summary>文件名净化：保留扩展名，替换非法字符，超长时截断头部保留尾部（含扩展名），预防路径注入。</summary>
    private static string? SanitizePickedName(string name)
    {
        try
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(invalid.Contains(c) || c == '/' || c == '\\' || c == ':' ? '_' : c);
            }
            while (sb.Length > 0 && sb[0] == '.') sb.Remove(0, 1); // 防隐藏文件/伪装目录
            if (sb.Length > 120)
            {
                // 截掉头部，保留尾部（扩展名与大部分原名）
                var keep = sb.ToString(sb.Length - 120, 120);
                sb.Clear();
                sb.Append(keep);
            }
            var s = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
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

    // ==================== 日志导出到公共 Download（无需 adb 取证） ====================

    /// <summary>
    /// 把 crash.log / fta.trace.log 复制到 /sdcard/Download/fta_crash.log、fta_trace.log，
    /// 用户用文件管理器或数据线即可取回，无需 adb。权限不足时静默失败（不影响启动）。
    /// </summary>
    private static void CopyLogsToPublicDownloads()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var srcDir = ctx?.GetExternalFilesDir(null)?.AbsolutePath;
            if (string.IsNullOrEmpty(srcDir) || !Directory.Exists(srcDir)) return;

            var downloadDir = global::Android.OS.Environment.GetExternalStoragePublicDirectory(
                global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
            if (string.IsNullOrEmpty(downloadDir)) return;
            try { Directory.CreateDirectory(downloadDir); } catch { return; }

            var pairs = new[] { ("crash.log", "fta_crash.log"), ("fta.trace.log", "fta_trace.log") };
            foreach (var (src, dest) in pairs)
            {
                var s = Path.Combine(srcDir, src);
                if (File.Exists(s))
                {
                    File.Copy(s, Path.Combine(downloadDir, dest), overwrite: true);
                    global::Android.Util.Log.Info("FTA.BOOT", $"log exported: {dest}");
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("FTA.BOOT", $"CopyLogsToPublicDownloads failed: {ex.Message}");
        }
    }

    // ==================== 崩溃捕获（遵循 1498720 经验：无 logcat 时优先落盘私有目录） ====================

    /// <summary>FTA.CTRL / FTA.HTTP 等 Trace 日志写入公共目录 fta.trace.log（PackageExternalFiles），
    /// 路径：/sdcard/Android/data/&lt;pkg&gt;/files/fta.trace.log，Release 包也能 adb pull。</summary>
    private static void AttachTraceFileListener()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var dir = ctx?.GetExternalFilesDir(null)?.AbsolutePath;
            if (string.IsNullOrEmpty(dir))
                dir = ctx?.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(dir)) return;
            var path = Path.Combine(dir, "fta.trace.log");
            System.Diagnostics.Trace.AutoFlush = true;
            System.Diagnostics.Trace.Listeners.Add(
                new System.Diagnostics.TextWriterTraceListener(path));
            global::Android.Util.Log.Info("FTA.BOOT", $"fta.trace.log -> {path}");
        }
        catch (Exception ex)
        {
            // 日志不可用不影响启动
            global::Android.Util.Log.Warn("FTA.BOOT", $"AttachTraceFileListener failed: {ex.Message}");
        }
    }

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
