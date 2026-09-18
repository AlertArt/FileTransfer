using System.Text.Json;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 本机安装级持久身份服务。首次启动生成 DeviceId + ECDH P-256 密钥对，
/// 之后每次进程启动复用同一身份（不再像 v1 那样每启动生成新 DeviceId）。
/// </summary>
public sealed class DeviceIdentityStore : IDeviceIdentityStore, IDisposable
{
    public const string StoreKey = "device_identity";

    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeviceIdentity? _cached;

    public DeviceIdentityStore(ISettingsStore store) => _store = store;

    /// <summary>获取（必要时创建）本机身份。线程安全，进程内缓存。</summary>
    public async Task<DeviceIdentity> GetOrCreateAsync()
    {
        if (_cached is { IsValid: true }) return _cached;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cached is { IsValid: true }) return _cached;

            var existing = TryLoad();
            if (existing is { IsValid: true })
            {
                _cached = existing;
                return _cached;
            }

            var created = CreateNew();
            Persist(created);
            _cached = created;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>进程内同步版（供构造期生成身份无 async 阻塞）。内部仍是异步实现并同步等待。</summary>
    public DeviceIdentity GetOrCreate()
        => GetOrCreateAsync().GetAwaiter().GetResult();

    private DeviceIdentity? TryLoad()
    {
        var json = _store.ReadRaw(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DeviceIdentity>(json);
        }
        catch
        {
            return null;
        }
    }

    private static DeviceIdentity CreateNew()
    {
        var (pub, priv) = DeviceCrypto.GenerateKeyPair();
        return new DeviceIdentity
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            PublicKeyBase64 = pub,
            PrivateKeyBase64 = priv,
        };
    }

    private void Persist(DeviceIdentity identity)
        => _store.WriteRaw(StoreKey, JsonSerializer.Serialize(identity, JsonOpts));

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public void Dispose() => _gate.Dispose();
}