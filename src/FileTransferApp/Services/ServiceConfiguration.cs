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
            sp.GetRequiredService<IPairingService>()));
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

        // 根视图模型
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<LogsViewModel>();

        return services;
    }
}
