namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 配对被对端用户明确拒绝时抛出，由发送引擎转为任务失败并展示原因，
/// 与"对端为 v1 不支持配对（404 → 明文降级）"明确区分。
/// </summary>
public sealed class PairingDeniedException : Exception
{
    public PairingDeniedException(string reason) : base(reason) { }
}