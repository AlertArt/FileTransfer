using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>桌面 / iOS 默认实现：无电池优化概念，恒为已豁免。</summary>
public sealed class NullBatteryOptimizationService : IBatteryOptimizationService
{
    public static readonly NullBatteryOptimizationService Instance = new();

    public bool IsExempt => true;
    public void RequestExemption() { /* no-op */ }
}
