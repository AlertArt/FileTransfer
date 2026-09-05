using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Services;

/// <summary>
/// 自动审批服务：默认接受所有入站传输，保存到默认接收目录。
/// 可替换为基于 Avalonia 对话框的 UI 审批实现（保留 WaitingApproval 状态语义）。
/// </summary>
public sealed class AutoTransferApprovalService : ITransferApprovalService
{
    private readonly IStorageService _storage;

    public AutoTransferApprovalService(IStorageService storage) => _storage = storage;

    public Task<(bool Accepted, string? SavePath)> RequestApprovalAsync(FileMetadata metadata, string peerName)
    {
        // 返回 null 让 IStorageService.OpenWriteStreamAsync 自行拼接收目录 + 清洗文件名，
        // 避免传入完整路径导致 Sanitize 将路径分隔符替换为下划线。
        return Task.FromResult<(bool, string?)>((true, null));
    }
}
