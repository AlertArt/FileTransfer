using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.Services;

/// <summary>
/// 服务定位器。各平台头项目在启动时构建 IServiceProvider 并赋值，
/// 供 Avalonia App.axaml.cs 在框架初始化完成后解析根视图模型与启动后台服务。
///
/// 启动时序说明（尤其 Android / iOS 等移动平台）：
/// - Avalonia.AppBuilder.Setup → App.Initialize → App.OnFrameworkInitializationCompleted
///   可能在平台入口 Program/MainActivity/AppDelegate 构建 DI 容器之前就同步触发。
///   因此赋值 Services 后还必须触发 <see cref="Initialized"/> 信号，
///   让 OnFrameworkInit 上的"等待 DI 就绪"循环能正确取消等待并绑定视图。
/// </summary>
public static class ServiceLocator
{
    private static readonly TaskCompletionSource<IServiceProvider> _initializedTcs = new();

    public static IServiceProvider Services
    {
        get => _services;
        set
        {
            _services = value ?? throw new ArgumentNullException(nameof(value));
            if (value is not EmptyServiceProvider)
                _initializedTcs.TrySetResult(value);
        }
    }
    private static IServiceProvider _services = EmptyServiceProvider.Instance;

    /// <summary>当且仅当 Services 被赋值为一个非空容器后完成。</summary>
    public static Task<IServiceProvider> Initialized => _initializedTcs.Task;

    public static T GetRequiredService<T>() where T : notnull
        => Services.GetRequiredService<T>();

    public static T? GetService<T>() where T : notnull
        => Services.GetService<T>();
}

internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();

    // GetRequiredService 在 Microsoft.Extensions.DependencyInjection 中
    // 找不到服务时本就抛 InvalidOperationException；这里保持一致语义，
    // 让 GetService 也抛异常，避免上游 GetRequiredService 返回 null 后空引用
    public object? GetService(System.Type serviceType)
        => throw new InvalidOperationException(
            $"Service {serviceType.FullName} is not registered. Call ConfigureServices in the " +
            "platform entry point (Program/AppDelegate/MainActivity) before resolving services.");
}
