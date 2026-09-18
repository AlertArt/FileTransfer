using System.Text.Json;
using FileTransferApp.Core.Security;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 设备配对存储：持久化 对端DeviceId → PairRecord（含共享密钥）。
/// 相同设备再次发起传输时直接命中记录，无需重复配对握手。
/// </summary>
public sealed class PairingStore : IPairingStore, IDisposable
{
    public const string StoreKey = "pairs";

    private readonly ISettingsStore _store;
    private readonly object _lock = new();
    private readonly List<PairRecord> _records;
    private bool _dirty;

    public PairingStore(ISettingsStore store)
    {
        _store = store;
        _records = Load();
    }

    private List<PairRecord> Load()
    {
        var json = _store.ReadRaw(StoreKey);
        if (string.IsNullOrWhiteSpace(json)) return new List<PairRecord>();
        try
        {
            return JsonSerializer.Deserialize<List<PairRecord>>(json) ?? new List<PairRecord>();
        }
        catch
        {
            // 数据损坏时以全新列表启动，避免阻断传输
            return new List<PairRecord>();
        }
    }

    /// <summary>按对端设备 ID 查找配对记录（存在即"已配对"）。</summary>
    public PairRecord? FindByDeviceId(string peerDeviceId)
    {
        if (string.IsNullOrEmpty(peerDeviceId)) return null;
        lock (_lock)
        {
            return _records.FirstOrDefault(r => r.PeerDeviceId == peerDeviceId);
        }
    }

    /// <summary>按对端设备 ID 判断是否已配对。</summary>
    public bool IsPaired(string peerDeviceId)
        => FindByDeviceId(peerDeviceId) is not null;

    /// <summary>新增或更新配对记录（upsert by peerDeviceId），立即持久化。</summary>
    public void Upsert(PairRecord record)
    {
        lock (_lock)
        {
            var idx = _records.FindIndex(r => r.PeerDeviceId == record.PeerDeviceId);
            if (idx >= 0) _records[idx] = record;
            else _records.Add(record);
            _dirty = true;
        }
        Persist();
    }

    /// <summary>解除配对：删除记录并持久化。此后该设备若再传输需重新配对。</summary>
    public void Remove(string peerDeviceId)
    {
        lock (_lock)
        {
            var idx = _records.FindIndex(r => r.PeerDeviceId == peerDeviceId);
            if (idx < 0) return;
            _records.RemoveAt(idx);
            _dirty = true;
        }
        Persist();
    }

    /// <summary>当前全部配对记录（副本，避免调用方修改内部集合）。</summary>
    public IReadOnlyList<PairRecord> GetAll()
    {
        lock (_lock)
        {
            return _records.ToArray();
        }
    }

    private void Persist()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            _store.WriteRaw(StoreKey, JsonSerializer.Serialize(_records, JsonOpts));
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public void Dispose() { }
}