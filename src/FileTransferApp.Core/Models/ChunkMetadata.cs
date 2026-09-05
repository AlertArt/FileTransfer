namespace FileTransferApp.Core.Models;

/// <summary>单个切片元数据：索引、偏移、长度、哈希</summary>
public readonly record struct ChunkMetadata(int Index, long Offset, int Length, string Hash);

/// <summary>/chunk 接口响应</summary>
public sealed class ChunkResponse
{
    public int ChunkIndex { get; set; }
    public string Status { get; set; } = "Success";
}

/// <summary>/control 接口请求体</summary>
public sealed class ControlRequest
{
    public string FileId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // PAUSE | RESUME | CANCEL
}

/// <summary>/control 接口响应</summary>
public sealed class ControlResponse
{
    public string Status { get; set; } = "Acknowledged";
}

/// <summary>统一错误响应</summary>
public sealed class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
