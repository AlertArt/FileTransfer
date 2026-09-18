namespace FileTransferApp.Core.Models;

/// <summary>/security/pair 配对请求体（明文，仅首次交换公钥；本机公钥无密级）</summary>
public sealed class PairRequest
{
    /// <summary>发起方设备 ID（与心跳 DeviceId 一致）</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>发起方设备名称（用于接收端展示）</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>发起方设备类型</summary>
    public string DeviceType { get; set; } = string.Empty;

    /// <summary>发起方 ECDH P-256 公钥（SubjectPublicKeyInfo, base64）</summary>
    public string PublicKeyBase64 { get; set; } = string.Empty;
}

/// <summary>/security/pair 配对响应体</summary>
public sealed class PairResponse
{
    public bool Accepted { get; set; }

    /// <summary>响应方设备 ID</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>响应方设备名称</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>响应方 ECDH P-256 公钥（SubjectPublicKeyInfo, base64）</summary>
    public string PublicKeyBase64 { get; set; } = string.Empty;

    /// <summary>已被拒/已配对的说明（可选）</summary>
    public string? Reason { get; set; }
}