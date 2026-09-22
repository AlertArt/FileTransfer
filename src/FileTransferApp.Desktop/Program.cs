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
            // 带大小上限 + 轮转，避免长时间运行/大文件传输把日志撑大
            Trace.Listeners.Add(new FileTransferApp.Core.Diagnostics.RollingFileTraceListener(
                logPath, maxBytes: 1_000_000, maxBackups: 3));
            Trace.WriteLine($"[FTA.BOOT] trace log -> {logPath}");
        }
        catch { /* 日志不可用不影响启动 */ }
    }

    private static void ConfigureServices()
    {
        var services = new ServiceCollection();
        // 平台特定服务
        services.AddSingleton<IStorageService, DesktopStorageService>();
        // Windows 系统 Toast 通知：传输进行中显示进度，完成/失败投放状态通知
        services.AddSingleton<IPlatformKeepAliveService, WindowsNotificationService>();
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
