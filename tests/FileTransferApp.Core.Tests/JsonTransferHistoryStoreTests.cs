using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>传输历史存储：去重、上限、清空、跨实例持久化。</summary>
public class JsonTransferHistoryStoreTests
{
    private static TransferHistoryEntry Entry(string id) => new()
    {
        FileId = id,
        FileName = $"{id}.bin",
        Direction = TransferDirection.Send,
        TotalBytes = 100,
        State = TransferState.Completed,
        StartedUtc = DateTime.UtcNow,
        EndedUtc = DateTime.UtcNow,
    };

    [Fact]
    public void AddOrUpdate_Inserts_Newest_First_And_Dedupes_By_FileId()
    {
        var store = new JsonTransferHistoryStore(new InMemorySettingsStore());
        store.AddOrUpdate(Entry("a"));
        store.AddOrUpdate(Entry("b"));
        store.AddOrUpdate(Entry("a")); // 同 FileId → 覆盖并前移，不重复

        var all = store.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("a", all[0].FileId);
    }

    [Fact]
    public void AddOrUpdate_Enforces_MaxEntries()
    {
        var store = new JsonTransferHistoryStore(new InMemorySettingsStore());
        for (var i = 0; i < JsonTransferHistoryStore.MaxEntries + 25; i++)
            store.AddOrUpdate(Entry($"f{i}"));

        Assert.Equal(JsonTransferHistoryStore.MaxEntries, store.GetAll().Count);
    }

    [Fact]
    public void Clear_Empties_And_Persists()
    {
        var settings = new InMemorySettingsStore();
        var store = new JsonTransferHistoryStore(settings);
        store.AddOrUpdate(Entry("a"));
        store.Clear();

        Assert.Empty(store.GetAll());
        Assert.Empty(new JsonTransferHistoryStore(settings).GetAll());
    }

    [Fact]
    public void Persists_Across_Instances()
    {
        var settings = new InMemorySettingsStore();
        new JsonTransferHistoryStore(settings).AddOrUpdate(Entry("x"));

        var reloaded = new JsonTransferHistoryStore(settings);
        Assert.Single(reloaded.GetAll());
        Assert.Equal("x", reloaded.GetAll()[0].FileId);
    }
}
