using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 传输历史持久化：保存终态任务的精简快照，App 重启后仍可查看。
/// 实现需自带上限与去重（同 FileId 覆盖）。
/// </summary>
public interface ITransferHistoryStore
{
    /// <summary>按时间倒序返回全部历史（最新在前）。</summary>
    IReadOnlyList<TransferHistoryEntry> GetAll();

    /// <summary>新增或按 FileId 覆盖一条历史。</summary>
    void AddOrUpdate(TransferHistoryEntry entry);

    /// <summary>清空全部历史。</summary>
    void Clear();
}
