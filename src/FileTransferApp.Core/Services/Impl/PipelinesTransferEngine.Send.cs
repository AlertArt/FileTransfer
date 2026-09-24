using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Diagnostics;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>发送侧：创建任务 → 计算哈希/缩略图 → /prepare 握手（含 401 自动重配）→ 切片推送 → 续传/重试。</summary>
public sealed partial class PipelinesTransferEngine
{
    public async Task<TransferTaskInfo> CreateSendTaskAsync(string filePath, DeviceNode peer, CancellationToken ct = default)
    {
        // 快速路径：只获取文件名 + 大小，立即返回任务对象，让 UI 第一时间展示任务卡片。
        // 大文件的 SHA-256 全文件哈希与缩略图生成移到 StartSendAsync 的 Preparing 阶段后台计算，
        // 避免用户选完大文件后"界面无反应很久"。
        var size = await Task.Run(() => _storage.GetFileSize(filePath), ct).ConfigureAwait(false);

        var task = new TransferTaskInfo
        {
            FileId = Guid.NewGuid().ToString("N"),
            FileName = Path.GetFileName(filePath),
            TotalBytes = size,
            ChunkSize = ProtocolConstants.ChunkSize,
            Sha256 = string.Empty, // 延迟到 StartSendAsync 中计算
            ThumbnailBase64 = null,
            ThumbnailMimeType = null,
            Direction = TransferDirection.Send,
            Peer = peer,
            LocalPath = filePath,
            State = TransferState.Created,
            StartedUtc = DateTime.UtcNow,
        };
        _tasks[task.FileId] = task;
        _speeds[task.FileId] = new SpeedCalculator();
        FtaTrace.Info("FTA.SEND", $"start->{peer.DeviceName}({peer.IpAddress}) file='{task.FileName}' size={SpeedFormatter.FormatSize(size)} fileId={task.FileId}");
        return task;
    }

    public async Task StartSendAsync(string fileId, CancellationToken ct = default)
    {
        if (_tasks.TryGetValue(fileId, out var task) is false) return;

        // 发送并发上限：超过则排队等待（避免同时开启过多传输占满带宽/内存）
        await _sendSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartSendCoreAsync(task, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendSlots.Release();
        }
    }

