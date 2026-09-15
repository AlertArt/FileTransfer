using System;
using System.Diagnostics;
using System.IO;
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
        AttachFileTraceListener();
        ConfigureServices();
        // 后台异步注册防火墙规则，不阻塞 UI 启动；非管理员权限时静默失败
        if (OperatingSystem.IsWindows())
            _ = Task.Run(WindowsFirewallRegistrar.EnsureInboundRule);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>把 Trace 输出写入 EXE 同级目录 fta.trace.log（不可写时回退到 LocalAppData）。
    /// core 服务（FTA.CTRL 控制命令、FTA.HTTP 等）与 Avalonia LogToTrace 都经 Trace, 便于用日志排查对端同步问题。</summary>
    private static void AttachFileTraceListener()
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "fta.trace.log");
            try
            {
                Directory.CreateDirectory(AppContext.BaseDirectory);
                using var probe = File.Open(logPath, FileMode.Append, FileAccess.Write);
            }
            catch
            {
                logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileTransferApp", "fta.trace.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            }
            Trace.AutoFlush = true;
            Trace.Listeners.Add(new TextWriterTraceListener(logPath));
            Trace.WriteLine($"[FTA.BOOT] trace log -> {logPath}");
        }
        catch { /* 日志不可用不影响启动 */ }
    }

    private static void ConfigureServices()
    {
        var services = new ServiceCollection();
        // 平台特定服务
        services.AddSingleton<IStorageService, DesktopStorageService>();
        // Desktop 无需前台保活（窗口在前台），注入空实现占位；移动端会注入对应平台实现
        services.AddSingleton<IPlatformKeepAliveService>(NullPlatformKeepAliveService.Instance);
        services.AddSingleton<IFileOpenService, DesktopFileOpenService>();
        services.AddSingleton<ILogFileProvider, DesktopLogFileProvider>();
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
