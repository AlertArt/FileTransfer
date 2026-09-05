using FileTransferApp.Core.Protocols;

namespace FileTransferApp.Core.Models;

/// <summary>
/// 传输任务信息。同时承载发送端与接收端上下文，含 FSM 状态、进度与已接收切片 Bitmap。
/// </summary>
public sealed class TransferTaskInfo
{
    public string FileId { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long BytesTransferred { get; set; }
    public int ChunkSize { get; set; } = ProtocolConstants.ChunkSize;
    public string Sha256 { get; set; } = string.Empty;
    public string? ThumbnailBase64 { get; set; }
    public string? ThumbnailMimeType { get; set; }

    public TransferDirection Direction { get; set; }
    public TransferState State { get; set; } = TransferState.Created;
    public DeviceNode? Peer { get; set; }

    /// <summary>发送端源路径 / 接收端目标路径</summary>
    public string? LocalPath { get; set; }

    /// <summary>接收端临时文件路径 (.tmp)</summary>
    public string? TempPath { get; set; }

    /// <summary>接收端已收到的切片索引集合（断点续传 Bitmap）</summary>
    public HashSet<int> ReceivedChunks { get; } = new();

    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>暂停令牌源（控制本任务传输循环）</summary>
    [field: NonSerialized]
    public System.Threading.CancellationTokenSource? PauseCts { get; set; }

    public double ProgressPercentage => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesTransferred / TotalBytes * 100, 0, 100);

    public int TotalChunks => TotalBytes <= 0
        ? 0
        : (int)((TotalBytes + ChunkSize - 1) / ChunkSize);
}
