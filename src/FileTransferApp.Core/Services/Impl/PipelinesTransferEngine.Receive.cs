using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Diagnostics;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>接收侧：注册任务（含审批 + 同名残留清理）→ 切片零拷贝落盘 → 完成校验/缩略图。</summary>
public sealed partial class PipelinesTransferEngine
{
    public async Task<PrepareResponse> RegisterReceiveTaskAsync(PrepareRequest prepare, DeviceNode peer)
    {
        // 续传：任务已存在 → 回传 Bitmap
        if (_tasks.TryGetValue(prepare.FileId, out var existing) && _receiveStreams.ContainsKey(prepare.FileId))
        {
            // 恢复握手：发送端再次 /prepare 即"恢复发送"信号。
            // 若本端任务处于 暂停(Paused) 或 断线(Disconnected)，必须先把状态拉回 Transferring，
            // 否则恢复后的首个切片会被 WriteChunkAsync 的 Paused/Cancelled 检查按 409 拒绝，
            // 发送端会判定失败并进入无法继续的 Disconnected（"暂停之后无法继续"的根源之一）。
            if (existing.State is TransferState.Paused or TransferState.Disconnected)
                SetState(existing, existing.State, TransferState.Transferring);
            return new PrepareResponse { Accepted = true, ReceivedChunks = existing.ReceivedChunks.ToArray() };
        }

        // 请求 UI 审批
        var metadata = new FileMetadata
        {
            FileId = prepare.FileId,
            FileName = prepare.FileName,
            FileSize = prepare.FileSize,
            ChunkSize = prepare.ChunkSize,
            Sha256 = prepare.Sha256,
            ThumbnailBase64 = prepare.ThumbnailBase64,
            ThumbnailMimeType = prepare.ThumbnailMimeType,
        };

        var (accepted, savePath) = await _approval.RequestApprovalAsync(metadata, peer.DeviceName).ConfigureAwait(false);
        if (!accepted)
        {
            return new PrepareResponse { Accepted = false, Reason = "用户拒绝接收" };
        }

        var fileName = savePath ?? prepare.FileName;
        // 保护：发送端在本端未完成时重发同名文件（携带新 FileId），旧任务仍持有 .tmp 写入流句柄，
        // 若直接 OpenWriteStreamAsync 会抛 IO_SharingViolation → /prepare 500。
        // 此处先将同名且未完成的接收任务取消并释放流，其语义由新任务取代。
        await CleanupStaleReceiveTasksByNameAsync(fileName).ConfigureAwait(false);
        var (stream, finalPath) = await _storage.OpenWriteStreamAsync(fileName, prepare.FileSize).ConfigureAwait(false);
        _receiveStreams[prepare.FileId] = stream;

        var task = new TransferTaskInfo
        {
            FileId = prepare.FileId,
            FileName = prepare.FileName,
            TotalBytes = prepare.FileSize,
            ChunkSize = prepare.ChunkSize,
            Sha256 = prepare.Sha256,
            ThumbnailBase64 = prepare.ThumbnailBase64,
            ThumbnailMimeType = prepare.ThumbnailMimeType,
            Direction = TransferDirection.Receive,
            Peer = peer,
            LocalPath = finalPath,
            State = TransferState.Created,
            StartedUtc = DateTime.UtcNow,
        };
        _tasks[prepare.FileId] = task;
        _speeds[prepare.FileId] = new SpeedCalculator();
        FtaTrace.Info("FTA.RECV", $"incoming<-{peer.DeviceName}({peer.IpAddress}) file='{prepare.FileName}' size={SpeedFormatter.FormatSize(prepare.FileSize)} fileId={prepare.FileId}");
        // 关键：必须用 SetState 更新 task.State，而非仅 EmitState。
        // 否则 task.State 仍为 Created，后续 WriteChunkAsync 的 SetState(WaitingApproval→Transferring) 会因条件不匹配而跳过。
        // 状态机要求 Created → Preparing → WaitingApproval 两步流转（与发送侧 StartSendAsync 一致），
        // 直接 Created → WaitingApproval 会抛 "非法状态流转" 异常导致 /prepare 返回 500。
        SetState(task, TransferState.Created, TransferState.Preparing);
        SetState(task, TransferState.Preparing, TransferState.WaitingApproval);

        return new PrepareResponse { Accepted = true, ReceivedChunks = Array.Empty<int>() };
    }

