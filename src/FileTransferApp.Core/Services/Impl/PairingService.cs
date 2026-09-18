using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 设备配对与加密协调服务实现（见 <see cref="IPairingService"/>）。
/// 密钥协商：双方长期 ECDH P-256 密钥对，配对时交换公钥，各自用对方公钥 + 己方私钥
/// 计算相同原始共享秘密，HKDF 派生出 AES-256 密钥。密钥仅落盘本机（pairs.json），不随网络传输。
/// 首次陌生设备自动配对并持久化，无需用户确认（局域网互传的默认信任模型）。
/// </summary>
public sealed class PairingService : IPairingService
{
    private readonly IDeviceIdentityStore _identityStore;
    private readonly IPairingStore _pairingStore;
    private readonly HttpClient _http;
    private readonly string _selfDeviceName;
    private readonly string _selfDeviceType;
    private readonly object _lock = new();

    public PairingService(
        IDeviceIdentityStore identityStore,
        IPairingStore pairingStore,
        string selfDeviceName,
        string selfDeviceType)
    {
        _identityStore = identityStore;
        _pairingStore = pairingStore;
        _selfDeviceName = selfDeviceName;
        _selfDeviceType = selfDeviceType;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>internal: 供单元测试注入 mock handler，验证握手流程而不起真实端口。</summary>
    internal PairingService(
        IDeviceIdentityStore identityStore,
        IPairingStore pairingStore,
        string selfDeviceName,
        string selfDeviceType,
        HttpMessageHandler handler)
    {
        _identityStore = identityStore;
        _pairingStore = pairingStore;
        _selfDeviceName = selfDeviceName;
        _selfDeviceType = selfDeviceType;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string SelfDeviceName => _selfDeviceName;
    public string SelfDeviceType => _selfDeviceType;

    public async Task<PeerSecurityContext> GetContextForSendAsync(DeviceNode peer, CancellationToken ct = default)
    {
        // v1 明文对端：不加密、不配对（向后兼容）
        if (peer.ProtocolVersion < ProtocolConstants.ProtocolVersion)
            return new PeerSecurityContext { IsProtocolV2 = false };

        var identity = await _identityStore.GetOrCreateAsync().ConfigureAwait(false);

        // 已有配对记录 → 直接加密
        var record = _pairingStore.FindByDeviceId(peer.DeviceId);
        if (record is not null && !string.IsNullOrEmpty(record.SharedKeyBase64))
            return BuildEncryptedContext(identity, record);

        // 未配对 → 自动发起握手
        var paired = await PairWithPeerAsync(peer, identity, ct).ConfigureAwait(false);
        if (paired is null) return new PeerSecurityContext { IsProtocolV2 = true, IsPaired = false };
        return BuildEncryptedContext(identity, paired);
    }

    public async Task<PairResponse> HandlePairRequestAsync(PairRequest request, DeviceNode peer)
    {
        var identity = await _identityStore.GetOrCreateAsync().ConfigureAwait(false);

        if (string.IsNullOrEmpty(request.DeviceId) || string.IsNullOrEmpty(request.PublicKeyBase64))
        {
            return new PairResponse { Accepted = false, Reason = "缺少设备 ID 或公钥" };
        }

        // 首次陌生设备自动配对（无需用户确认）；已配对设备重新握手属于幂等更新
        var sharedRaw = ComputeSharedRaw(identity, request.DeviceId, request.PublicKeyBase64);
        if (sharedRaw is null)
        {
            return new PairResponse { Accepted = false, Reason = "公钥无效" };
        }

        lock (_lock)
        {
            _pairingStore.Upsert(new PairRecord
            {
                PeerDeviceId = request.DeviceId,
                PeerDeviceName = string.IsNullOrEmpty(request.DeviceName) ? peer.DeviceName : request.DeviceName,
                PeerDeviceType = request.DeviceType,
                SharedKeyBase64 = DeviceCrypto.EncodeSharedKey(sharedRaw),
                PairedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow,
            });
        }

        return new PairResponse
        {
            Accepted = true,
            DeviceId = identity.DeviceId,
            DeviceName = _selfDeviceName,
            PublicKeyBase64 = identity.PublicKeyBase64,
        };
    }

    public byte[]? TryDecrypt(string? peerDeviceId, byte[] body)
    {
        if (string.IsNullOrEmpty(peerDeviceId) || body.Length < EncryptedFrame.Overhead) return null;
        var record = _pairingStore.FindByDeviceId(peerDeviceId);
        if (record is null || string.IsNullOrEmpty(record.SharedKeyBase64)) return null;
        try
        {
            var key = DeviceCrypto.DecodeSharedKey(record.SharedKeyBase64);
            var plain = DeviceCrypto.Decrypt(key, body);
            return plain;
        }
        catch
        {
            return null;
        }
    }

    public string? GetPeerDisplayName(string? peerDeviceId)
    {
        if (string.IsNullOrEmpty(peerDeviceId)) return null;
        return _pairingStore.FindByDeviceId(peerDeviceId)?.PeerDeviceName;
    }

    public IReadOnlyList<PairRecord> GetAllPairRecords() => _pairingStore.GetAll();

    public void Unpair(string peerDeviceId) => _pairingStore.Remove(peerDeviceId);

    // ---- 内部 ----

    private PeerSecurityContext BuildEncryptedContext(DeviceIdentity identity, PairRecord record)
    {
        byte[] key;
        try { key = DeviceCrypto.DecodeSharedKey(record.SharedKeyBase64); }
        catch { return new PeerSecurityContext { IsProtocolV2 = true, IsPaired = false }; }
        return new PeerSecurityContext
        {
            IsProtocolV2 = true,
            IsPaired = true,
            SharedKey = key,
            SelfDeviceId = identity.DeviceId,
        };
    }

    /// <summary>
    /// 向对端发起 /security/pair 握手。成功返回配对记录（含共享密钥），失败返回 null。
    /// 幂等：对端已有本机配对记录时同样返回本机所需身份与公钥。
    /// 明确拒绝（400 + Accepted=false）抛 <see cref="PairingDeniedException"/>，由发送引擎转为失败。
    /// </summary>
    private async Task<PairRecord?> PairWithPeerAsync(DeviceNode peer, DeviceIdentity identity, CancellationToken ct)
    {
        try
        {
            var baseUri = $"http://{peer.IpAddress}:{peer.Port}";
            var request = new PairRequest
            {
                DeviceId = identity.DeviceId,
                DeviceName = _selfDeviceName,
                DeviceType = _selfDeviceType,
                PublicKeyBase64 = identity.PublicKeyBase64,
            };
            using var content = PipelinesTransferEngine.BuildJsonContent(request);
            using var resp = await _http.PostAsync(baseUri + ProtocolConstants.PathPair, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 对端为 v1 老版本（无 /security/pair 端点 → 404）→ 降级明文
                if ((int)resp.StatusCode == 404) return null;

                // 对端明确拒绝配对（400 + Accepted=false）：视为用户拒绝，中止而非降级明文
                if (resp.StatusCode == HttpStatusCode.BadRequest)
                {
                    try
                    {
                        var pr = await resp.Content
                            .ReadFromJsonAsync<PairResponse>(cancellationToken: ct).ConfigureAwait(false);
                        if (pr is { Accepted: false } && !string.IsNullOrEmpty(pr.Reason))
                            throw new PairingDeniedException(pr.Reason);
                    }
                    catch (PairingDeniedException) { throw; }
                    catch { /* 非标准拒绝响应体，走通用失败 */ }
                }
                return null;
            }

            var response = await resp.Content.ReadFromJsonAsync<PairResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (response is null || !response.Accepted || string.IsNullOrEmpty(response.PublicKeyBase64))
                return null;

            var sharedRaw = ComputeSharedRaw(identity, response.DeviceId, response.PublicKeyBase64);
            if (sharedRaw is null) return null;

            var record = new PairRecord
            {
                PeerDeviceId = response.DeviceId,
                PeerDeviceName = string.IsNullOrEmpty(response.DeviceName) ? peer.DeviceName : response.DeviceName,
                PeerDeviceType = peer.DeviceType.ToString(),
                SharedKeyBase64 = DeviceCrypto.EncodeSharedKey(sharedRaw),
                PairedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow,
            };
            lock (_lock)
            {
                _pairingStore.Upsert(record);
            }
            return record;
        }
        catch (PairingDeniedException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>ECDH 原始共享秘密 + HKDF 派生 AES 密钥。失败（公钥非法/设备 ID 为空）返回 null。</summary>
    private static byte[]? ComputeSharedRaw(DeviceIdentity identity, string peerDeviceId, string peerPublicKeyBase64)
    {
        try
        {
            if (string.IsNullOrEmpty(peerDeviceId) || string.IsNullOrEmpty(peerPublicKeyBase64)) return null;
            var shared = DeviceCrypto.DeriveSharedKey(identity.PrivateKeyBase64, peerPublicKeyBase64);
            return DeviceCrypto.DeriveAesKey(shared, identity.DeviceId, peerDeviceId);
        }
        catch
        {
            return null;
        }
    }
}