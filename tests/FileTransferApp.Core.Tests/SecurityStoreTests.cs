using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 安装级身份与配对记录持久化测试：DeviceId/密钥对跨"进程"复用稳定，
/// 配对记录 upsert/remove 正确落盘并可重载。
/// </summary>
public class SecurityStoreTests
{
    [Fact]
    public async Task DeviceIdentity_IsPersistent_AcrossReinstances()
    {
        var store = new InMemorySettingsStore();
        var identity1 = await new DeviceIdentityStore(store).GetOrCreateAsync();

        // 模拟进程重启：用同一底层存储新建服务实例
        var identity2 = await new DeviceIdentityStore(store).GetOrCreateAsync();

        Assert.Equal(identity1.DeviceId, identity2.DeviceId);
        Assert.Equal(identity1.PublicKeyBase64, identity2.PublicKeyBase64);
        Assert.Equal(identity1.PrivateKeyBase64, identity2.PrivateKeyBase64);
        Assert.True(identity1.IsValid);
    }

    [Fact]
    public async Task DeviceIdentity_GeneratedOnce_ThenLoaded()
    {
        var store = new InMemorySettingsStore();
        var id1 = await new DeviceIdentityStore(store).GetOrCreateAsync();
        var id2 = await new DeviceIdentityStore(store).GetOrCreateAsync();
        Assert.NotNull(id1.DeviceId);
        Assert.False(string.IsNullOrEmpty(id1.PrivateKeyBase64));
        Assert.Equal(id1.DeviceId, id2.DeviceId);
        Assert.Equal(id1.PrivateKeyBase64, id2.PrivateKeyBase64);
    }

    [Fact]
    public async Task DeviceIdentity_CorruptedStore_Regenerates()
    {
        var store = new InMemorySettingsStore();
        store.WriteRaw(DeviceIdentityStore.StoreKey, "{ not valid json !!");
        var id = await new DeviceIdentityStore(store).GetOrCreateAsync();
        Assert.True(id.IsValid);
    }

    [Fact]
    public void PairingStore_Upsert_Find_Remove()
    {
        var store = new InMemorySettingsStore();
        var pairing = new PairingStore(store);

        Assert.False(pairing.IsPaired("peer-1"));

        pairing.Upsert(new PairRecord
        {
            PeerDeviceId = "peer-1",
            PeerDeviceName = "Phone",
            SharedKeyBase64 = Convert.ToBase64String(new byte[32]),
        });
        Assert.True(pairing.IsPaired("peer-1"));
        var rec = pairing.FindByDeviceId("peer-1");
        Assert.NotNull(rec);
        Assert.Equal("Phone", rec!.PeerDeviceName);

        pairing.Remove("peer-1");
        Assert.False(pairing.IsPaired("peer-1"));
        Assert.Empty(pairing.GetAll());
    }

    [Fact]
    public void PairingStore_IsPersistent_AcrossReinstances()
    {
        var store = new InMemorySettingsStore();
        var pairing1 = new PairingStore(store);
        pairing1.Upsert(new PairRecord { PeerDeviceId = "peer-x", SharedKeyBase64 = "base64key" });

        // 模拟重启：同一底层存储重载
        var pairing2 = new PairingStore(store);
        Assert.True(pairing2.IsPaired("peer-x"));
        Assert.Equal("base64key", pairing2.FindByDeviceId("peer-x")!.SharedKeyBase64);
    }

    [Fact]
    public void PairingStore_Remove_UpsertsTheSamePeer()
    {
        var store = new InMemorySettingsStore();
        var pairing = new PairingStore(store);
        pairing.Upsert(new PairRecord { PeerDeviceId = "peer", SharedKeyBase64 = "k1" });
        pairing.Upsert(new PairRecord { PeerDeviceId = "peer", SharedKeyBase64 = "k2", PeerDeviceName = "New" });

        var rec = pairing.FindByDeviceId("peer");
        Assert.Equal("k2", rec!.SharedKeyBase64);
        Assert.Equal("New", rec.PeerDeviceName);
        Assert.Single(pairing.GetAll());
    }
}