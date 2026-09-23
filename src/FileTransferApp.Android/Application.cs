using Android.App;
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
                var services = new ServiceCollection();
                services.AddSingleton<IPlatformKeepAliveService>(_ => new AndroidKeepAliveService(this));
                services.AddSingleton<IStorageService>(_ => new AndroidStorageService(this));
                services.AddSingleton<IFileOpenService>(_ => new AndroidFileOpenService(this));
                var deviceName = "Android Device";
                try { deviceName = Build.Model ?? "Android Device"; } catch { }
                services.AddFileTransferServices(deviceName, Core.Models.DeviceType.Android);
                // 覆盖共享注册：Android 文件选择/文件打开必须走原生实现。
                // 【根因】此前这两项写在 MainActivity.ConfigureServices()，而真实 DI 在此方法内联构建，
                // 导致 IFileOpenService 无注册（双击打开→GetRequiredService 抛异常→async void 外泄→闪退），
                // 文件选择仍是共享的 AvaloniaFilePickerService（Android 上 StorageProvider 为 null → 窗口打不开）。
                // AndroidFilePickerService 内部惰性取 MainActivity.Current，因此可在此处注册。
                services.AddSingleton<IFilePickerService>(_ => new AndroidFilePickerService());
                services.AddSingleton<ILogFileProvider>(_ => new AndroidLogFileProvider());
                ServiceLocator.Services = services.BuildServiceProvider();
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

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
            .WithInterFont();
        }
    }
}
