using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.Services;

/// <summary>
/// 跨平台服务注册扩展。注册 Core 网络服务 + UI 通用服务（缩略图/审批/选择器/ViewModel）。
/// 平台特定服务（IStorageService / IPlatformKeepAliveService）由各平台头项目补充注册。
/// </summary>
public static class ServiceConfiguration
{
    public static IServiceCollection AddFileTransferServices(
        this IServiceCollection services,
        string deviceName,
        DeviceType deviceType)
    {
        // 消息总线（单例弱引用）
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // 单例服务以接口暴露（便于注入/测试；实现仍是进程级单例）
        services.AddSingleton<ILocalizationService>(LocalizationService.Instance);
        services.AddSingleton<IThemeService>(ThemeService.Instance);

        // 持久化存储与安全层：设备身份 + 配对记录 + 配对/加密协调
        services.AddSingleton<ISettingsStore, AppDataSettingsStore>();
        services.AddSingleton<IDeviceIdentityStore, DeviceIdentityStore>();
        services.AddSingleton<IPairingStore, PairingStore>();
        services.AddSingleton<IPairingService>(sp => new PairingService(
            sp.GetRequiredService<IDeviceIdentityStore>(),
            sp.GetRequiredService<IPairingStore>(),
            deviceName,
            deviceType.ToString()));

        // 核心网络服务
        services.AddSingleton<IDiscoveryService>(sp =>
            new UdpDiscoveryService(
                sp.GetRequiredService<IMessenger>(),
                deviceName,
                deviceType,
                sp.GetRequiredService<IDeviceIdentityStore>()));
        services.AddSingleton<ITransferEngine>(sp => new PipelinesTransferEngine(
            sp.GetRequiredService<IStorageService>(),
            sp.GetRequiredService<IThumbnailService>(),
            sp.GetRequiredService<ITransferApprovalService>(),
            sp.GetRequiredService<IMessenger>(),
            sp.GetRequiredService<IPairingService>(),
            sp.GetRequiredService<IDiscoveryService>()));
        services.AddSingleton<ITransferServer>(sp => new TransferHttpServer(
            sp.GetRequiredService<ITransferEngine>(),
            sp.GetRequiredService<IPairingService>()));

        // UI 通用服务
        services.AddSingleton<IThumbnailService, AvaloniaThumbnailService>();
        // 桌面端：弹窗审批（满足设计文档"等待对方同意"语义）；移动端无主窗口时内部回退自动接受
        services.AddSingleton<ITransferApprovalService, DialogTransferApprovalService>();
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();

        // 传输进度/状态 → 平台通知（Android 通知栏 / Windows Toast / iOS 本地通知）
        services.AddSingleton<TransferNotificationBridge>();

        // 传输历史持久化（终态任务快照，重启后仍可查看）
        services.AddSingleton<ITransferHistoryStore, JsonTransferHistoryStore>();

        // 根视图模型
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<PairingViewModel>();

        return services;
    }

    /// <summary>
    /// 共享的 DI 装配入口（各平台头统一调用，避免三份重复装配代码）：
    /// 注册跨平台服务 → 注册平台服务（在共享注册之后，可覆盖默认实现）→ 构建 provider
    /// → 赋值 <see cref="ServiceLocator.Services"/> → 预激活跨平台单例（通知桥）。
    /// </summary>
    public static IServiceProvider BuildProvider(
        string deviceName,
        DeviceType deviceType,
        Action<IServiceCollection> configurePlatform)
    {
        var services = new ServiceCollection();
        services.AddFileTransferServices(deviceName, deviceType);
        // 平台服务放在共享注册之后：可覆盖共享默认实现（如 Android 覆盖 IFilePickerService）
        configurePlatform(services);

        var provider = services.BuildServiceProvider();
        ServiceLocator.Services = provider;

        // 预激活跨平台单例：构造即注册到消息总线（传输通知桥），保证各平台一致
        try { provider.GetService<TransferNotificationBridge>(); }
        catch { /* 激活失败不影响启动 */ }

        return provider;
    }
}
