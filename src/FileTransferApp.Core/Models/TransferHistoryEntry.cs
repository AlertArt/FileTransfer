namespace FileTransferApp.Core.Models;

/// <summary>
/// 传输历史条目（终态任务的精简快照），持久化到磁盘以便 App 重启后仍可查看历史。
/// 不包含缩略图/切片 Bitmap 等大字段，避免历史文件膨胀。
/// </summary>
public sealed class TransferHistoryEntry
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public TransferDirection Direction { get; set; }
    public long TotalBytes { get; set; }
    public long BytesTransferred { get; set; }
    public TransferState State { get; set; }
    public string? LocalPath { get; set; }
    public string? PeerName { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public string? ErrorMessage { get; set; }
}
