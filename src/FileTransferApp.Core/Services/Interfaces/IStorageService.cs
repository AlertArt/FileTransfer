using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 文件存储抽象接口。
/// 桌面端直接 System.IO.File 访问；Android 10+ 走 Storage Access Framework (SAF)。
/// </summary>
public interface IStorageService
{
    /// <summary>发送端打开源文件读取流</summary>
    Task<Stream> OpenReadStreamAsync(string fileIdentifier);

    /// <summary>接收端打开目标写入流（按 totalSize 预分配/截断）。写入 finalPath+".tmp" 临时文件，返回 (写入流, 最终路径)。</summary>
    Task<(Stream Stream, string FinalPath)> OpenWriteStreamAsync(string fileName, long totalSize);

    /// <summary>完成写入：刷新并重命名 .tmp -> final（接收端校验通过后调用）</summary>
    Task FinalizeWriteAsync(string finalPath);

    /// <summary>取消写入：删除 .tmp 临时文件，防止垃圾残留</summary>
    Task CancelWriteAsync(string finalPath);

    /// <summary>获取默认接收目录</summary>
    string GetDefaultReceiveDirectory();

    /// <summary>文件是否存在</summary>
    bool FileExists(string path);

    /// <summary>计算文件 SHA256（用于握手与完整性校验）</summary>
    Task<string> ComputeSha256Async(string filePath);

    /// <summary>获取文件大小</summary>
    long GetFileSize(string filePath);
}

/// <summary>接收审批策略接口：UI 决定是否同意接收</summary>
public interface ITransferApprovalService
{
    /// <summary>询问是否接收。返回 (是否同意, 是否覆盖已存在文件)。</summary>
    Task<(bool Accepted, string? SavePath)> RequestApprovalAsync(FileMetadata metadata, string peerName);
}
