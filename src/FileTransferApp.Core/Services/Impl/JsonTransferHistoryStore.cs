using System.Text.Json;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 基于 <see cref="ISettingsStore"/> 的 JSON 历史存储。
/// 内存列表 + 每次变更写盘；上限 <see cref="MaxEntries"/> 条（超出丢弃最旧），避免历史文件无限增长。
/// 读写失败静默（历史非关键路径）。
/// </summary>
public sealed class JsonTransferHistoryStore : ITransferHistoryStore
{
    public const string SettingsKey = "transfer-history";
    public const int MaxEntries = 200;

    private readonly ISettingsStore _settings;
    private readonly object _gate = new();
    private readonly List<TransferHistoryEntry> _entries = new();

    public JsonTransferHistoryStore(ISettingsStore settings)
    {
        _settings = settings;
        Load();
    }

    public IReadOnlyList<TransferHistoryEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    public void AddOrUpdate(TransferHistoryEntry entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.FileId)) return;
        lock (_gate)
        {
            // 同 FileId 覆盖，并前移到最前（最新在前）
            var idx = _entries.FindIndex(e => e.FileId == entry.FileId);
            if (idx >= 0) _entries.RemoveAt(idx);
            _entries.Insert(0, entry);

            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            var raw = _settings.ReadRaw(SettingsKey);
            if (string.IsNullOrWhiteSpace(raw)) return;
            var list = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(raw);
            if (list is not null) _entries.AddRange(list);
        }
        catch { /* 历史损坏按空处理 */ }
    }

    private void Save()
    {
        try { _settings.WriteRaw(SettingsKey, JsonSerializer.Serialize(_entries)); }
        catch { /* 持久化失败不影响运行 */ }
    }
}
