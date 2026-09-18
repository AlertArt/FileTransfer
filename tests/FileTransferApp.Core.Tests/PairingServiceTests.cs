using System.Net;
using System.Text;
using System.Text.Json;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 配对协调服务测试：收发两端 ECDH 握手后得到一致共享密钥、
/// 相同设备免重复配对（记录命中直接加密）、v1 明文对端降级不加密、
/// 服务端解密入站请求、未配对返回 401 语义。
/// </summary>
public class PairingServiceTests
{
    private sealed class DeviceSide : IDisposable
    {
        public InMemorySettingsStore Settings { get; } = new();
        public DeviceIdentityStore Identity => new(Settings);
        public PairingStore Pairing { get; }
        public PairingService Service { get; }

        public DeviceSide(
            string name,
            string deviceType,
            HttpMessageHandler? handler = null)
        {
            Pairing = new PairingStore(Settings);
            Service = handler is null
                ? new PairingService(Identity, Pairing, name, deviceType)
                : new PairingService(Identity, Pairing, name, deviceType, handler);
        }

        public void Dispose() { }
    }

    private static DeviceNode MakePeer(string deviceId, string name = "Peer", int protocol = ProtocolConstants.ProtocolVersion)
        => new()
        {
            DeviceId = deviceId,
            DeviceName = name,
            DeviceType = DeviceType.Android,
            Port = ProtocolConstants.TransferPort,
            ProtocolVersion = protocol,
            IpAddress = IPAddress.Loopback,
        };

    [Fact]
    public async Task Handshake_TwoSides_DeriveSameSharedKey_AndPersist()
    {
        var a = new DeviceSide("DevA", "Windows");
        var b = new DeviceSide("DevB", "Android");

        var aId = await a.Identity.GetOrCreateAsync();
        var bId = await b.Identity.GetOrCreateAsync();

        // A 发起配对 → B 接收端响应
        var req = new PairRequest
        {
            DeviceId = aId.DeviceId,
            DeviceName = "DevA",
            DeviceType = "Windows",
            PublicKeyBase64 = aId.PublicKeyBase64,
        };
        var resp = await b.Service.HandlePairRequestAsync(req, MakePeer("anyip", "DevA"));
        Assert.True(resp.Accepted);
        Assert.Equal(bId.DeviceId, resp.DeviceId);

        // B 侧持久化记录持有共享密钥
        var bKey = b.Pairing.FindByDeviceId(aId.DeviceId)!.SharedKeyBase64;

        // A 侧用 B 的响应公钥 + 己方私钥推导，应与 B 侧一致（ECDH 对称性 + HKDF 可交换排序）
        var aRaw = DeviceCrypto.DeriveSharedKey(aId.PrivateKeyBase64, resp.PublicKeyBase64);
        var aKey = DeviceCrypto.DeriveAesKey(aRaw, aId.DeviceId, resp.DeviceId);
        Assert.Equal(Convert.ToHexString(aKey), Convert.ToHexString(DeviceCrypto.DecodeSharedKey(bKey)));
    }

    [Fact]
    public async Task Handshake_ClientSide_PairsOnce_ThenReusesRecord_NoSecondHandshake()
    {
        var a = new DeviceSide("DevA", "Windows");
        var b = new DeviceSide("DevB", "Android");

        // mock 对端服务器：返回 PairResponse（对端身份 + 公钥）
        var bId = await b.Identity.GetOrCreateAsync();
        var handler = new MockPairHandler(bId.DeviceId, "DevB", bId.PublicKeyBase64);
        var aClient = new DeviceSide("DevA", "Windows", handler);

        var peerB = MakePeer(bId.DeviceId, "DevB");
        var ctx1 = await aClient.Service.GetContextForSendAsync(peerB);

        Assert.True(ctx1.IsProtocolV2);
        Assert.True(ctx1.IsPaired);
        Assert.True(ctx1.IsEncrypted);
        Assert.NotNull(ctx1.SharedKey);
        Assert.NotNull(aClient.Pairing.FindByDeviceId(bId.DeviceId));
        Assert.Equal(1, handler.HandshakeCount);

        // 再次请求：命中已持久化记录，不应再触发第二次握手
        var ctx2 = await aClient.Service.GetContextForSendAsync(peerB);
        Assert.True(ctx2.IsEncrypted);
        Assert.Equal(1, handler.HandshakeCount);
    }

    [Fact]
    public async Task GetContext_V1Peer_ReturnsNotEncrypted_WithoutPairing()
    {
        var a = new DeviceSide("DevA", "Windows");
        var peerV1 = MakePeer("old-peer", "Old", protocol: 1);
        var ctx = await a.Service.GetContextForSendAsync(peerV1);
        Assert.False(ctx.IsProtocolV2);
        Assert.False(ctx.IsEncrypted);
    }

