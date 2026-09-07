using Foundation;
using UIKit;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using FileTransferApp.iOS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    // Avalonia 11 iOS 基类未将 FinishedLaunching 声明为 virtual，
    // 因此服务注册放在 CustomizeAppBuilder（Avalonia 启动早期钩子）中一次性完成。
    // 首次构建 builder 时 ConfigureServices 正好初始化 ServiceLocator 单例，
    // 保证 ViewModel 层使用前已经完成注册。
    private static int _servicesConfigured;

    /// <summary>注册跨平台核心服务 + iOS 平台服务（保活 / 存储）</summary>
    private static void ConfigureServices()
    {
        if (Interlocked.CompareExchange(ref _servicesConfigured, 1, 0) != 0) return;

        var services = new ServiceCollection();
        // iOS 平台服务：前台保活（熄屏禁用 + 后台任务标识 + 用户提示）+ 沙箱 Documents 存储
        services.AddSingleton<IPlatformKeepAliveService, IOSKeepAliveService>();
        services.AddSingleton<IStorageService, IOSStorageService>();
        services.AddSingleton<IFileOpenService, IOSFileOpenService>();
        services.AddFileTransferServices(GetDeviceName(), DeviceType.iOS);
        ServiceLocator.Services = services.BuildServiceProvider();
    }

    private static string GetDeviceName()
    {
        try { return UIDevice.CurrentDevice.Name ?? "iOS Device"; }
        catch { return "iOS Device"; }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        ConfigureServices();
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
