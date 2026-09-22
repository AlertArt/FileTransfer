using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Diagnostics;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 基于 System.IO.Pipelines 的高性能文件传输引擎（门面 + 共享状态）。
/// 职责按 partial 文件拆分，便于维护：
///  - PipelinesTransferEngine.cs          共享状态 / FSM 流转 / 消息 / 进度 / 静态 HTTP 构造（本文件）
///  - PipelinesTransferEngine.Send.cs     发送侧（握手 + 切片推送 + 续传/重试）
///  - PipelinesTransferEngine.Receive.cs  接收侧（注册 + 落盘 + 完成校验）
///  - PipelinesTransferEngine.Control.cs  控制命令（暂停 / 恢复 / 重试 / 取消 / 删除）
/// Pipelines 零拷贝内核见 <see cref="PipeStreamCopier"/>。
/// </summary>
public sealed partial class PipelinesTransferEngine : ITransferEngine
{
    private readonly IStorageService _storage;
    private readonly IThumbnailService _thumbnail;
    private readonly ITransferApprovalService _approval;
    private readonly IMessenger _messenger;
    private readonly HttpClient _http;
    private readonly IPairingService? _pairing;
    private readonly IDiscoveryService? _discovery;

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
        IMessenger messenger,
        IPairingService? pairing = null,
        IDiscoveryService? discovery = null)
    {
        _storage = storage;
        _thumbnail = thumbnail;
        _approval = approval;
        _messenger = messenger;
        _pairing = pairing;
        _discovery = discovery;
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

    // ===================== 静态 HTTP 内容构造 =====================

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

    /// <summary>
    /// 构造 v2 加密请求体：AES-GCM 加密明文 JSON，并附加 X-Protocol / X-Device-Id 头。
    /// sec 为 null（v1 明文路径）时退化为 BuildJsonContent。
    /// </summary>
    internal static HttpContent BuildSecureContent(object obj, PeerSecurityContext? sec)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        return BuildSecureContent(jsonBytes, sec);
    }

    /// <summary>对任意明文字节构造请求体；sec 为 null 或未加密时返回原始字节 content。</summary>
    internal static HttpContent BuildSecureContent(byte[] plainBytes, PeerSecurityContext? sec)
    {
        if (sec?.IsEncrypted == true && sec.SharedKey is not null)
        {
            var frame = DeviceCrypto.Encrypt(sec.SharedKey, plainBytes);
            var content = new ByteArrayContent(frame);
            content.Headers.Add(ProtocolConstants.HeaderProtocolVersion, ProtocolConstants.ProtocolVersion.ToString());
            content.Headers.Add(ProtocolConstants.HeaderDeviceId, sec.SelfDeviceId ?? string.Empty);
            return content;
        }
        var plain = new ByteArrayContent(plainBytes);
        plain.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return plain;
    }

    private static string ComputeChunkHash(byte[] buf, int len)
    {
        var h = SHA256.HashData(new ReadOnlySpan<byte>(buf, 0, len));
        return Convert.ToHexString(h).ToLowerInvariant();
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
            // 每个任务结束只记一条摘要（含大小/用时/均速），替代"每切片一条"的高噪声日志
            var durSec = (task.EndedUtc - task.StartedUtc).TotalSeconds;
            var avgBps = durSec > 0.05 ? task.BytesTransferred / durSec : 0;
            FtaTrace.Info("FTA.XFER",
                $"{to} {task.Direction} file='{task.FileName}' size={SpeedFormatter.FormatSize(task.TotalBytes)} " +
                $"moved={SpeedFormatter.FormatSize(task.BytesTransferred)} 用时={durSec:0.0}s 平均={SpeedFormatter.FormatSpeed(avgBps)}" +
                (string.IsNullOrEmpty(task.ErrorMessage) ? string.Empty : $" err='{task.ErrorMessage}'"));
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
}
