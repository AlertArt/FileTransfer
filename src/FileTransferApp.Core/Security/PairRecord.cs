namespace FileTransferApp.Core.Security;

/// <summary>
/// 与对端设备的配对记录：设备 ID + 名称 + 经 ECDH 协商、HKDF 派生的 AES-256 共享密钥。
/// 持久化于 pairs.json；同类设备再次配对时直接命中，无需重复握手。
/// </summary>
public sealed class PairRecord
{
    public string PeerDeviceId { get; set; } = string.Empty;
    public string PeerDeviceName { get; set; } = string.Empty;
    public string PeerDeviceType { get; set; } = string.Empty;

    /// <summary>共享密钥（base64, 32 字节 AES-256）</summary>
    public string SharedKeyBase64 { get; set; } = string.Empty;

    /// <summary>首次配对时间 (UTC)</summary>
    public DateTime PairedUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}