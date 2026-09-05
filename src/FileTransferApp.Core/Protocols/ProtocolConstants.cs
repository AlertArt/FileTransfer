namespace FileTransferApp.Core.Protocols;

/// <summary>通信协议常量：UDP 发现端口、HTTP/TCP 传输端口、心跳/离线阈值、路由路径与自定义头。</summary>
public static class ProtocolConstants
{
    // ---- 设备发现 (UDP) ----
    public const int DiscoveryPort = 53317;
    public const int HeartbeatIntervalMs = 3000;
    public const int OfflineTimeoutMs = 10000;
    public const int ProtocolVersion = 1;

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

    // ---- 自定义请求头 ----
    public const string HeaderFileId = "X-File-Id";
    public const string HeaderChunkIndex = "X-Chunk-Index";
    public const string HeaderChunkHash = "X-Chunk-Hash";
    public const string HeaderContentLength = "Content-Length";
}
