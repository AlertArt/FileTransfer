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

        // 核心网络服务
        services.AddSingleton<IDiscoveryService>(sp =>
            new UdpDiscoveryService(sp.GetRequiredService<IMessenger>(), deviceName, deviceType));
        services.AddSingleton<ITransferEngine, PipelinesTransferEngine>();
        services.AddSingleton<ITransferServer, TransferHttpServer>();

        // UI 通用服务
        services.AddSingleton<IThumbnailService, AvaloniaThumbnailService>();
        // 桌面端：弹窗审批（满足设计文档"等待对方同意"语义）；移动端无主窗口时内部回退自动接受
        services.AddSingleton<ITransferApprovalService, DialogTransferApprovalService>();
        services.AddSingleton<IFilePickerService, AvaloniaFilePickerService>();

        // 根视图模型
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<LogsViewModel>();

        return services;
    }
}
