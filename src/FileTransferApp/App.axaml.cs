using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using FileTransferApp.ViewModels;
using FileTransferApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp;

public partial class App : Application
{
    /// <summary>平台绑定后的根视图（桌面=MainWindow，移动=MainView）。
    /// 供弹窗类服务跨平台反查 TopLevel 窗口，无需平台专属引用。</summary>
    internal static Avalonia.Visual? CurrentRootView { get; private set; }

    public override void Initialize()
    {
        // 中文回退字体：Android 端（及桌面端）缺少 CJK 字形时回退到内嵌的 Noto Sans CJK SC，
        // 否则 Android 平板上中文会渲染成豆腐块（□□□）。
        // 必须在 FontManager 首次构造（首次文本渲染）前完成，见 FontFallbackRegistrar。
        FontFallbackRegistrar.Register();
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 恢复用户上次选择的语言（无记录时保持默认；失败静默回退）
        LocalizationService.Instance.InitializePersistedLanguage();
        // 恢复用户上次选择的主题（无记录时跟随系统；失败静默回退）
        ThemeService.Instance.InitializePersistedTheme();
        BootDiagnostics.Log("App.OnFrameworkInit ENTER. ServiceLocator=" + ServiceLocator.Services.GetType().FullName);
        // 关键修复：Android 上 ConfigureServices 已在 base.OnCreate 前执行，DI 此时已就绪。
        // 必须在 base.OnFrameworkInitializationCompleted() 之前同步绑定 MainView，
        // 否则 base 方法创建渲染表面时 MainView 为 null，导致 Android 上看不到界面。
        if (!(ServiceLocator.Services is EmptyServiceProvider))
        {
            try
            {
                var vm = ServiceLocator.Services.GetRequiredService<MainViewModel>();
                BindMainView(vm);
                BootDiagnostics.Log("App.OnFrameworkInit: MainView bound synchronously OK.");
            }
            catch (Exception ex)
            {
                BootDiagnostics.Error("App.OnFrameworkInit: synchronous bind FAIL: " + ex);
            }
        }
        else
        {
            // 回退：DI 尚未就绪（理论不应发生），异步等待绑定
            BootDiagnostics.Warn("App.OnFrameworkInit: DI not ready, falling back to async bind.");
            _ = WaitForDiAndBindMainViewAsync();
        }
        _ = StartBackgroundServicesAsync(); // 内部也会等 DI
        // 首次渲染后验证内嵌中文字体可解析（桌面端提前暴露 URI 写错/资源缺失等问题）
        Avalonia.Threading.Dispatcher.UIThread.Post(FontFallbackRegistrar.VerifyEmbeddedFont);
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task WaitForDiAndBindMainViewAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sp = await WaitForDiOrTimeoutAsync(cts.Token).ConfigureAwait(false);
            if (sp == null)
            {
                BootDiagnostics.Warn("App.OnFrameworkInit: DI not ready within 10s. MainView binding SKIPPED.");
                return;
            }

            var vm = sp.GetRequiredService<MainViewModel>();
            // 绑定必须回到 UI 线程，否则 Avalonia 属性写入会抛 "Called from invalid thread"
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                BindMainView(vm);
            }
            else
            {
                var tcs = new TaskCompletionSource<bool>();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { BindMainView(vm); tcs.TrySetResult(true); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
                await tcs.Task.ConfigureAwait(false);
            }

            BootDiagnostics.Log("App.OnFrameworkInit: MainView bound OK.");
        }
        catch (Exception ex)
        {
            BootDiagnostics.Error("App.OnFrameworkInit: binding MainView FAIL: " + ex);
        }
    }

    private static async Task<IServiceProvider?> WaitForDiOrTimeoutAsync(CancellationToken cancel)
    {
        if (!(ServiceLocator.Services is EmptyServiceProvider))
            return ServiceLocator.Services;

        try
        {
            var delay = Task.Delay(Timeout.InfiniteTimeSpan, cancel);
            var winner = await Task.WhenAny(ServiceLocator.Initialized, delay).ConfigureAwait(false);
            if (winner == ServiceLocator.Initialized)
                return await ServiceLocator.Initialized.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* timeout */ }
        return null;
    }

    private static void BindMainView(MainViewModel vm)
    {
        // 激活「传输进度/状态 → 平台通知」桥：构造即注册到消息总线；
        // 单例由 DI 容器持有，解析一次后不会被 GC（WeakReferenceMessenger 是弱引用）。
        try { ServiceLocator.Services?.GetService<TransferNotificationBridge>(); }
        catch (Exception ex) { BootDiagnostics.Warn("BindMainView: resolve TransferNotificationBridge FAIL: " + ex.Message); }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var win = new MainWindow { DataContext = vm };
            CurrentRootView = win;
            desktop.MainWindow = win;
        }
        else if (Avalonia.Application.Current?.ApplicationLifetime is IActivityApplicationLifetime activityFactory)
        {
            // 复用同一实例：不可在每次 MainViewFactory 调用时 new，否则弹窗服务反查 TopLevel 时拿到未附加视图
            var view = new MainView { DataContext = vm };
            CurrentRootView = view;
            activityFactory.MainViewFactory = () => view;
        }
        else if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var view = new MainView { DataContext = vm };
            CurrentRootView = view;
            singleView.MainView = view;
        }
        else
        {
            BootDiagnostics.Log("App.BindMainView unknown ApplicationLifetime: " +
                                (Avalonia.Application.Current?.ApplicationLifetime?.GetType().FullName ?? "null"));
        }
    }

    /// <summary>启动 UDP 设备发现与 HTTP 传输服务。
    /// 关键：Android 上 StrictMode 禁止主线程执行 new Socket / Bind / Listen，
    /// 因此整个启动必须放到 Task.Run 切到线程池线程执行，
    /// 否则在部分定制 ROM（小米/华为/OPPO 等）上会触发 NetworkOnMainThreadException 直接闪退。
    /// 关键修复：discovery 和 server 必须放在独立 try-catch 中，
    /// 否则 UDP 端口绑定失败会导致 HTTP 传输服务也永不启动 → Android 无法接收文件。
    /// </summary>
    private static Task StartBackgroundServicesAsync()
    {
        return Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var sp = await WaitForDiOrTimeoutAsync(cts.Token).ConfigureAwait(false);
                if (sp == null)
                {
                    BootDiagnostics.Warn("StartBackgroundServicesAsync SKIPPED: DI not ready within 15s");
                    return;
                }

                // ① UDP 发现服务：即使失败也不阻止 HTTP 传输服务启动
                try
                {
                    var discovery = sp.GetRequiredService<IDiscoveryService>();
                    BootDiagnostics.Log($"Calling IDiscoveryService.StartAsync ({discovery.GetType().Name})...");
                    await discovery.StartAsync().ConfigureAwait(false);
                    BootDiagnostics.Log("IDiscoveryService.StartAsync OK.");
                }
                catch (Exception ex)
                {
                    BootDiagnostics.Error("Discovery StartAsync FAIL (non-fatal, transfer server will still start): " + ex);
                }

                // ② HTTP 传输服务：必须独立启动，不能被 discovery 失败连带
                try
                {
                    var server = sp.GetRequiredService<ITransferServer>();
                    BootDiagnostics.Log("Calling ITransferServer.StartAsync...");
                    await server.StartAsync().ConfigureAwait(false);
                    BootDiagnostics.Log("ITransferServer.StartAsync OK. Background services ready.");
                }
                catch (Exception ex)
                {
                    BootDiagnostics.Error("TransferServer StartAsync FAIL: " + ex);
                }
            }
            catch (Exception ex)
            {
                BootDiagnostics.Error("Background services outer FAIL: " + ex);
            }
        });
    }

    /// <summary>
    /// 平台无关的启动诊断写入器：
    /// - 运行时存在 Android.Util.Log 则写 logcat INFO (tag=FTA.BOOT)
    /// - 否则写 Trace.WriteLine (tag=FTA.BOOT)
    /// 不依赖 DI，不依赖 ANDROID 编译常量（FileTransferApp 是单 TFM net10.0）。
    /// </summary>
    internal static class BootDiagnostics
    {
        private const string Tag = "FTA.BOOT";

        private static readonly Action<string, string>? _androidLogInfo = ResolveAndroidLog("Info");
        private static readonly Action<string, string>? _androidLogWarn = ResolveAndroidLog("Warn");
        private static readonly Action<string, string>? _androidLogError = ResolveAndroidLog("Error");

        public static void Log(string msg)
        {
            try { Core.Diagnostics.FtaTrace.Info(Tag, msg); } catch { /* ignore */ }
            try { _androidLogInfo?.Invoke(Tag, msg); } catch { /* ignore */ }
        }

        public static void Warn(string msg)
        {
            try { Core.Diagnostics.FtaTrace.Warn(Tag, msg); } catch { /* ignore */ }
            try { _androidLogWarn?.Invoke(Tag, msg); } catch { /* ignore */ }
        }

        public static void Error(string msg)
        {
            try { Core.Diagnostics.FtaTrace.Warn(Tag, $"ERROR {msg}"); } catch { /* ignore */ }
            try { _androidLogError?.Invoke(Tag, msg); } catch { /* ignore */ }
        }

        /// <summary>在运行时探测 global::Android.Util.Log.{method}(string tag, string msg)。</summary>
        private static Action<string, string>? ResolveAndroidLog(string method)
        {
            try
            {
                var logType = Type.GetType("Android.Util.Log, Mono.Android", throwOnError: false);
                logType ??= Type.GetType("Android.Util.Log, Mono.Android.Runtime", throwOnError: false);
                logType ??= Type.GetType("Android.Util.Log", throwOnError: false);
                if (logType == null) return null;
                var mi = logType.GetMethod(method, new[] { typeof(string), typeof(string) });
                if (mi == null || !mi.IsStatic) return null;
                return (tag, msg) => mi.Invoke(null, new object?[] { tag, msg });
            }
            catch
            {
                return null;
            }
        }
    }
}
