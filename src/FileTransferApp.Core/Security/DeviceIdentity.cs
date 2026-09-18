using System.Text.Json.Serialization;

namespace FileTransferApp.Core.Security;

/// <summary>
/// 本机安装级持久身份：设备唯一 ID + 长期 ECDH(P-256) 密钥对。
/// DeviceId 不再像 v1 那样每次进程启动重新生成，保证配对记录与设备身份跨会话稳定。
/// 持久化于 device_identity.json（应用私有数据目录），密钥不随协议传输。
/// </summary>
public sealed class DeviceIdentity
{
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>ECDH P-256 公钥（SubjectPublicKeyInfo, base64）</summary>
    public string PublicKeyBase64 { get; set; } = string.Empty;

    /// <summary>ECDH P-256 私钥（PKCS8, base64）。仅本机持有，绝不参与任何协议报文。</summary>
    public string PrivateKeyBase64 { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrEmpty(DeviceId)
        && !string.IsNullOrEmpty(PublicKeyBase64)
        && !string.IsNullOrEmpty(PrivateKeyBase64);
}