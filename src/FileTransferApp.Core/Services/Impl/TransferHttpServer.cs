using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FileTransferApp.Core.Diagnostics;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 基于 System.Net.Sockets + Pipelines 的轻量 HTTP/1.1 传输服务端。
/// 监听 53318 端口，响应 /prepare、/chunk、/control 三个 RESTful 接口，
/// 委托 <see cref="ITransferEngine"/> 完成接收侧逻辑。
/// </summary>
public sealed class TransferHttpServer : ITransferServer
{
    private readonly ITransferEngine _engine;
    private readonly IPairingService? _pairing;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public TransferHttpServer(ITransferEngine engine, IPairingService? pairing = null)
    {
        _engine = engine;
        _pairing = pairing;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1) return Task.CompletedTask;

        FtaTrace.Info("FTA.VER", BuildVersion());
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, ProtocolConstants.TransferPort);
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoopAsync, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch { continue; }

            _ = HandleConnectionAsync(client, token);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using (tcp)
            using (tcp.Client)
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null) return;

                var peer = BuildPeer(tcp);
                string responseJson;
                int status;

                // /security/pair 永远明文：配对握手在建立共享密钥之前发生，只交换公钥。
                // 其余三个传输端点：若携带 X-Device-Id 头（v2 加密请求）→ 必须先解密成功。
                if (request.Path == ProtocolConstants.PathPair)
                {
                    (status, responseJson) = await HandlePairAsync(request, peer).ConfigureAwait(false);
                }
                else
                {
                    if (TryDecryptV2Request(ref request, ref peer, out status, out responseJson))
                    {
                        // 解密成功（或明文 v1 请求无需解密）继续分发；否则 TryDecryptV2Request 已写入 401
                        switch (request.Path)
                        {
                            case ProtocolConstants.PathPrepare:
                                (status, responseJson) = await HandlePrepareAsync(request, peer).ConfigureAwait(false);
                                break;

                            case ProtocolConstants.PathChunk:
                                (status, responseJson) = await HandleChunkAsync(request).ConfigureAwait(false);
                                break;

                            case ProtocolConstants.PathControl:
                                (status, responseJson) = await HandleControlAsync(request).ConfigureAwait(false);
                                break;

                            default:
                                status = 404;
                                responseJson = Serialize(new ErrorResponse { Error = "Not Found" });
                                break;
                        }
                    }
                }

                // 每请求一条（大文件每 2MB 一个 /chunk 请求）→ Verbose，默认不落盘，避免日志暴涨
                FtaTrace.Verbose("FTA.HTTP", $"REQ {request.Method} {request.Path} -> {status} from {peer.IpAddress}:{peer.Port}");
                await WriteResponseAsync(stream, status, responseJson).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* ignore */ }
        catch
        {
            /* 单连接异常不影响整体服务 */
        }
    }

    /// <summary>internal: 供单元测试通过构造 mock HttpRequest 直接驱动</summary>
    internal async Task<(int status, string json)> HandlePrepareAsync(HttpRequest req, DeviceNode peer)
    {
        try
        {
            var prepare = Deserialize<PrepareRequest>(req.Body);
            if (prepare is null) return BadRequest("无效的 prepare 请求");

            var resp = await _engine.RegisterReceiveTaskAsync(prepare, peer).ConfigureAwait(false);
            return (200, Serialize(resp));
        }
        catch (Exception ex)
        {
            // 接收端 /prepare 失败通常是根因：权限/审批弹窗/存储写入。
            // 把完整堆栈写 Trace (桌面端) + logcat (Android)，Error 字段返回 Message 给发送端 UI 展示。
            Trace.TraceError($"[FTA.HTTP] /prepare Exception: {ex}");
            return (500, Serialize(new ErrorResponse { Error = ex.Message }));
        }
    }

    /// <summary>
    /// internal: 供单元测试驱动 404/409/400 错误路径
    /// </summary>
    internal async Task<(int status, string json)> HandleChunkAsync(HttpRequest req)
    {
        try
        {
            if (!req.Headers.TryGetValue(ProtocolConstants.HeaderFileId, out var fileId) ||
                !req.Headers.TryGetValue(ProtocolConstants.HeaderChunkIndex, out var idxStr) ||
                !int.TryParse(idxStr, out var chunkIndex))
            {
                return BadRequest("缺少 X-File-Id / X-Chunk-Index 头");
            }

            var task = _engine.GetTask(fileId);
            if (task is null) return (404, Serialize(new ErrorResponse { Error = "任务不存在，请先 /prepare" }));

            var ok = await _engine.WriteChunkAsync(fileId, chunkIndex, task.TotalChunks, req.Body)
                .ConfigureAwait(false);
            return ok
                ? (200, Serialize(new ChunkResponse { ChunkIndex = chunkIndex, Status = "Success" }))
                : (409, Serialize(new ErrorResponse { Error = "已暂停或已取消，拒绝写入" }));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[FTA.HTTP] /chunk Exception: {ex}");
            return (500, Serialize(new ErrorResponse { Error = ex.Message }));
        }
    }

    /// <summary>internal: 供单元测试驱动 200/400 路径</summary>
    internal async Task<(int status, string json)> HandleControlAsync(HttpRequest req)
    {
        try
        {
            var ctl = Deserialize<ControlRequest>(req.Body);
            FtaTrace.Info("FTA.HTTP", $"REQ POST {req.Path} | Content-Length={req.Body.Length} | ctl={(ctl is null ? "NULL" : $"FileId={ctl.FileId} Action='{ctl.Action}'")} | body={TruncateForLog(req.Body)}");
            if (ctl is null || !Enum.TryParse<TransferAction>(ctl.Action, ignoreCase: true, out var action))
            {
                FtaTrace.Warn("FTA.HTTP", $"control parse FAIL -> 400 (ctl={(ctl is null ? "null" : $"Action='{ctl.Action}'")})");
                return BadRequest("无效的 action");
            }

            FtaTrace.Info("FTA.CTRL", $"<- {action} {ctl.FileId} (server)");
            await _engine.ApplyControlAsync(ctl.FileId, action).ConfigureAwait(false);
            return (200, Serialize(new ControlResponse()));
        }
        catch (Exception ex)
        {
            FtaTrace.Warn("FTA.HTTP", $"control handler EXCEPTION -> 500: {ex}");
            return (500, Serialize(new ErrorResponse { Error = ex.Message }));
        }
    }

    // ---- HTTP/1.1 解析与响应 ----

    /// <summary>处理 /security/pair 配对请求（明文，见 HandleConnectionAsync 路由前置）</summary>
    internal async Task<(int status, string json)> HandlePairAsync(HttpRequest req, DeviceNode peer)
    {
        try
        {
            if (_pairing is null)
            {
                // 未注入配对服务：v1 兼容包退化为不支持配对
                return (404, Serialize(new ErrorResponse { Error = "Not Found" }));
            }
            var pair = Deserialize<PairRequest>(req.Body);
            if (pair is null) return BadRequest("无效的配对请求");

            var resp = await _pairing.HandlePairRequestAsync(pair, peer).ConfigureAwait(false);
            return resp.Accepted
                ? (200, Serialize(resp))
                : (400, Serialize(resp));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[FTA.HTTP] /pair Exception: {ex}");
            return (500, Serialize(new ErrorResponse { Error = ex.Message }));
        }
    }

    /// <summary>
    /// v2 加密请求解密前置处理。
    /// - 请求带 X-Device-Id 头（v2 加密）：校验配对并解密 body，成功返回 true；未配对/解密失败写 401 返回 false。
    /// - 无 X-Device-Id 头（v1 明文）：原样通过返回 true。
    /// 解密成功时用配对记录中的设备名/ID 充实 peer（供审批 UI 展示真实设备名）。
    /// </summary>
    /// <summary>internal: 端点前置解密逻辑，供单元测试驱动 v2 加密路径（未配对 401 / 解密成功 / v1 明文直通）</summary>
    internal bool TryDecryptV2Request(ref HttpRequest request, ref DeviceNode peer, out int status, out string responseJson)
    {
        if (!request.Headers.TryGetValue(ProtocolConstants.HeaderDeviceId, out var senderId))
        {
            status = 0;
            responseJson = string.Empty;
            return true; // v1 明文
        }

        if (_pairing is null)
        {
            status = 401;
            responseJson = Serialize(new ErrorResponse { Error = "服务器未启用配对加密" });
            return false;
        }

        var plain = _pairing.TryDecrypt(senderId, request.Body);
        if (plain is null)
        {
            status = 401;
            responseJson = Serialize(new ErrorResponse { Error = "设备未配对或密文无效" });
            return false;
        }

        request = request with { Body = plain };
        peer.DeviceId = senderId;
        var displayName = _pairing.GetPeerDisplayName(senderId);
        if (!string.IsNullOrEmpty(displayName)) peer.DeviceName = displayName;
        status = 0;
        responseJson = string.Empty;
        return true;
    }

    // ---- HTTP/1.1 解析与响应 ----

    /// <summary>internal: 供单元测试驱动 HTTP 请求解析 + 404 路径</summary>
    internal static async Task<HttpRequest?> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var buf = new MemoryStream();
        var tmp = new byte[4096];
        int total = 0;
        int delim = -1;

        while (delim < 0)
        {
            int n = await s.ReadAsync(tmp, ct).ConfigureAwait(false);
            if (n == 0) return null;
            buf.Write(tmp, 0, n);
            total += n;
            var arr = buf.GetBuffer();
            delim = IndexOfCrlfCrlf(arr, total);
            if (total > 1 << 20) return null; // 头部过大保护
        }

        var headerText = Encoding.ASCII.GetString(buf.GetBuffer(), 0, delim);
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;
        var method = parts[0];
        var path = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim();
                var val = line[(colon + 1)..].Trim();
                headers[key] = val;
            }
        }

        // 处理 Expect: 100-continue：.NET HttpClient 默认对带 body 的 POST 先发请求头等待 100 Continue。
        // 我们的服务器不主动响应会导致 body 被延迟发送或读取异常（表现为 body 为空 → 反序列化 null → 400）。
        // 检测到该头后立即返回 100 Continue，告诉客户端可以发送 body。
        if (headers.TryGetValue("Expect", out var expectVal) &&
            expectVal.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            var continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            await s.WriteAsync(continueBytes, 0, continueBytes.Length, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }

        var bodyPrelude = delim + 4;
        var bodyLen = headers.TryGetValue(ProtocolConstants.HeaderContentLength, out var lenStr)
                      && int.TryParse(lenStr, out var bl) ? bl : 0;

        // 与上面的 REQ 日志重复，且每请求一条 → Verbose
        FtaTrace.Verbose("FTA.HTTP", $"{method} {path} | Content-Length={bodyLen} | preloaded={Math.Max(0, total - bodyPrelude)}");

        var body = new byte[bodyLen];
        var preloaded = Math.Max(0, total - bodyPrelude);
        if (preloaded > 0)
        {
            var arr = buf.GetBuffer();
            Array.Copy(arr, bodyPrelude, body, 0, Math.Min(preloaded, bodyLen));
        }
        var remaining = bodyLen - preloaded;
        if (remaining > 0)
        {
            var offset = preloaded;
            while (remaining > 0)
            {
                var n = await s.ReadAsync(body.AsMemory(offset, remaining), ct).ConfigureAwait(false);
                if (n == 0) break;
                offset += n;
                remaining -= n;
            }
        }

        return new HttpRequest(method, path, headers, body);
    }

    /// <summary>internal: 供单元测试验证 HTTP 响应格式</summary>
    internal static async Task WriteResponseAsync(Stream s, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ')
            .Append(status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict", 500 => "Internal Server Error", _ => "OK" })
            .Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(bytes.Length).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await s.WriteAsync(headerBytes).ConfigureAwait(false);
        await s.WriteAsync(bytes).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    internal static int IndexOfCrlfCrlf(byte[] arr, int length)
    {
        // arr 来自 buf.GetBuffer()，length 为有效长度
        for (int i = 0; i + 3 < length; i++)
            if (arr[i] == (byte)'\r' && arr[i + 1] == (byte)'\n' && arr[i + 2] == (byte)'\r' && arr[i + 3] == (byte)'\n')
                return i;
        return -1;
    }

    internal static DeviceNode BuildPeer(TcpClient tcp)
    {
        var ep = tcp.Client.RemoteEndPoint as IPEndPoint;
        return new DeviceNode
        {
            IpAddress = ep?.Address,
            Port = ep?.Port ?? 0,
            DeviceName = ep?.Address.ToString() ?? "unknown"
        };
    }

    internal static string Serialize<T>(T obj) => JsonSerializer.Serialize(obj, JsonOpts);

    /// <summary>构建版本标识写入日志，用于区分两端是否同一构建（排查旧版 chunked 上传误报）。</summary>
    internal static string BuildVersion()
    {
        var asm = typeof(TransferHttpServer).Assembly;
        var fileVer = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "?";
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        return $"{fileVer} ({infoVer})";
    }

    /// <summary>
    /// 反序列化选项：发送端 HttpClient.PostAsJsonAsync 默认使用 camelCase 属性名
    /// （JsonSerializerDefaults.Web），接收端必须 PropertyNameCaseInsensitive=true
    /// 才能把 {"fileId":"...","fileName":"..."} 正确映射到 PascalCase 的 C# 属性。
    /// 之前默认大小写敏感，导致 PrepareRequest 所有字段为默认值（非 null，
    /// 但若 body 读取异常为空仍会返回 null → HTTP 400）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static T? Deserialize<T>(byte[]? body)
    {
        if (body is null || body.Length == 0) return default;
        try
        {
            var result = JsonSerializer.Deserialize<T>(body, JsonOpts);
            if (result is null)
            {
                // JSON 合法但内容不足以构造对象（如 {}），也返回 null 让上层返回 400
                Trace.TraceWarning($"[FTA.HTTP] Deserialize<{typeof(T).Name}> returned null. Body({body.Length} bytes) = {TruncateForLog(body)}");
            }
            return result;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[FTA.HTTP] Deserialize<{typeof(T).Name}> FAIL: {ex.Message}. Body({body.Length} bytes) = {TruncateForLog(body)}");
            return default;
        }
    }

    private static string TruncateForLog(byte[] body)
    {
        try
        {
            var s = Encoding.UTF8.GetString(body);
            return s.Length > 300 ? s[..300] + "..." : s;
        }
        catch { return "<non-utf8 body>"; }
    }

    internal static (int, string) BadRequest(string msg) => (400, Serialize(new ErrorResponse { Error = msg }));

    /// <summary>HTTP 请求结构（请求行 + 头 + Body 二进制），internal 供测试构造</summary>
    internal sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
}
