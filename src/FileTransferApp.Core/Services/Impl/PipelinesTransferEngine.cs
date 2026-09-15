using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Diagnostics;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 基于 System.IO.Pipelines 的高性能文件传输引擎。
/// 发送侧：HTTP Metadata 握手 (POST /prepare) + 切片推送 (POST /chunk)；
/// 接收侧：Pipelines 零拷贝落盘 (StreamChunkToDiskAsync) + 断点续传 Bitmap。
/// 严格遵循 <see cref="TransferStateMachine"/> 状态机流转。
/// </summary>
public sealed class PipelinesTransferEngine : ITransferEngine
{
    private readonly IStorageService _storage;
    private readonly IThumbnailService _thumbnail;
    private readonly ITransferApprovalService _approval;
    private readonly IMessenger _messenger;
    private readonly HttpClient _http;

    private readonly ConcurrentDictionary<string, TransferTaskInfo> _tasks = new();
    private readonly ConcurrentDictionary<string, SpeedCalculator> _speeds = new();
    private readonly ConcurrentDictionary<string, Stream> _receiveStreams = new();

    /// <summary>逐任务进度推送节流：上次推送的时间戳 (Environment.TickCount64, ms)</summary>
    private readonly ConcurrentDictionary<string, long> _lastProgressTickMs = new();
    /// <summary>每个任务最近一次已发布进度对应的字节数，用于"百分比步进"强制推送中间帧。</summary>
    private readonly ConcurrentDictionary<string, long> _lastPublishedBytes = new();
    /// <summary>逐任务发送续传重入锁：本地点击"恢复"与对端控制 RESUME 可能并发触发同一条续传，需去重</summary>
    private readonly ConcurrentDictionary<string, byte> _resuming = new();
    /// <summary>大文件（数百 MB）按 64KB 切片会有上万条进度消息，
    /// 全量 Post 到 UI 线程会造成明显卡顿，限制到 ~10Hz/任务。</summary>
    private const int ProgressPublishIntervalMs = 100;
    /// <summary>进度步进阈值：累计推进 ≥ 此比例时即使未到时间限频也强制推送一帧，
    /// 保证局域网快传不会只剩 0%→100% 两帧，UI 能看到阶梯式进度。</summary>
    private const double ProgressStepRatio = 0.05;

