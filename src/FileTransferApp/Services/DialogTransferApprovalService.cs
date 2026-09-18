using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Services;

/// <summary>
/// 文件接收审批服务：局域网互传采用默认接受模型，对端发来的文件自动接收保存，
/// 不再弹窗打断用户（发送端主动发起即可视为双方已达成传输意图）。
/// </summary>
public sealed class DialogTransferApprovalService : ITransferApprovalService
{
    public Task<(bool Accepted, string? SavePath)> RequestApprovalAsync(FileMetadata metadata, string peerName)
    {
        // 返回 null 让 IStorageService.OpenWriteStreamAsync 自行拼接收目录 + 清洗文件名，
        // 避免传入完整路径导致 Sanitize 将路径分隔符替换为下划线。
        return Task.FromResult<(bool, string?)>((true, null));
    }
}