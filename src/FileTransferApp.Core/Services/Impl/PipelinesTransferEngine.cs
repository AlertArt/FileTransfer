using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Messaging;
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
    /// <summary>大文件（数百 MB）按 64KB 切片会有上万条进度消息，
    /// 全量 Post 到 UI 线程会造成明显卡顿，限制到 ~10Hz/任务。</summary>
    private const int ProgressPublishIntervalMs = 100;

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

    // ===================== 控制命令 =====================

    public Task PauseAsync(string fileId)
    {
        if (_tasks.TryGetValue(fileId, out var task) && task.State == TransferState.Transferring)
        {
            SetState(task, TransferState.Transferring, TransferState.Paused);
            try { task.PauseCts?.Cancel(); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    public async Task ResumeAsync(string fileId)
    {
        if (!_tasks.TryGetValue(fileId, out var task)) return;
        if (task.State != TransferState.Paused) return;

        task.PauseCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        SetState(task, TransferState.Paused, TransferState.Transferring);

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
            var resp = await _http.PostAsJsonAsync(baseUri + ProtocolConstants.PathPrepare, prepareReq, default)
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

    public async Task CancelAsync(string fileId)
    {
        if (!_tasks.TryGetValue(fileId, out var task)) return;
        if (TransferStateMachine.IsTerminal(task.State)) return;

        try { task.PauseCts?.Cancel(); } catch { /* ignore */ }
        TryTransition(task, TransferState.Cancelled);

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
        // 非终态先取消（保证资源被清理）
        if (!TransferStateMachine.IsTerminal(task.State))
            await CancelAsync(fileId).ConfigureAwait(false);
        CloseReceiveStream(fileId);
        _speeds.TryRemove(fileId, out _);
        _tasks.TryRemove(fileId, out _);
        // 通知 UI 从列表移除
        _messenger.Send(new TransferTaskRemovedMessage(fileId));
    }

    // ===================== 接收侧（被 TransferServer 调用）=====================

    public async Task<PrepareResponse> RegisterReceiveTaskAsync(PrepareRequest prepare, DeviceNode peer)
    {
        // 续传：任务已存在 → 回传 Bitmap
        if (_tasks.TryGetValue(prepare.FileId, out var existing) && _receiveStreams.ContainsKey(prepare.FileId))
        {
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
        if (!_tasks.TryGetValue(fileId, out var task)) return Task.CompletedTask;
        switch (action)
        {
            case TransferAction.PAUSE:
                if (task.State == TransferState.Transferring)
                    SetState(task, TransferState.Transferring, TransferState.Paused);
                break;
            case TransferAction.RESUME:
                if (task.State == TransferState.Paused)
                    SetState(task, TransferState.Paused, TransferState.Transferring);
                break;
            case TransferAction.CANCEL:
                _ = CancelAsync(fileId);
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
        // 最后一帧（BytesTransferred >= TotalBytes）必须推，保证进度收尾准确。
        var now = Environment.TickCount64;
        var last = _lastProgressTickMs.TryGetValue(task.FileId, out var v) ? v : long.MinValue;
        if (now - last < ProgressPublishIntervalMs && task.BytesTransferred < task.TotalBytes)
            return;
        _lastProgressTickMs[task.FileId] = now;

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
}
