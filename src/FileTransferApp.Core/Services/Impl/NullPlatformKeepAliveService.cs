using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 默认空实现：桌面端无需前台保活（窗口已在前台）；移动端会注入对应平台实现。
/// 也可作为单元测试与设计时预览的占位。
/// </summary>
public sealed class NullPlatformKeepAliveService : IPlatformKeepAliveService
{
    public static readonly NullPlatformKeepAliveService Instance = new();

    public void StartKeepAlive(string title, string content) { /* no-op */ }
    public void StopKeepAlive() { /* no-op */ }
}
