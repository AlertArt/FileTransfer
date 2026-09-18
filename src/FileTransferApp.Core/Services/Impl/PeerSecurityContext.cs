using System.Net.Http.Headers;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Security;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 与单个对端设备的一次会话安全上下文。
/// 封装：对端协议版本、是否已配对、共享密钥。发送端在握手前构造，决定请求体是否加密。
/// </summary>
public sealed class PeerSecurityContext
{
    public bool IsProtocolV2 { get; init; }
    public bool IsPaired { get; init; }
    public byte[]? SharedKey { get; init; }

    /// <summary>本机设备 ID（加密请求头 X-Device-Id 用）</summary>
    public string? SelfDeviceId { get; init; }

    /// <summary>是否启用端到端加密（v2 协议 且 已配对 且 有共享密钥）。</summary>
    public bool IsEncrypted => IsProtocolV2 && IsPaired && SharedKey is not null && SharedKey.Length == ProtocolConstants.KeySizeBytes;

    /// <summary>加密模式下的请求头声明（X-Protocol: 2 与 X-Device-Id）。</summary>
    public void ApplyEncryptionHeaders(HttpHeaders headers, string selfDeviceId)
    {
        if (!IsEncrypted) return;
        headers.Remove(ProtocolConstants.HeaderProtocolVersion);
        headers.Add(ProtocolConstants.HeaderProtocolVersion, ProtocolConstants.ProtocolVersion.ToString());
        headers.Remove(ProtocolConstants.HeaderDeviceId);
        headers.Add(ProtocolConstants.HeaderDeviceId, selfDeviceId);
    }
}