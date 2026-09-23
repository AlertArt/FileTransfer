namespace FileTransferApp.Core.Protocols;

/// <summary>通信协议常量：UDP 发现端口、HTTP/TCP 传输端口、心跳/离线阈值、路由路径与自定义头。</summary>
public static class ProtocolConstants
{
    // ---- 设备发现 (UDP) ----
    public const int DiscoveryPort = 53317;
    public const int HeartbeatIntervalMs = 3000;
    // 离线判定阈值。放宽到 30s：Android 在 Wi-Fi 省电/组播受限下可能间歇性收不到广播，
    // 10s 会导致"发现→丢失→发现"反复上下线；30s 可容忍约 10 次心跳丢失。
    public const int OfflineTimeoutMs = 30000;

    /// <summary>协议版本：v1=明文；v2=设备配对 + AES-GCM 加密传输（向后兼容 v1 明文）。</summary>
    public const int ProtocolVersion = 2;

    /// <summary>UDP 多播组地址</summary>
    public const string MulticastGroup = "239.255.255.250";

    // ---- 文件传输 (HTTP/TCP) ----
    public const int TransferPort = 53318;
    public const int ChunkSize = 2 * 1024 * 1024; // 2MB
    public const int ReceiveBufferSize = 64 * 1024; // 64KB Pipelines 缓冲

    // ---- RESTful 路由 ----
    public const string PathPrepare = "/api/v1/transfer/prepare";
    public const string PathChunk = "/api/v1/transfer/chunk";
    public const string PathControl = "/api/v1/transfer/control";
    /// <summary>设备配对握手：v2 设备传输前需先经此端点交换公钥、协商共享密钥并持久化。</summary>
    public const string PathPair = "/api/v1/security/pair";

    // ---- 自定义请求头 ----
    public const string HeaderFileId = "X-File-Id";
    public const string HeaderChunkIndex = "X-Chunk-Index";
    public const string HeaderChunkHash = "X-Chunk-Hash";
    public const string HeaderContentLength = "Content-Length";
    /// <summary>发送方设备唯一 ID（v2 所有传输请求必须携带，用于识别设备并选择共享密钥解密）</summary>
    public const string HeaderDeviceId = "X-Device-Id";
    /// <summary>协议版本声明（=2 表示按 v2 加密解析请求体；缺失/1 视为 v1 明文兼容）</summary>
    public const string HeaderProtocolVersion = "X-Protocol";

    // ---- 加密参数 ----
    /// <summary>共享密钥字节数（AES-256）</summary>
    public const int KeySizeBytes = 32;
    /// <summary>AES-GCM nonce 长度</summary>
    public const int NonceSize = 12;
    /// <summary>AES-GCM tag 长度</summary>
    public const int TagSize = 16;
}
