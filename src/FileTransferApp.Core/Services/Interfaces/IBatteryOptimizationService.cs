namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 电池优化白名单（保活）：Android 上引导用户把本应用加入"不优化"名单，
/// 避免后台被系统冻结/清理导致 UDP/HTTP 通讯中断。非 Android 平台恒为已豁免。
/// </summary>
public interface IBatteryOptimizationService
{
    /// <summary>是否已豁免电池优化（非 Android 平台恒为 true）。</summary>
    bool IsExempt { get; }

    /// <summary>请求/引导用户豁免电池优化（非 Android 平台 no-op）。</summary>
    void RequestExemption();
}
