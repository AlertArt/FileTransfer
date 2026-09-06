using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 设计文档 7.1 断点续传 Bitmap 扩展测试。
/// 覆盖：大规模切片 (100K) 内存与完整性、乱序 MarkReceived、ComputeMissing 边界、
/// Received/Missing 排序、并发 MarkReceived 不抛异常。
/// </summary>
public class ChunkBitmapAdvancedTests
{
    // ---- 规模：大文件 10W 切片 ----

    [Fact]
    public void Large_100K_Chunks_MarkAll_And_IsComplete()
    {
        const int n = 100_000; // 5GB 文件 / 2MB chunks = 2560 chunks；100K 是压力上限
        var bm = new ChunkBitmap(n);

        for (int i = 0; i < n; i++) bm.MarkReceived(i);

        Assert.True(bm.IsComplete);
        Assert.Equal(n, bm.Received.Length);
        Assert.Empty(bm.Missing);
    }

    [Fact]
    public void Large_100K_Chunks_Missing_Exactly_Even_Indices()
    {
        const int n = 10_000;
        var bm = new ChunkBitmap(n);
        for (int i = 0; i < n; i += 2) bm.MarkReceived(i);

        Assert.Equal(n / 2, bm.Missing.Length);
        Assert.All(bm.Missing, m => Assert.True(m % 2 == 1));
        // 缺失从首到尾都是奇数位
        for (int i = 0; i < bm.Missing.Length; i++)
            Assert.Equal(2 * i + 1, bm.Missing[i]);
    }

    [Fact]
    public void Large_100K_Chunks_ComputeMissing_With_Received_Peer()
    {
        // 模拟对端已收到偶数索引，ComputeMissing 应该返回奇数索引
        const int n = 20_000;
        var peer = Enumerable.Range(0, n).Where(i => i % 2 == 0).ToArray();
        var missing = ChunkBitmap.ComputeMissing(n, peer);

        Assert.Equal(n / 2, missing.Length);
        for (int i = 0; i < missing.Length; i++)
            Assert.Equal(2 * i + 1, missing[i]);
    }

    // ---- 乱序 MarkReceived ----

    [Fact]
    public void Out_Of_Order_MarkReceived_Produces_Sorted_Received()
    {
        var bm = new ChunkBitmap(10);
        var order = new[] { 7, 2, 9, 0, 4, 1, 5, 3, 8, 6 };
        foreach (var i in order) bm.MarkReceived(i);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, bm.Received);
    }

    [Fact]
    public void Out_Of_Order_MarkReceived_Partial_Missing_Is_Ordered()
    {
        var bm = new ChunkBitmap(10);
        var order = new[] { 7, 2, 0, 6, 8 };
        foreach (var i in order) bm.MarkReceived(i);

        Assert.Equal(new[] { 1, 3, 4, 5, 9 }, bm.Missing);
    }

    // ---- ComputeMissing 边界 ----

    [Fact]
    public void ComputeMissing_Zero_Chunks_Returns_Empty()
    {
        var missing = ChunkBitmap.ComputeMissing(0, new[] { 0, 1 });
        Assert.Empty(missing);
    }

    [Fact]
    public void ComputeMissing_Total_0_Peer_Empty()
    {
        var missing = ChunkBitmap.ComputeMissing(0, Array.Empty<int>());
        Assert.Empty(missing);
    }

    [Fact]
    public void ComputeMissing_Peer_Has_Out_Of_Bounds_Indices_They_Are_Ignored()
    {
        // peer 回传了超出 total 的索引（协议错误或旧版本）→ 被 HashSet 吞，不应影响结果
        var missing = ChunkBitmap.ComputeMissing(5, new[] { 0, 2, 100, -1, 9999 });
        Assert.Equal(new[] { 1, 3, 4 }, missing);
    }

    [Fact]
    public void ComputeMissing_Duplicate_Peer_Received_Deduplicates()
    {
        var missing = ChunkBitmap.ComputeMissing(5, new[] { 0, 0, 1, 1, 1, 2 });
        Assert.Equal(new[] { 3, 4 }, missing);
    }

    // ---- 并发 ----

    [Fact]
    public void Concurrent_MarkReceived_Does_Not_Throw()
    {
        const int n = 5000;
        var bm = new ChunkBitmap(n);
        var rng = new Random(42);
        var indices = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).ToArray();

        var ex = Record.Exception(() =>
            Parallel.For(0, n, i => bm.MarkReceived(indices[i])));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Concurrent_Missing_Does_Not_Throw()
    {
        const int n = 2000;
        var bm = new ChunkBitmap(n);
        for (int i = 0; i < n; i++) bm.MarkReceived(i);

        int[] r = null!, m = null!;
        var t1 = Task.Run(() => r = bm.Received);
        var t2 = Task.Run(() => m = bm.Missing);
        await Task.WhenAll(t1, t2);

        Assert.Equal(n, r.Length);
        Assert.Empty(m);
    }

    // ---- 空/边界 ----

    [Fact]
    public void Total_Zero_IsComplete_Never_True()
    {
        var bm = new ChunkBitmap(0);
        bm.MarkReceived(0);
        Assert.False(bm.IsComplete);
        Assert.Empty(bm.Received);
        Assert.Empty(bm.Missing);
    }

    [Fact]
    public void MarkReceived_Out_Of_Range_Does_Not_Increase_Count()
    {
        var bm = new ChunkBitmap(5);
        bm.MarkReceived(-1);
        bm.MarkReceived(5);
        bm.MarkReceived(int.MaxValue);
        Assert.Empty(bm.Received);
        Assert.Equal(5, bm.Missing.Length);
    }

    // ---- 设计文档 7.1 第二点：模拟缺失 [1,3,5] 块，ComputeMissing 精确返回 ----

    [Fact]
    public void Scenario_Missing_1_3_5_Returns_Exactly()
    {
        // 设计文档 7.1：模拟接收端缺失 [1,3,5] 块，验证 ComputeMissing
        var peer = Enumerable.Range(0, 10).Where(i => i != 1 && i != 3 && i != 5).ToArray();
        var missing = ChunkBitmap.ComputeMissing(10, peer);
        Assert.Equal(new[] { 1, 3, 5 }, missing);
    }
}