    private async Task StartSendCoreAsync(TransferTaskInfo task, CancellationToken ct)
    {
        SetState(task, TransferState.Created, TransferState.Preparing);

        // Preparing 阶段：在后台线程计算 SHA-256 全文件哈希 + 生成缩略图。
        // 大文件（数百 MB ~ GB）此步骤耗时较长，放在这里而不是 CreateSendTaskAsync，
        // 让任务卡片能立即出现在 UI 上（状态显示"握手中"），用户不再"选完文件傻等"。
        try
        {
            // 首包延迟优化：v2 加密传输每片由 AES-GCM 保证完整性，无需发送前对整文件做 SHA-256
            // （数百 MB~GB 的预哈希会把"开始传输"延迟数十秒）。仅对 v1 明文对端保留整文件预哈希。
            var needFullHash = (task.Peer?.ProtocolVersion ?? 1) < ProtocolConstants.ProtocolVersion;
            if (!needFullHash)
                FtaTrace.Info("FTA.SEND", $"skip full-file SHA256 (v2 encrypted peer) fileId={task.FileId}");

            var (sha, thumb) = await Task.Run(async () =>
            {
                var hash = needFullHash
                    ? await _storage.ComputeSha256Async(task.LocalPath!).ConfigureAwait(false)
                    : string.Empty;
                var thumbnail = await _thumbnail.GenerateThumbnailAsync(task.LocalPath!).ConfigureAwait(false);
                return (hash, thumbnail);
            }, ct).ConfigureAwait(false);

            task.Sha256 = sha;
            task.ThumbnailBase64 = thumb is null ? null : Convert.ToBase64String(thumb);
            task.ThumbnailMimeType = thumb is null ? null : "image/jpeg";

            // 缩略图计算完成后通知 UI 更新（任务卡片从扩展名图标切换为真实缩略图）
            if (task.ThumbnailBase64 is not null)
                _messenger.Send(new TransferThumbnailUpdatedMessage(task.FileId, task.ThumbnailBase64, task.ThumbnailMimeType));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SetError(task, $"准备失败: {ex.Message}", "Err.PrepareFailure", ex.Message);
            SetState(task, task.State, TransferState.Failed);
            return;
        }

        RefreshPeerFromDiscovery(task);
        var baseUri = $"http://{task.Peer!.IpAddress}:{task.Peer.Port}";

        // Metadata 握手
        var prepareReq = new PrepareRequest
        {
            FileId = task.FileId,
            FileName = task.FileName,
            FileSize = task.TotalBytes,
            ChunkSize = task.ChunkSize,
            Sha256 = task.Sha256,
            ThumbnailBase64 = task.ThumbnailBase64,
            ThumbnailMimeType = task.ThumbnailMimeType,
        };

        SetState(task, TransferState.Preparing, TransferState.WaitingApproval);

        PrepareResponse? prep;
        PeerSecurityContext? secCtx;
        try
        {
            // 握手（含 401 自动重新配对重试）。不用 PostAsJsonAsync（可能 chunked → 服务器读不到 body → 400），
            // 手动序列化为 byte[] 走 ByteArrayContent，自动携带 Content-Length。
            var (resp, ctx) = await PostPrepareWithRepairAsync(task, baseUri, prepareReq, ct).ConfigureAwait(false);
            secCtx = ctx;
            using (resp)
            {
                // 不要直接 EnsureSuccessStatusCode：把非 2xx 响应体读出作为 ErrorMessage，
                // 以便用户直观看出对端拒绝的原因
                if (!resp.IsSuccessStatusCode)
                {
                    string? reason = null;
                    try { reason = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }
                    if (!string.IsNullOrEmpty(reason) &&
                        (reason.Contains('{') || reason.StartsWith("\"") || reason.Length > 300))
                    {
                        // 如果是 JSON 或过长文本，降级展示 HTTP 状态码
                        SetError(task, $"握手失败 HTTP {(int)resp.StatusCode} ({baseUri})", "Err.HandshakeHttp", (int)resp.StatusCode, baseUri);
                    }
                    else if (string.IsNullOrEmpty(reason))
                    {
                        SetError(task, $"握手失败 HTTP {(int)resp.StatusCode} ({baseUri})", "Err.HandshakeHttp", (int)resp.StatusCode, baseUri);
                    }
                    else
                    {
                        SetError(task, $"握手被拒({(int)resp.StatusCode}): {reason.Trim()}", "Err.HandshakeRejected", (int)resp.StatusCode, reason.Trim());
                    }
                    SetState(task, task.State, TransferState.Failed);
                    return;
                }
                prep = await resp.Content.ReadFromJsonAsync<PrepareResponse>(cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (PairingDeniedException ex)
        {
            // 对端用户明确拒绝了配对请求：中止发送（不降级明文）
            SetError(task, $"对方拒绝了配对请求: {ex.Message}", "Err.PairingDenied", ex.Message);
            SetState(task, task.State, TransferState.Cancelled);
            return;
        }
        catch (Exception ex)
        {
            // HttpRequestException/SocketException: 通常是 IP:port 不可达、目标未启动 HTTP 服务、防火墙拒绝
            var detail = ex.InnerException?.Message ?? ex.Message;
            SetError(task, $"连接失败({baseUri}): {detail}", "Err.ConnectFailed", baseUri, detail);
            SetState(task, task.State, TransferState.Failed);
            return;
        }

        if (prep is null || !prep.Accepted)
        {
            if (string.IsNullOrEmpty(prep?.Reason))
                SetError(task, "对方拒绝接收 (未说明原因)", "Err.PeerRejected");
            else
                SetError(task, $"对方拒绝: {prep.Reason}", "Err.PeerRejectedReason", prep.Reason);
            SetState(task, task.State, TransferState.Cancelled);
            return;
        }

        // 等待审批通过 → 进入传输
        SetState(task, TransferState.WaitingApproval, TransferState.Transferring);
        task.PauseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await SendBatchAsync(task, baseUri, prep.ReceivedChunks ?? Array.Empty<int>(), secCtx, task.PauseCts.Token).ConfigureAwait(false);

        // 全部完成
        if (task.State == TransferState.Transferring)
        {
            task.BytesTransferred = task.TotalBytes;
            SetState(task, TransferState.Transferring, TransferState.Completed);
        }
    }

    private async Task SendBatchAsync(TransferTaskInfo task, string baseUri, int[] peerReceived, PeerSecurityContext? secCtx, CancellationToken token)
    {
        var speed = _speeds.GetOrAdd(task.FileId, _ => new SpeedCalculator());
        // 基线进度：对端已接收的切片
        task.BytesTransferred = Math.Min((long)peerReceived.Length * task.ChunkSize, task.TotalBytes);

        var totalChunks = task.TotalChunks;
        var missing = ChunkBitmap.ComputeMissing(totalChunks, peerReceived);

        await using var fs = await _storage.OpenReadStreamAsync(task.LocalPath!).ConfigureAwait(false);
        if (!fs.CanSeek) throw new InvalidOperationException("发送端读取流必须支持随机定位 (Seek)。");

        var buf = new byte[task.ChunkSize];
        foreach (var idx in missing)
        {
            if (token.IsCancellationRequested) break;
            if (task.State != TransferState.Transferring) break;

            var offset = (long)idx * task.ChunkSize;
            fs.Position = offset;
            var len = (int)Math.Min(task.ChunkSize, task.TotalBytes - offset);
            var read = 0;
            while (read < len)
            {
                var n = await fs.ReadAsync(buf.AsMemory(read, len - read), token).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }

            var hash = ComputeChunkHash(buf, read);
            try
            {
                // 带宽节流：达到上限时在此等待，保持平均速率
                await _throttle.AcquireAsync(read, token).ConfigureAwait(false);
                await PostChunkAsync(task, baseUri, idx, hash, buf, read, secCtx, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpRequestException hre) when (hre.StatusCode == HttpStatusCode.Conflict)
            {
                // 对端任务处于 Paused/Cancelled（/chunk 返回 409 "已暂停或已取消，拒绝写入"）。
                // 此前本端会直接判为 Disconnected → UI 没有"继续"入口，传输彻底卡死。
                // 现在将对端暂停信号同步为本端 Paused，用户点"继续"即可无缝续传。
                TryTransition(task, TransferState.Paused);
                return;
            }
            catch (Exception ex)
            {
                SetError(task, $"切片 {idx} 发送失败: {ex.Message}", "Err.ChunkSendFailed", idx, ex.Message);
                TryTransition(task, TransferState.Disconnected);
                return;
            }

            task.BytesTransferred += read;
            speed.AddSample(task.BytesTransferred);
            PublishProgress(task, speed);
        }
    }

    private async Task PostChunkAsync(TransferTaskInfo task, string baseUri, int idx, string hash, byte[] buf, int len, PeerSecurityContext? secCtx, CancellationToken token)
    {
        using var content = BuildSecureContent(new ReadOnlyMemory<byte>(buf, 0, len).ToArray(), secCtx);
        content.Headers.Add(ProtocolConstants.HeaderFileId, task.FileId);
        content.Headers.Add(ProtocolConstants.HeaderChunkIndex, idx.ToString());
        if (secCtx?.IsEncrypted != true)
        {
            // 明文（v1 兼容）路径保留 X-Chunk-Hash 供调试；v2 加密路径不再发送明文哈希（GCM 已保证完整性）
            content.Headers.Add(ProtocolConstants.HeaderChunkHash, hash);
        }

        using var resp = await _http.PostAsync(baseUri + ProtocolConstants.PathChunk, content, token)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>获取任务对端的发送安全上下文。未注入配对服务 / 对端为 v1 明文 → null。</summary>
    private async Task<PeerSecurityContext?> GetSendSecurityContextAsync(TransferTaskInfo task)
    {
        if (_pairing is null || task.Peer is null) return null;
        var sec = await _pairing.GetContextForSendAsync(task.Peer).ConfigureAwait(false);
        return sec.IsProtocolV2 && sec.IsPaired ? sec : null;
    }

    /// <summary>
    /// 发送 /prepare，并在收到 401 时自动修复配对后重试一次。
    /// 401 表示「发送端本地配对记录与对端不一致」（对端清过数据/重装/密钥世代不同）：
    /// 此前会被当作普通失败 → 永久 401、"怎么都连不上"。这里清除本地陈旧记录并重新走
    /// /security/pair 握手即可自愈。返回的 HttpResponseMessage 由调用方负责 Dispose。
    /// </summary>
    /// <summary>/prepare 单独的超时（比 HttpClient.Timeout=30s 更短）：对端不可达时尽快失败，
    /// 避免长时间占用 <c>_resuming</c> 单飞锁导致后续"重试/恢复"被静默忽略。</summary>
    private const int PrepareTimeoutMs = 12000;

    private async Task<(HttpResponseMessage Response, PeerSecurityContext? SecCtx)> PostPrepareWithRepairAsync(
        TransferTaskInfo task, string baseUri, PrepareRequest prepareReq, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var secCtx = await GetSendSecurityContextAsync(task).ConfigureAwait(false);
            using var content = BuildSecureContent(prepareReq, secCtx);
            // 对 /prepare 施加更短超时（链接调用方 token）
            using var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            prepareCts.CancelAfter(PrepareTimeoutMs);
            var resp = await _http.PostAsync(baseUri + ProtocolConstants.PathPrepare, content, prepareCts.Token).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && secCtx is not null
                && !string.IsNullOrEmpty(task.Peer?.DeviceId))
            {
                FtaTrace.Warn("FTA.PAIR", $"401 未配对/密文无效 → 清除陈旧配对并重新握手 fileId={task.FileId} peer={task.Peer!.DeviceId}");
                resp.Dispose();
                try { _pairing!.Unpair(task.Peer!.DeviceId!); } catch { /* ignore */ }
                continue;
            }

            return (resp, secCtx);
        }
    }

    /// <summary>
    /// 发送/续传前，按 <c>DeviceId</c> 用发现服务的最新结果刷新对端地址。
    /// 发现层每次心跳都会用新的 <see cref="DeviceNode"/> 覆盖字典，但任务在创建时固定了当时的
    /// Peer 引用；对端换 IP（重连/漫游）后，已存在的失败/断开任务若直接重试会一直打旧地址。
    /// 这里按 DeviceId 取回最新节点，更新任务 Peer 的 IP/端口后再握手。
    /// 仅对发送方向生效（接收方向的 Peer 是 TCP 源地址，不应被覆盖）；对端不在发现列表中
    /// （如手动直连的伪节点）时保持原值。
    /// </summary>
    private void RefreshPeerFromDiscovery(TransferTaskInfo task)
    {
        if (_discovery is null || task.Peer is null) return;
        if (task.Direction != TransferDirection.Send) return;
        var id = task.Peer.DeviceId;
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            var latest = _discovery.Devices.FirstOrDefault(d => d.DeviceId == id);
            if (latest?.IpAddress is null) return;
            if (!Equals(task.Peer.IpAddress, latest.IpAddress) || task.Peer.Port != latest.Port)
            {
                FtaTrace.Info("FTA.SEND",
                    $"refresh peer {id}: {task.Peer.IpAddress}:{task.Peer.Port} -> {latest.IpAddress}:{latest.Port}");
            }
            task.Peer.IpAddress = latest.IpAddress;
            if (latest.Port > 0) task.Peer.Port = latest.Port;
        }
        catch { /* 发现层异常不影响发送 */ }
    }

    /// <summary>
    /// 发送任务的续传核心：重新握手获取对端最新 Bitmap，再推送缺失切片。
    /// 供多个入口复用：本端点击"恢复"(ResumeAsync)、对端发起恢复(ApplyControlAsync RESUME)、本端/对端重试(RetryAsync)。
    /// 重入保护：同一任务同一时刻只允许一条续传链路，避免双循环重复推送。
    /// </summary>
    private async Task ResumeSendAsync(TransferTaskInfo task)
    {
        var from = task.State;
        if (from is not (TransferState.Paused or TransferState.Disconnected or TransferState.Failed)) return;
        if (!_resuming.TryAdd(task.FileId, 0))
        {
            // 同一任务已有续传链路在进行：忽略本次（有日志，不再静默）
            FtaTrace.Info("FTA.SEND", $"resume/retry ignored (already in progress) fileId={task.FileId}");
            return;
        }
        try
        {
            task.PauseCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            if (from is TransferState.Disconnected or TransferState.Failed)
            {
                // 重试：先清除旧的失败标记，重新进入握手
                task.ErrorMessage = string.Empty;
                task.ErrorCode = string.Empty;
                task.ErrorArgs = null;
            }
            SetState(task, from, TransferState.Transferring);

            // 通知对端恢复为 Transferring（若对端此前也处于 Paused）。
            // 必须在重新握手/发片前发起，尽量消除"对端仍在 Paused → 首个切片被 409 拒绝"的竞态；
            // 即使漏达，接收端 RegisterReceiveTaskAsync 的续传分支也会把状态拉回 Transferring。
            NotifyPeerControlAsync(task, TransferAction.RESUME);

            // 恢复时重新握手，获取对端最新 Bitmap（可能已部分到达）
            RefreshPeerFromDiscovery(task);
            var baseUri = $"http://{task.Peer!.IpAddress}:{task.Peer.Port}";
            var prepareReq = new PrepareRequest
            {
                FileId = task.FileId,
                FileName = task.FileName,
                FileSize = task.TotalBytes,
                ChunkSize = task.ChunkSize,
                Sha256 = task.Sha256,
                ThumbnailBase64 = task.ThumbnailBase64,
                ThumbnailMimeType = task.ThumbnailMimeType,
            };
            try
            {
                // 握手（含 401 自动重新配对重试），与初次发送一致自动携带 Content-Length。
                var (resp, secCtx) = await PostPrepareWithRepairAsync(task, baseUri, prepareReq, default).ConfigureAwait(false);
                PrepareResponse? prep;
                using (resp)
                {
                    if (!resp.IsSuccessStatusCode) { TryTransition(task, TransferState.Disconnected); return; }
                    prep = await resp.Content.ReadFromJsonAsync<PrepareResponse>().ConfigureAwait(false);
                }
                if (prep is null || !prep.Accepted) { TryTransition(task, TransferState.Cancelled); return; }
                await SendBatchAsync(task, baseUri, prep.ReceivedChunks ?? Array.Empty<int>(), secCtx, task.PauseCts.Token)
                    .ConfigureAwait(false);
                if (task.State == TransferState.Transferring)
                {
                    task.BytesTransferred = task.TotalBytes;
                    SetState(task, TransferState.Transferring, TransferState.Completed);
                }
            }
            catch (PairingDeniedException ex)
            {
                SetError(task, $"对方拒绝了配对请求: {ex.Message}", "Err.PairingDenied", ex.Message);
                TryTransition(task, TransferState.Cancelled);
            }
            catch (Exception ex)
            {
                SetError(task, $"恢复失败: {ex.Message}", "Err.ResumeFailed", ex.Message);
                TryTransition(task, TransferState.Disconnected);
            }
        }
        finally
        {
            _resuming.TryRemove(task.FileId, out _);
        }
    }
}