    public async Task<bool> WriteChunkAsync(string fileId, int chunkIndex, int totalChunks, ReadOnlyMemory<byte> data)
    {
        if (!_tasks.TryGetValue(fileId, out var task)) return false;
        if (task.State == TransferState.Paused || task.State == TransferState.Cancelled) return false;

        if (!_receiveStreams.TryGetValue(fileId, out var stream)) return false;

        // 首个切片到达 → 进入传输状态
        if (task.State == TransferState.WaitingApproval)
            SetState(task, TransferState.WaitingApproval, TransferState.Transferring);

        var offset = (long)chunkIndex * task.ChunkSize;
        if (stream.CanSeek) stream.Position = offset;

        // Pipelines 零拷贝落盘内核 (System.IO.Pipelines)
        await PipeStreamCopier.CopyAsync(new MemoryStream(data.ToArray()), stream, CancellationToken.None).ConfigureAwait(false);

        task.ReceivedChunks.Add(chunkIndex);
        task.BytesTransferred = Math.Min(task.BytesTransferred + data.Length, task.TotalBytes);

        var speed = _speeds.GetOrAdd(fileId, _ => new SpeedCalculator());
        speed.AddSample(task.BytesTransferred);
        PublishProgress(task, speed);

        // 完整性判定
        if (task.ReceivedChunks.Count >= totalChunks || task.BytesTransferred >= task.TotalBytes)
        {
            await FinalizeReceiveAsync(task).ConfigureAwait(false);
        }
        return true;
    }

    private async Task FinalizeReceiveAsync(TransferTaskInfo task)
    {
        // 关闭写入流并重命名 .tmp -> final
        CloseReceiveStream(task.FileId);
        await _storage.FinalizeWriteAsync(task.LocalPath!).ConfigureAwait(false);

        // SHA256 校验（可选，文件完整性）
        try
        {
            var actual = await _storage.ComputeSha256Async(task.LocalPath!).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(task.Sha256) &&
                !actual.Equals(task.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                SetError(task, "SHA256 校验失败", "Err.Sha256Mismatch");
                SetState(task, task.State, TransferState.Failed);
                return;
            }
        }
        catch
        {
            // 校验异常不阻断完成
        }

        task.BytesTransferred = task.TotalBytes;
        if (task.State == TransferState.Transferring)
            SetState(task, TransferState.Transferring, TransferState.Completed);

        // 接收完成后：若握手时无缩略图（非图片 / 发送端未传），尝试从落地文件重新生成缩略图
        // 这样无论发送方是否传缩略图，UI 都能展示
        if (task.State == TransferState.Completed &&
            string.IsNullOrEmpty(task.ThumbnailBase64) &&
            !string.IsNullOrEmpty(task.LocalPath))
        {
            try
            {
                var thumb = await _thumbnail.GenerateThumbnailAsync(task.LocalPath).ConfigureAwait(false);
                if (thumb is not null && thumb.Length > 0)
                {
                    var b64 = Convert.ToBase64String(thumb);
                    task.ThumbnailBase64 = b64;
                    task.ThumbnailMimeType = "image/jpeg";
                    _messenger.Send(new TransferThumbnailUpdatedMessage(task.FileId, b64, task.ThumbnailMimeType));
                }
            }
            catch
            {
                // 缩略图生成失败不影响任务已完成状态
            }
        }
    }

    /// <summary>
    /// 注册新接收任务前清理同名残留任务：发送端在本端未完成时重发同名文件（新 FileId），
    /// 旧任务仍持有 .tmp 写入流句柄，会让新的 OpenWriteStreamAsync 抛 IO_SharingViolation。
    /// 仅清理"未完成"（非终态）的任务：已完成/失败/取消的任务已释放流（FinalizeWriteAsync
    /// 将 .tmp 重命名为最终文件或 CancelWriteAsync 已删除），无需干涉，应继续展示在 UI。
    /// 注意：全程 async（此前用 GetAwaiter().GetResult() 同步阻塞接收线程，存在卡死风险）。
    /// </summary>
    private async Task CleanupStaleReceiveTasksByNameAsync(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        var stale = _tasks.Values
            .Where(t => t.Direction == TransferDirection.Receive &&
                        string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
                        !TransferStateMachine.IsTerminal(t.State))
            .ToList();
        foreach (var task in stale)
        {
            FtaTrace.Info("FTA.RECV", $"cleanup stale receive task '{fileName}' fileId={task.FileId} state={task.State}");
            CloseReceiveStream(task.FileId);

            try { task.PauseCts?.Cancel(); } catch { /* ignore */ }
            SetState(task, task.State, TransferState.Cancelled);
            if (task.LocalPath is not null)
            {
                try { await _storage.CancelWriteAsync(task.LocalPath).ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            _speeds.TryRemove(task.FileId, out _);
            _tasks.TryRemove(task.FileId, out _);
            _messenger.Send(new TransferTaskRemovedMessage(task.FileId));
        }
    }
}
