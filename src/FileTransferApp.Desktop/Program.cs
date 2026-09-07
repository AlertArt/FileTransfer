using System;
using Avalonia;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Desktop.Services;
using FileTransferApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureServices();
        // 后台异步注册防火墙规则，不阻塞 UI 启动；非管理员权限时静默失败
        if (OperatingSystem.IsWindows())
            _ = Task.Run(WindowsFirewallRegistrar.EnsureInboundRule);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigureServices()
    {
        var services = new ServiceCollection();
        // 平台特定服务
        services.AddSingleton<IStorageService, DesktopStorageService>();
        // Desktop 无需前台保活（窗口在前台），注入空实现占位；移动端会注入对应平台实现
        services.AddSingleton<IPlatformKeepAliveService>(NullPlatformKeepAliveService.Instance);
        services.AddSingleton<IFileOpenService, DesktopFileOpenService>();
        // 跨平台核心 + UI 服务
        services.AddFileTransferServices(Environment.MachineName, DeviceType.Windows);
        ServiceLocator.Services = services.BuildServiceProvider();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