    public PipelinesTransferEngine(
        IStorageService storage,
        IThumbnailService thumbnail,
        ITransferApprovalService approval,
        IMessenger messenger)
    {
        _storage = storage;
        _thumbnail = thumbnail;
        _approval = approval;
        _messenger = messenger;
        // 禁用 Expect: 100-continue：默认行为会让 HttpClient 先发请求头等 100 Continue 再发 body，
        // 我们的简易 HTTP 服务器虽然已处理该头，但禁用后可直接发送 body，减少握手延迟与失败概率。
        var handler = new SocketsHttpHandler
        {
            Expect100ContinueTimeout = TimeSpan.Zero,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    public event EventHandler<TransferStatusChangedMessage>? StateChanged;

    // ===================== 发送侧 =====================

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
        return task;
    }

    public async Task StartSendAsync(string fileId, CancellationToken ct = default)
    {
        if (_tasks.TryGetValue(fileId, out var task) is false) return;

        SetState(task, TransferState.Created, TransferState.Preparing);

        // Preparing 阶段：在后台线程计算 SHA-256 全文件哈希 + 生成缩略图。
        // 大文件（数百 MB ~ GB）此步骤耗时较长，放在这里而不是 CreateSendTaskAsync，
        // 让任务卡片能立即出现在 UI 上（状态显示"握手中"），用户不再"选完文件傻等"。
        try
        {
            var (sha, thumb) = await Task.Run(async () =>
            {
                var hash = await _storage.ComputeSha256Async(task.LocalPath!).ConfigureAwait(false);
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
        try
        {
            // 不用 PostAsJsonAsync：它在内容较大时可能使用 Transfer-Encoding: chunked，
            // 我们的简易 HTTP 服务器只支持 Content-Length，chunked 会导致 body 读取为空 → 400。
            // 手动序列化为 byte[] 用 ByteArrayContent，HttpClient 会自动设置 Content-Length。
            var jsonBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(prepareReq, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
            using var content = new ByteArrayContent(jsonBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
            var resp = await _http.PostAsync(baseUri + ProtocolConstants.PathPrepare, content, ct)
                .ConfigureAwait(false);
            // 不要直接 EnsureSuccessStatusCode：把非 2xx 响应体读出作为 ErrorMessage
            // 以便用户直观看出 Android 端拒绝的原因
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

        await SendBatchAsync(task, baseUri, prep.ReceivedChunks ?? Array.Empty<int>(), task.PauseCts.Token).ConfigureAwait(false);

        // 全部完成
        if (task.State == TransferState.Transferring)
        {
            task.BytesTransferred = task.TotalBytes;
            SetState(task, TransferState.Transferring, TransferState.Completed);
        }
    }

    private async Task SendBatchAsync(TransferTaskInfo task, string baseUri, int[] peerReceived, CancellationToken token)
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
                await PostChunkAsync(task, baseUri, idx, hash, buf, read, token).ConfigureAwait(false);
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

    private async Task PostChunkAsync(TransferTaskInfo task, string baseUri, int idx, string hash, byte[] buf, int len, CancellationToken token)
    {
        using var content = new ByteArrayContent(buf, 0, len);
        content.Headers.Add(ProtocolConstants.HeaderFileId, task.FileId);
        content.Headers.Add(ProtocolConstants.HeaderChunkIndex, idx.ToString());
        content.Headers.Add(ProtocolConstants.HeaderChunkHash, hash);

        using var resp = await _http.PostAsync(baseUri + ProtocolConstants.PathChunk, content, token)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private static string ComputeChunkHash(byte[] buf, int len)
    {
        var h = SHA256.HashData(new ReadOnlySpan<byte>(buf, 0, len));
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    /// <summary>
    /// 手动序列化 JSON 为 ByteArrayContent：HttpClient 会据此自动设置 Content-Length。
    /// 不要用 PostAsJsonAsync —— 它在 .NET 上以 Transfer-Encoding: chunked 发送
    /// （无 Content-Length 头），简易 HTTP 服务器只支持 Content-Length，会把 body 读空 → 400。
    /// </summary>
    internal static ByteArrayContent BuildJsonContent(object obj)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    // ===================== 控制命令 =====================

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

    /// <summary>
    /// 发送任务的续传核心：重新握手获取对端最新 Bitmap，再推送缺失切片。
    /// 供多个入口复用：本端点击"恢复"(ResumeAsync)、对端发起恢复(ApplyControlAsync RESUME)、本端/对端重试(RetryAsync)。
    /// 重入保护：同一任务同一时刻只允许一条续传链路，避免双循环重复推送。
    /// </summary>
    private async Task ResumeSendAsync(TransferTaskInfo task)
    {
        var from = task.State;
        if (from is not (TransferState.Paused or TransferState.Disconnected or TransferState.Failed)) return;
        if (!_resuming.TryAdd(task.FileId, 0)) return;
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
                // PostAsJsonAsync 会以 chunked 发送（无 Content-Length），简易服务器读不到 body → 400；
                // 与初次发送一致，改用 ByteArrayContent 自动携带 Content-Length。
                using var content = BuildJsonContent(prepareReq);
                var resp = await _http.PostAsync(baseUri + ProtocolConstants.PathPrepare, content, default)
                    .ConfigureAwait(false);
                var prep = await resp.Content.ReadFromJsonAsync<PrepareResponse>().ConfigureAwait(false);
                if (prep is null || !prep.Accepted) { TryTransition(task, TransferState.Cancelled); return; }
                await SendBatchAsync(task, baseUri, prep.ReceivedChunks ?? Array.Empty<int>(), task.PauseCts.Token)
                    .ConfigureAwait(false);
                if (task.State == TransferState.Transferring)
                {
                    task.BytesTransferred = task.TotalBytes;
                    SetState(task, TransferState.Transferring, TransferState.Completed);
                }
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
        _messenger.Send(new TransferTaskRemovedMessage(fileId));
    }

    // ===================== 接收侧（被 TransferServer 调用）=====================

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
        CleanupStaleReceiveTasksByName(fileName);
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
        await StreamChunkToDiskAsync(new MemoryStream(data.ToArray()), stream, CancellationToken.None).ConfigureAwait(false);

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

    // ===================== System.IO.Pipelines 零拷贝内核 (设计 5.4) =====================

    /// <summary>使用 Pipe 将 source 流零拷贝写入 dest 文件流</summary>
    private static async Task StreamChunkToDiskAsync(Stream source, Stream dest, CancellationToken ct)
    {
        var pipe = new Pipe();
        Task writing = FillPipeAsync(source, pipe.Writer, ct);
        Task reading = ReadPipeAsync(dest, pipe.Reader, ct);
        await Task.WhenAll(writing, reading).ConfigureAwait(false);
    }

    private static async Task FillPipeAsync(Stream source, PipeWriter writer, CancellationToken ct)
    {
        const int minimumBufferSize = ProtocolConstants.ReceiveBufferSize; // 64KB
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(minimumBufferSize);
                var bytesRead = await source.ReadAsync(memory, ct).ConfigureAwait(false);
                if (bytesRead == 0) break;

                writer.Advance(bytesRead);
                var result = await writer.FlushAsync(ct).ConfigureAwait(false);
                if (result.IsCompleted) break;
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private static async Task ReadPipeAsync(Stream dest, PipeReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                foreach (var segment in buffer)
                    await dest.WriteAsync(segment, ct).ConfigureAwait(false);
                reader.AdvanceTo(buffer.End);
                if (result.IsCompleted) break;
            }
            await dest.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    // ===================== 查询 / 状态 / 事件 =====================

    public IReadOnlyList<TransferTaskInfo> GetTasks() => _tasks.Values.ToList();
    public TransferTaskInfo? GetTask(string fileId) => _tasks.TryGetValue(fileId, out var t) ? t : null;

    /// <summary>
    /// 记录任务错误：同时写入本地化错误码（Err.*）与原始兜底文案。
    /// UI 层优先按 ErrorCode/ErrorArgs 转本地化文本，资源缺失时回退 ErrorMessage。
    /// </summary>
    private static void SetError(TransferTaskInfo task, string message, string errorCode, params object[] args)
    {
        task.ErrorMessage = message;
        task.ErrorCode = errorCode;
        task.ErrorArgs = args.Length == 0 ? null : args;
    }

    private void SetState(TransferTaskInfo task, TransferState expectedFrom, TransferState to)
    {
        if (task.State != expectedFrom) return;
        TransferStateMachine.EnsureTransition(task.State, to);
        task.State = to;
        EmitState(task, to);
    }

    private void TryTransition(TransferTaskInfo task, TransferState to)
    {
        if (task.State == to) return;
        if (TransferStateMachine.IsTerminal(task.State)) return;
        if (!TransferStateMachine.CanTransition(task.State, to)) return;
        task.State = to;
        EmitState(task, to);
    }

    private void EmitState(TransferTaskInfo task, TransferState to)
    {
        var msg = new TransferStatusChangedMessage(task.FileId, to);
        StateChanged?.Invoke(this, msg);
        _messenger.Send(msg);
        if (TransferStateMachine.IsTerminal(to))
        {
            task.EndedUtc = DateTime.UtcNow;
            _messenger.Send(new TransferCompletedMessage(
                task.FileId, to == TransferState.Completed, task.ErrorMessage));
        }
    }

    private void PublishProgress(TransferTaskInfo task, SpeedCalculator speed)
    {
        // 限频推送：避免大文件（上万切片）向 UI 线程注入海量消息导致卡顿。
        // 但纯时间限频在局域网快传（< 100ms 完成）时会吞掉全部中间帧，UI 只见 0%→100%。
        // 因此叠加"百分比步进"条件：自上次推送推进 ≥ ProgressStepRatio 时必定发一帧。
        // 最后一帧（BytesTransferred >= TotalBytes）也必须推，保证进度收尾准确。
        var now = Environment.TickCount64;
        var last = _lastProgressTickMs.TryGetValue(task.FileId, out var v) ? v : long.MinValue;
        var elapsedOk = now - last >= ProgressPublishIntervalMs;
        var lastBytes = _lastPublishedBytes.TryGetValue(task.FileId, out var lb) ? lb : 0;
        var stepBytes = task.TotalBytes > 0 ? Math.Max(1L, (long)(task.TotalBytes * ProgressStepRatio)) : long.MaxValue;
        var stepOk = task.BytesTransferred - lastBytes >= stepBytes && task.BytesTransferred > 0;
        if (!elapsedOk && !stepOk && !(task.BytesTransferred >= task.TotalBytes))
            return;
        _lastProgressTickMs[task.FileId] = now;
        _lastPublishedBytes[task.FileId] = task.BytesTransferred;

        _messenger.Send(new TransferProgressMessage(
            task.FileId, task.BytesTransferred, task.TotalBytes, speed.GetSpeedBytesPerSecond()));
    }

    private void CloseReceiveStream(string fileId)
    {
        if (_receiveStreams.TryRemove(fileId, out var s))
        {
            try { s.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 注册新接收任务前清理同名残留任务：发送端在本端未完成时重发同名文件（新 FileId），
    /// 旧任务仍持有 .tmp 写入流句柄，会让新的 OpenWriteStreamAsync 抛 IO_SharingViolation。
    /// 仅清理"未完成"（非终态）的任务：已完成/失败/取消的任务已释放流（FinalizeWriteAsync
    /// 将 .tmp 重命名为最终文件或 CancelWriteAsync 已删除），无需干涉，应继续展示在 UI。
    /// </summary>
    private void CleanupStaleReceiveTasksByName(string fileName)
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
                try { _storage.CancelWriteAsync(task.LocalPath).GetAwaiter().GetResult(); }
                catch { /* ignore */ }
            }

            _speeds.TryRemove(task.FileId, out _);
            _tasks.TryRemove(task.FileId, out _);
            _messenger.Send(new TransferTaskRemovedMessage(task.FileId));
        }
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
                    // 改用 ByteArrayContent 携带 Content-Length。
                    using var content = BuildJsonContent(cmd);
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
