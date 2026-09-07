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
                var deviceName = "Android Device";
                try { deviceName = Build.Model ?? "Android Device"; } catch { }
                services.AddFileTransferServices(deviceName, Core.Models.DeviceType.Android);
                ServiceLocator.Services = services.BuildServiceProvider();
                global::Android.Util.Log.Info("FTA.BOOT", "Application.OnCreate: ConfigureServices OK (before Avalonia App init)");
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Error("FTA.BOOT", "Application.OnCreate: ConfigureServices FAIL: " + ex.Message);
            }

            // 获取 WiFi MulticastLock，确保能接收 UDP 多播包（Android 默认过滤多播）
            try
            {
                var wifiManager = (global::Android.Net.Wifi.WifiManager?)GetSystemService(global::Android.Content.Context.WifiService);
                if (wifiManager is not null)
                {
                    var multicastLock = wifiManager.CreateMulticastLock("FileTransferApp_Discovery");
                    if (multicastLock is not null) multicastLock.Acquire();
                    global::Android.Util.Log.Info("FTA.BOOT", "MulticastLock acquired OK");
                }
            }
            catch (System.Exception ex)
            {
                global::Android.Util.Log.Warn("FTA.BOOT", "MulticastLock failed: " + ex.Message);
            }

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

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
            .WithInterFont();
        }
    }
}
