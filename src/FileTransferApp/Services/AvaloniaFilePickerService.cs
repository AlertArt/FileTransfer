using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace FileTransferApp.Services;

/// <summary>
/// 基于 Avalonia StorageProvider 的跨平台文件选择器（桌面 / Android / iOS）。
/// 桌面端从 MainWindow 获取 StorageProvider；移动端从当前活动的 TopLevel 获取。
/// 关键：优先使用 MainView 自身 (UserControl) 反查 TopLevel，即可同时兼容
///   - ClassicDesktopStyleApplicationLifetime (桌面)
///   - ISingleViewApplicationLifetime (单视图 Android/iOS)
///   - IActivityApplicationLifetime (Avalonia 12 Android Activity)
/// 这三种 lifetime。
/// </summary>
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    public async Task<string[]> PickFilesAsync()
    {
        IStorageProvider? provider = null;

        // 策略 1：桌面端：ClassicDesktopStyleApplicationLifetime.MainWindow
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is not null)
        {
            provider = desktop.MainWindow.StorageProvider;
        }
        // 策略 2：单视图生命周期（Avalonia.Android / Avalonia.iOS）
        else if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView
                 && singleView.MainView is not null)
        {
            var tl = TopLevel.GetTopLevel(singleView.MainView);
            provider = tl?.StorageProvider;
        }
        // 策略 3：Activity 生命周期 (Avalonia 12 Android)：
        // 用反射拿到 IActivityApplicationLifetime.MainViewFactory 并调用，然后再走一次 GetTopLevel。
        else if (Avalonia.Application.Current?.ApplicationLifetime is not null)
        {
            var lifetime = Avalonia.Application.Current.ApplicationLifetime;
            var lifetimeType = lifetime.GetType();
            var factoryProp = lifetimeType.GetProperty("MainViewFactory");
            if (factoryProp is not null && factoryProp.GetValue(lifetime) is Delegate factory)
            {
                try
                {
                    if (factory.DynamicInvoke() is Visual v)
                    {
                        var tl = TopLevel.GetTopLevel(v);
                        provider = tl?.StorageProvider;
                    }
                }
                catch
                {
                    // ignore: 工厂可能抛出或者创建出非可视对象，退回到最后兜底
                }
            }
        }

        if (provider is null) return Array.Empty<string>();

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发送的文件",
            AllowMultiple = true
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToArray();
    }
}
