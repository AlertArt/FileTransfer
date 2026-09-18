using FileTransferApp.Core.Security;

namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>本机安装级持久身份（DeviceId + ECDH 密钥对）。</summary>
public interface IDeviceIdentityStore
{
    /// <summary>获取（必要时创建）本机身份。线程安全，进程内缓存。</summary>
    Task<DeviceIdentity> GetOrCreateAsync();
}

/// <summary>设备配对记录存储。</summary>
public interface IPairingStore
{
    PairRecord? FindByDeviceId(string peerDeviceId);
    bool IsPaired(string peerDeviceId);
    void Upsert(PairRecord record);
    void Remove(string peerDeviceId);
    IReadOnlyList<PairRecord> GetAll();
}