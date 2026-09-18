using System.Text;
using System.Text.Json;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// v2 加密通道 HTTP 服务端测试：带 X-Device-Id 加密请求解密成功并充实 peer、
/// 未配对返回 401 语义、v1 明文请求（无 X-Device-Id）直通、/security/pair 路由分发。
/// </summary>
public class TransferEncryptionTests
{
    private (PairingService Pairing, TransferHttpServer Server) Setup(
        InMemorySettingsStore? store = null,
        string selfName = "Server", string selfType = "Windows")
    {
        var s = store ?? new InMemorySettingsStore();
        var identity = new DeviceIdentityStore(s);
        var pairing = new PairingStore(s);
        var pairingService = new PairingService(identity, pairing, selfName, selfType);
        var engine = new NoopEngine();
        return (pairingService, new TransferHttpServer(engine, pairingService));
    }

    [Fact]
    public void EncryptedPreparedAttention_Unauthorized_WhenNotPaired()
    {
        // 未配对服务端收到带 X-Device-Id 的加密请求 → 401
        var (_, server) = Setup();
        var req = new TransferHttpServer.HttpRequest(
            "POST", ProtocolConstants.PathPrepare,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ProtocolConstants.HeaderDeviceId] = "stranger-device",
            },
            new byte[32]);

        var peer = new DeviceNode { IpAddress = System.Net.IPAddress.Loopback, Port = 0 };
        bool ok = server.TryDecryptV2Request(ref req, ref peer, out var status, out var json);
        Assert.False(ok);
        Assert.Equal(401, status);
        var err = JsonSerializer.Deserialize<ErrStub>(json, CiOpts);
        Assert.NotNull(err);
        Assert.Contains("未配对", err!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainV1Request_PassesThrough_WithoutDeviceId()
    {
        // v1 兼容：无 X-Device-Id → 原样直通，不触碰配对
        var (_, server) = Setup();
        var body = Encoding.UTF8.GetBytes("{\"fileId\":\"f\",\"action\":\"PAUSE\"}");
        var req = new TransferHttpServer.HttpRequest(
            "POST", ProtocolConstants.PathControl,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            body);
        var peer = new DeviceNode { IpAddress = System.Net.IPAddress.Loopback, Port = 0 };

        bool ok = server.TryDecryptV2Request(ref req, ref peer, out _, out _);
        Assert.True(ok);
        Assert.Equal(body, req.Body); // 未修改
        Assert.Equal(string.Empty, peer.DeviceId);
    }

    [Fact]
    public async Task EncryptedRequest_Decrypts_AndEnrichesPeerDeviceInfo()
    {
        // 先完成配对，再用对端设备加密 body 发送 → B 服务端应解密成功且 peer 设备名/ID 填充
        var store = new InMemorySettingsStore();
        var (pairing, server) = Setup(store, "ServerB", "Windows");

        // 对端 A 的身份与公钥
        var aStore = new InMemorySettingsStore();
        var aIdentity = await new DeviceIdentityStore(aStore).GetOrCreateAsync();

        // B 服务端接受 A 的配对
        var pairReq = new PairRequest
        {
            DeviceId = aIdentity.DeviceId,
            DeviceName = "PhoneA",
            DeviceType = "Android",
            PublicKeyBase64 = aIdentity.PublicKeyBase64,
        };
        var pairResp = await pairing.HandlePairRequestAsync(pairReq, new DeviceNode { IpAddress = System.Net.IPAddress.Loopback, Port = 0 });
        Assert.True(pairResp.Accepted);

        // A 侧推导同一密钥
        var aKeyRaw = DeviceCrypto.DeriveSharedKey(aIdentity.PrivateKeyBase64, pairResp.PublicKeyBase64);
        var aKey = DeviceCrypto.DeriveAesKey(aKeyRaw, aIdentity.DeviceId, pairResp.DeviceId);

        // A 构造加密 prepare body
        var plaintext = Encoding.UTF8.GetBytes("{\"fileId\":\"f-1\",\"fileName\":\"a.bin\",\"fileSize\":100,\"chunkSize\":1024,\"sha256\":\"\"}");
        var frame = DeviceCrypto.Encrypt(aKey, plaintext);

        var req = new TransferHttpServer.HttpRequest(
            "POST", ProtocolConstants.PathPrepare,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ProtocolConstants.HeaderDeviceId] = aIdentity.DeviceId,
                [ProtocolConstants.HeaderProtocolVersion] = ProtocolConstants.ProtocolVersion.ToString(),
            },
            frame);

        var peer = new DeviceNode { IpAddress = System.Net.IPAddress.Loopback, Port = 0 };
        bool ok = server.TryDecryptV2Request(ref req, ref peer, out _, out _);
        Assert.True(ok);
        Assert.Equal(plaintext, req.Body);
        Assert.Equal(aIdentity.DeviceId, peer.DeviceId);
        Assert.Equal("PhoneA", peer.DeviceName);
    }

    [Fact]
    public async Task PairRoute_RejectsEmptyPublicKey()
    {
        var store = new InMemorySettingsStore();
        var (_, server) = Setup(store);
        var body = Encoding.UTF8.GetBytes("{\"deviceId\":\"x\",\"deviceName\":\"y\",\"deviceType\":\"z\",\"publicKeyBase64\":\"\"}");
        var req = new TransferHttpServer.HttpRequest(
            "POST", ProtocolConstants.PathPair,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            body);
        var (status, _) = await server.HandlePairAsync(req, new DeviceNode { IpAddress = System.Net.IPAddress.Loopback, Port = 0 });
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task PairRoute_ReturnsServerIdentity_WhenAccepted()
    {
        var store = new InMemorySettingsStore();
        var (_, server) = Setup(store, "ServerB", "Windows");
        var identity = await new DeviceIdentityStore(store).GetOrCreateAsync();

        var otherPub = DeviceCrypto.GenerateKeyPair().PublicKeyBase64;
        var body = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new PairRequest
        {
            DeviceId = "other-id",
            DeviceName = "Other",
            DeviceType = "MacOS",
            PublicKeyBase64 = otherPub,
        }));
        var req = new TransferHttpServer.HttpRequest(
            "POST", ProtocolConstants.PathPair,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            body);
        var (status, json) = await server.HandlePairAsync(req, new DeviceNode { IpAddress = System.Net.IPAddress.Loopback, Port = 0 });
        Assert.Equal(200, status);
        var resp = System.Text.Json.JsonSerializer.Deserialize<PairResponse>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(resp);
        Assert.True(resp!.Accepted);
        Assert.Equal(identity.DeviceId, resp.DeviceId);
        Assert.Equal(identity.PublicKeyBase64, resp.PublicKeyBase64);
    }

    private static readonly JsonSerializerOptions CiOpts = new() { PropertyNameCaseInsensitive = true };
    private sealed class ErrStub { public string? Error { get; set; } }
    private sealed class NoopEngine : ITransferEngine
    {
#pragma warning disable CS0067 // 事件由引擎持有者订阅，测试中不触发
        public event EventHandler<TransferStatusChangedMessage>? StateChanged;
#pragma warning restore CS0067
        public Task CancelAsync(string fileId) => Task.CompletedTask;
        public Task ApplyControlAsync(string fileId, TransferAction action) => Task.CompletedTask;
        public Task<TransferTaskInfo> CreateSendTaskAsync(string filePath, DeviceNode peer, CancellationToken ct = default) => Task.FromResult(new TransferTaskInfo { FileId = Guid.NewGuid().ToString("N") });
        public Task PauseAsync(string fileId) => Task.CompletedTask;
        public Task RemoveTaskAsync(string fileId) => Task.CompletedTask;
        public Task RetryAsync(string fileId) => Task.CompletedTask;
        public Task ResumeAsync(string fileId) => Task.CompletedTask;
        public Task StartSendAsync(string fileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PrepareResponse> RegisterReceiveTaskAsync(PrepareRequest prepare, DeviceNode peer) => Task.FromResult(new PrepareResponse { Accepted = true });
        public Task<bool> WriteChunkAsync(string fileId, int chunkIndex, int totalChunks, ReadOnlyMemory<byte> data) => Task.FromResult(true);
        public IReadOnlyList<TransferTaskInfo> GetTasks() => Array.Empty<TransferTaskInfo>();
        public TransferTaskInfo? GetTask(string fileId) => null;
    }
}