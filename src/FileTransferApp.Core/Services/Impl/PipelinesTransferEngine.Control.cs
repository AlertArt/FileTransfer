using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Diagnostics;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>控制命令：暂停 / 恢复 / 重试 / 取消 / 删除，以及对端控制命令的本地应用与回传。</summary>
public sealed partial class PipelinesTransferEngine
{
    public Task PauseAsync(string fileId)
    {
        if (_tasks.TryGetValue(fileId, out var task) && task.State == TransferState.Transferring)
        {
            SetState(task, TransferState.Transferring, TransferState.Paused);
            try { task.PauseCts?.Cancel(); } catch { /* ignore */ }
            // 告知对端暂停：否则对端仍处于 Transferring，继续发片，本端/对端状态失同步
            NotifyPeerControlAsync(task, TransferAction.PAUSE);
        }
        return Task.CompletedTask;
    }

    public async Task ResumeAsync(string fileId)
    {
        if (!_tasks.TryGetValue(fileId, out var task)) return;
        if (task.State != TransferState.Paused) return;

        if (task.Direction == TransferDirection.Receive)
        {
            // 接收方向是被动方，无法自行续推：本地"恢复" = 同步状态并通知发送端重启推送。
            // 发送端收到控制 RESUME 会进入 ResumeSendAsync（重新握手 + 推送缺失切片）。
            SetState(task, TransferState.Paused, TransferState.Transferring);
            NotifyPeerControlAsync(task, TransferAction.RESUME);
            return;
        }

        await ResumeSendAsync(task).ConfigureAwait(false);
    }

    public async Task RetryAsync(string fileId)
    {
        if (!_tasks.TryGetValue(fileId, out var task)) return;
        if (task.State is not (TransferState.Disconnected or TransferState.Failed)) return;

        if (task.Direction == TransferDirection.Receive)
        {
            // 接收方向是被动方：同步状态并通知发送端重新推送。
            // 发送端若仍保留任务（Disconnected/Failed）会经 ResumeSendAsync 重新握手；
            // 若任务已不存在，接收端会重新 /prepare 走全新注册（审批 + 全量重收）。
            SetState(task, task.State, TransferState.Transferring);
            NotifyPeerControlAsync(task, TransferAction.RESUME);
            return;
        }

        await ResumeSendAsync(task).ConfigureAwait(false);
    }

    public async Task CancelAsync(string fileId)
    {
        if (!_tasks.TryGetValue(fileId, out var task)) return;
        if (TransferStateMachine.IsTerminal(task.State)) return;

        try { task.PauseCts?.Cancel(); } catch { /* ignore */ }
        TryTransition(task, TransferState.Cancelled);
        NotifyPeerControlAsync(task, TransferAction.CANCEL);

        // 接收端：删除 .tmp 临时文件
        if (task.Direction == TransferDirection.Receive && task.LocalPath is not null)
        {
            try { await _storage.CancelWriteAsync(task.LocalPath).ConfigureAwait(false); }
            catch { /* ignore */ }
            CloseReceiveStream(fileId);
        }
    }

    public async Task RemoveTaskAsync(string fileId)
    {
        if (!_tasks.TryGetValue(fileId, out var task)) return;
        // 非终态先取消（保证资源被清理；CancelAsync 会同步对端）
        if (!TransferStateMachine.IsTerminal(task.State))
            await CancelAsync(fileId).ConfigureAwait(false);
        CloseReceiveStream(fileId);
        _speeds.TryRemove(fileId, out _);
        _tasks.TryRemove(fileId, out _);
        // 通知对端删除关联任务（对端任务若仍存在会一并取消并清理，卡片同步移除）
        NotifyPeerControlAsync(task, TransferAction.REMOVE);
        // 通知 UI 从列表移除
        _messenger.Send(new Messaging.TransferTaskRemovedMessage(fileId));
    }

    public Task ApplyControlAsync(string fileId, TransferAction action)
    {
        if (!_tasks.TryGetValue(fileId, out var task))
        {
            FtaTrace.Warn("FTA.CTRL", $"<- {action} {fileId}: task not found (server-side)");
            return Task.CompletedTask;
        }
        FtaTrace.Info("FTA.CTRL", $"<- {action} {fileId} state={task.State} dir={task.Direction}");
        switch (action)
        {
            case TransferAction.PAUSE:
                if (task.State == TransferState.Transferring)
                    SetState(task, TransferState.Transferring, TransferState.Paused);
                else
                    FtaTrace.Warn("FTA.CTRL", $"<- PAUSE {fileId} skipped (state={task.State})");
                break;
            case TransferAction.RESUME:
                // 发送方向：对端恢复 = 重启本端续传（重新握手 + 推送缺失切片）。
                // 否则只翻状态不发片，发送端会停在 Transferring 空等，对端也永远等不到剩余切片。
                // 接收方向：仅同步状态（对端既是发送方，会自行重启推送）。
                if (task.Direction == TransferDirection.Send)
                    _ = ResumeSendAsync(task);
                else if (task.State == TransferState.Paused)
                    SetState(task, TransferState.Paused, TransferState.Transferring);
                break;
            case TransferAction.CANCEL:
                _ = CancelAsync(fileId);
                break;
            case TransferAction.REMOVE:
                // 对端删除任务 → 本端连取消带清理一并移除（RemoveTaskAsync 内部对非终态先取消）
                _ = RemoveTaskAsync(fileId);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 向对端发送控制命令（/control）：暂停/恢复/取消都需同步给对端，否则两端状态失同步。
    /// /control 端点在接收端早已实现（ApplyControlAsync），却从未被发送侧调用。
    /// fire-and-forget + 全程吞异常：对端离线/超时静默，绝不影响本端状态机。
    /// </summary>
    private void NotifyPeerControlAsync(TransferTaskInfo task, TransferAction action)
    {
        if (task.Peer?.IpAddress is null)
        {
            FtaTrace.Warn("FTA.CTRL", $"skip->{action} {task.FileId}: peer IP is null");
            return;
        }
        try
        {
            // 接收方向：Peer 来自 BuildPeer（TCP 源端口，临时端口），但目标设备的传输服务固定监听
            // ProtocolConstants.TransferPort；发送方向：Peer 来自设备发现（Port=53318）。
            var port = task.Direction == TransferDirection.Receive
                ? ProtocolConstants.TransferPort
                : task.Peer.Port;
            var target = $"http://{task.Peer.IpAddress}:{port}{ProtocolConstants.PathControl}";
            var cmd = new ControlRequest { FileId = task.FileId, Action = action.ToString() };
            FtaTrace.Info("FTA.CTRL", $"-> {action} {task.FileId} ({task.Direction}) {target}");
            _ = Task.Run(async () =>
            {
                try
                {
                    // 同上：PostAsJsonAsync 以 chunked 发送、服务器读不到 body → 400，
                    // 改用 ByteArrayContent 携带 Content-Length。v2 配对设备间同样加密。
                    var secCtx = await GetSendSecurityContextAsync(task).ConfigureAwait(false);
                    using var content = BuildSecureContent(cmd, secCtx);
                    using var resp = await _http.PostAsync(target, content).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    FtaTrace.Info("FTA.CTRL", $"-> {action} {task.FileId} OK");
                }
                catch (Exception ex)
                {
                    FtaTrace.Warn("FTA.CTRL", $"-> {action} {task.FileId} FAIL: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            FtaTrace.Warn("FTA.CTRL", $"-> {action} {task.FileId} setup FAIL: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
