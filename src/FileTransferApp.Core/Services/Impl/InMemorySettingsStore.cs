using System.Collections.Concurrent;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>测试用内存设置存储：不落盘，进程内读写。</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly ConcurrentDictionary<string, string> _map = new();

    public string? ReadRaw(string key) => _map.TryGetValue(key, out var v) ? v : null;
    public void WriteRaw(string key, string json) => _map[key] = json;
    public void Remove(string key) => _map.TryRemove(key, out _);

    internal IReadOnlyDictionary<string, string> Snapshot => _map;
}