    [Fact]
    public async Task TryDecrypt_AfterPairing_DecryptsInboundBody()
    {
        var a = new DeviceSide("DevA", "Windows");
        var b = new DeviceSide("DevB", "Android");

        var aId = await a.Identity.GetOrCreateAsync();
        var bId = await b.Identity.GetOrCreateAsync();

        // B 服务端先完成与 A 的配对
        var req = new PairRequest
        {
            DeviceId = aId.DeviceId,
            DeviceName = "DevA",
            DeviceType = "Windows",
            PublicKeyBase64 = aId.PublicKeyBase64,
        };
        await b.Service.HandlePairRequestAsync(req, MakePeer("ip", "DevA"));

        // A 侧推导同一密钥加密控制命令，B 应能解密还原明文
        var aRaw = DeviceCrypto.DeriveSharedKey(aId.PrivateKeyBase64, bId.PublicKeyBase64);
        var aKey = DeviceCrypto.DeriveAesKey(aRaw, aId.DeviceId, bId.DeviceId);

        var plaintext = Encoding.UTF8.GetBytes("{\"fileId\":\"f-1\",\"action\":\"PAUSE\"}");
        var frame = DeviceCrypto.Encrypt(aKey, plaintext);
        var decrypted = b.Service.TryDecrypt(aId.DeviceId, frame);
        Assert.NotNull(decrypted);
        Assert.Equal(Encoding.UTF8.GetString(plaintext), Encoding.UTF8.GetString(decrypted!));
    }

    [Fact]
    public async Task TryDecrypt_UnpairedPeer_ReturnsNull()
    {
        var b = new DeviceSide("DevB", "Android");
        var stranger = new DeviceSide("Stranger", "Windows");
        var sId = await stranger.Identity.GetOrCreateAsync();
        var frame = DeviceCrypto.Encrypt(new byte[32], new byte[] { 1 });

        Assert.Null(b.Service.TryDecrypt(sId.DeviceId, frame));
    }

    [Fact]
    public async Task TryDecrypt_TamperedFrame_ReturnsNull()
    {
        var a = new DeviceSide("DevA", "Windows");
        var b = new DeviceSide("DevB", "Android");
        var aId = await a.Identity.GetOrCreateAsync();
        await b.Service.HandlePairRequestAsync(new PairRequest
        {
            DeviceId = aId.DeviceId,
            DeviceName = "DevA",
            DeviceType = "Windows",
            PublicKeyBase64 = aId.PublicKeyBase64,
        }, MakePeer("ip", "DevA"));

        var bId = await b.Identity.GetOrCreateAsync();
        var raw = DeviceCrypto.DeriveSharedKey(aId.PrivateKeyBase64, bId.PublicKeyBase64);
        var key = DeviceCrypto.DeriveAesKey(raw, aId.DeviceId, bId.DeviceId);
        var frame = DeviceCrypto.Encrypt(key, new byte[] { 42 });
        frame[^1] ^= 0x01; // 篡改

        Assert.Null(b.Service.TryDecrypt(aId.DeviceId, frame));
    }

    [Fact]
    public async Task Unpair_RemovesRecord()
    {
        var a = new DeviceSide("DevA", "Windows");
        var aId = await a.Identity.GetOrCreateAsync();
        a.Pairing.Upsert(new PairRecord { PeerDeviceId = aId.DeviceId, SharedKeyBase64 = "x" });
        Assert.Single(a.Service.GetAllPairRecords());
        a.Service.Unpair(aId.DeviceId);
        Assert.Empty(a.Service.GetAllPairRecords());
    }

    [Fact]
    public async Task GetContextForSendAsync_PeerDeniesPairing_ThrowsDenied()
    {
        var a = new DeviceSide("DevA", "Windows", handler: new DenyPairHandler());
        var peer = MakePeer("peer-id-1", "DevB");

        var ex = await Assert.ThrowsAsync<PairingDeniedException>(
            () => a.Service.GetContextForSendAsync(peer));

        Assert.Contains("拒绝", ex.Message);
        Assert.Empty(a.Service.GetAllPairRecords());
    }

    /// <summary>mock 对端 /security/pair 拒绝服务器：400 + Accepted=false</summary>
    private sealed class DenyPairHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(new PairResponse { Accepted = false, Reason = "用户拒绝配对" });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>mock 对端 /security/pair 服务器：始终返回 200 + PairResponse，并记录调用次数</summary>
    private sealed class MockPairHandler : HttpMessageHandler
    {
        private readonly string _deviceId;
        private readonly string _deviceName;
        private readonly string _publicKey;
        public int HandshakeCount;

        public MockPairHandler(string deviceId, string deviceName, string publicKey)
        {
            _deviceId = deviceId;
            _deviceName = deviceName;
            _publicKey = publicKey;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HandshakeCount++;
            var resp = new PairResponse
            {
                Accepted = true,
                DeviceId = _deviceId,
                DeviceName = _deviceName,
                PublicKeyBase64 = _publicKey,
            };
            var json = JsonSerializer.Serialize(resp);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}