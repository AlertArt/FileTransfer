using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Impl;

namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 设备配对与加密协调服务。
/// - 发送端：GetContextForSendAsync 在发起传输前确保与对端配对，返回加密会话上下文。
/// - 接收端：HandlePairRequestAsync 响应 /security/pair 握手；TryDecrypt 解密入站请求体。
/// 配对记录持久化（pairs.json），相同设备无需重复握手。
/// </summary>
public interface IPairingService
{
    /// <summary>本机设备名（用于配对报文）</summary>
    string SelfDeviceName { get; }

    /// <summary>本机设备类型（用于配对报文）</summary>
    string SelfDeviceType { get; }

    /// <summary>
    /// 获取向对端发送所需的安全上下文。
    /// - 对端为 v1 明文协议 → 返回未加密上下文（向后兼容）。
    /// - 对端为 v2 且已有配对记录 → 直接返回加密上下文。
    /// - 对端为 v2 且未配对 → 自动发起 /security/pair 握手并持久化，再返回加密上下文。
    /// </summary>
    Task<PeerSecurityContext> GetContextForSendAsync(DeviceNode peer, CancellationToken ct = default);

    /// <summary>接收端处理入站配对请求：计算共享密钥、持久化、返回本机公钥。幂等（同设备重新配对返回同样密钥）。</summary>
    Task<PairResponse> HandlePairRequestAsync(PairRequest request, DeviceNode peer);

    /// <summary>用对端设备 ID 对应的共享密钥解密入站请求体。未配对或解密失败返回 null。</summary>
    byte[]? TryDecrypt(string? peerDeviceId, byte[] body);

    /// <summary>解密成功时读取配对记录中的对端显示名（供审批 UI 展示真实设备名）。</summary>
    string? GetPeerDisplayName(string? peerDeviceId);

    /// <summary>当前全部配对记录。</summary>
    IReadOnlyList<PairRecord> GetAllPairRecords();

    /// <summary>解除与指定设备的配对（删除本地密钥记录）。</summary>
    void Unpair(string peerDeviceId);
}