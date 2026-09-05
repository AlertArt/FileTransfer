using System.Text.Json.Serialization;
using FileTransferApp.Core.Protocols;

namespace FileTransferApp.Core.Models;

/// <summary>
/// 文件元数据。传输前在 /prepare 握手中交换：文件名、总大小、SHA256、切片大小与缩略图。
/// </summary>
public sealed class FileMetadata
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int ChunkSize { get; set; } = ProtocolConstants.ChunkSize;
    public string Sha256 { get; set; } = string.Empty;
    public string? ThumbnailBase64 { get; set; }
    public string? ThumbnailMimeType { get; set; }

    [JsonIgnore]
    public int TotalChunks => FileSize <= 0
        ? 0
        : (int)((FileSize + ChunkSize - 1) / ChunkSize);
}

/// <summary>/prepare 请求体</summary>
public sealed class PrepareRequest
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int ChunkSize { get; set; } = ProtocolConstants.ChunkSize;
    public string Sha256 { get; set; } = string.Empty;
    public string? ThumbnailBase64 { get; set; }
    public string? ThumbnailMimeType { get; set; }
}

/// <summary>/prepare 响应体：接收端是否同意并回传已存在的切片索引以进行续传</summary>
public sealed class PrepareResponse
{
    public bool Accepted { get; set; }
    public int[] ReceivedChunks { get; set; } = Array.Empty<int>();
    public string? Reason { get; set; }
}
