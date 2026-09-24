using Android.App;
using Android.Net;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using FileTransferApp.Android.Services;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public override void OnCreate()
        {
            // 关键：必须在 base.OnCreate() 之前完成 DI 配置。
            // base.OnCreate() 会创建 Avalonia App 并触发 OnFrameworkInitializationCompleted()，
            // 此时 ServiceLocator.Services 必须已就绪，否则 MainView 无法同步绑定 → 空白界面。
            try
            {
                var deviceName = "Android Device";
                try { deviceName = Build.Model ?? "Android Device"; } catch { }
                // 统一装配：平台服务在共享注册之后注册，可覆盖共享默认实现
                ServiceConfiguration.BuildProvider(deviceName, Core.Models.DeviceType.Android, services =>
                {
                    services.AddSingleton<IPlatformKeepAliveService>(_ => new AndroidKeepAliveService(this));
                    var storage = new AndroidStorageService(this);
                    storage.CleanupStaleTempFiles(); // 启动清理上次会话残留的 .tmp
                    services.AddSingleton<IStorageService>(storage);
                    services.AddSingleton<IFileOpenService>(_ => new AndroidFileOpenService(this));
                    // 覆盖共享的 Avalonia 文件选择器：Android 需走原生 SAF（内部惰性取 MainActivity.Current）
                    services.AddSingleton<IFilePickerService>(_ => new AndroidFilePickerService());
                    services.AddSingleton<ILogFileProvider>(_ => new AndroidLogFileProvider());
                    // 覆盖默认：Android 电池优化白名单（引导用户加入"不优化"名单保活）
                    services.AddSingleton<IBatteryOptimizationService>(_ => new AndroidBatteryOptimizationService(this));
                });
                global::Android.Util.Log.Info("FTA.BOOT", "Application.OnCreate: ConfigureServices OK (before Avalonia App init)");
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Error("FTA.BOOT", "Application.OnCreate: ConfigureServices FAIL: " + ex.Message);
            }

            // 获取 WiFi MulticastLock，确保能接收 UDP 多播/广播（Android 默认过滤）。
            // 【关键】锁对象必须由静态字段持有：若只放在局部变量里，会被 GC 回收 → 锁随之释放，
            // 表现为"本机能发心跳（对方能看到我），但收不到对方心跳（我看不到对方）"。
            AcquireMulticastLock();

            // 监听网络变化：WiFi 切换/重连后重获多播锁并重启发现服务（重建 socket + 重新加入多播组）
            RegisterConnectivityCallback();

            // 启动前台保活服务，防止 Android 后台杀进程导致 UDP/HTTP 服务中断
            try
            {
                var keepAlive = ServiceLocator.Services.GetService<IPlatformKeepAliveService>();
                keepAlive?.StartKeepAlive(
                    LocalizationService.Instance.GetString("KeepAliveTitle"),
                    LocalizationService.Instance.GetString("KeepAliveIdle"));
                global::Android.Util.Log.Info("FTA.BOOT", "Foreground keep-alive service started");
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Warn("FTA.BOOT", "KeepAlive start failed: " + ex.Message);
            }

            base.OnCreate();
        }

        /// <summary>WiFi 多播锁：必须由静态字段持有，避免被 GC 回收导致锁失效（收不到对方心跳）。</summary>
        private static global::Android.Net.Wifi.WifiManager.MulticastLock? _multicastLock;

        /// <summary>获取/维持 WiFi 多播锁（幂等；WiFi 重连后再次调用可重新持有）。</summary>
        private void AcquireMulticastLock()
        {
            try
            {
                var wifiManager = (global::Android.Net.Wifi.WifiManager?)GetSystemService(global::Android.Content.Context.WifiService);
                if (wifiManager is null) return;

                _multicastLock ??= wifiManager.CreateMulticastLock("FileTransferApp_Discovery");
                if (_multicastLock is null) return;

                _multicastLock.SetReferenceCounted(false);
                if (!_multicastLock.IsHeld) _multicastLock.Acquire();
                global::Android.Util.Log.Info("FTA.BOOT", $"MulticastLock acquired (held={_multicastLock.IsHeld})");
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Warn("FTA.BOOT", "MulticastLock failed: " + ex.Message);
            }
        }

        /// <summary>注册默认网络回调：可用/丢失时触发 OnNetworkChanged。</summary>
        private void RegisterConnectivityCallback()
        {
            try
            {
                var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
                cm?.RegisterDefaultNetworkCallback(new ConnectivityCallback(this));
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Warn("FTA.BOOT", "register connectivity callback failed: " + ex.Message);
            }
        }

        /// <summary>网络变化：重获 WiFi 多播锁并重启发现服务（重建 socket + 重新加入多播组）。</summary>
        private void OnNetworkChanged()
        {
            try
            {
                AcquireMulticastLock();
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var discovery = ServiceLocator.Services?.GetService<IDiscoveryService>();
                        if (discovery is not null) await discovery.RestartAsync().ConfigureAwait(false);
                    }
                    catch (System.Exception ex)
                    {
                        global::Android.Util.Log.Warn("FTA.BOOT", "restart discovery failed: " + ex.Message);
                    }
                });
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Warn("FTA.BOOT", "OnNetworkChanged failed: " + ex.Message);
            }
        }

        private sealed class ConnectivityCallback : ConnectivityManager.NetworkCallback
        {
            private readonly Application _app;
            public ConnectivityCallback(Application app) => _app = app;
            public override void OnAvailable(Network network) => _app.OnNetworkChanged();
            public override void OnLost(Network network) => _app.OnNetworkChanged();
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
            .WithInterFont();
        }
    }
}
