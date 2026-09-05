using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 断点续传切片 Bitmap 单元测试。
/// 覆盖 MarkReceived / IsComplete / Received / Missing / ComputeMissing 路径。
/// </summary>
public class ChunkBitmapTests
{
    [Fact]
    public void New_Bitmap_Has_No_Received_And_All_Missing()
    {
        var bmp = new ChunkBitmap(totalChunks: 5);

        Assert.False(bmp.IsComplete);
        Assert.Empty(bmp.Received);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, bmp.Missing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void MarkReceived_Adds_Index_And_Removes_From_Missing(int idx)
    {
        var bmp = new ChunkBitmap(totalChunks: 5);

        bmp.MarkReceived(idx);

        Assert.Contains(idx, bmp.Received);
        Assert.DoesNotContain(idx, bmp.Missing);
        Assert.False(bmp.IsComplete);
    }

    [Fact]
    public void MarkReceived_Ignores_Out_Of_Range_Indices()
    {
        var bmp = new ChunkBitmap(totalChunks: 3);

        bmp.MarkReceived(-1);
        bmp.MarkReceived(3);
        bmp.MarkReceived(100);

        Assert.Empty(bmp.Received);
        Assert.Equal(new[] { 0, 1, 2 }, bmp.Missing);
    }

    [Fact]
    public void MarkReceived_Is_Idempotent_For_Duplicate_Index()
    {
        var bmp = new ChunkBitmap(totalChunks: 3);

        bmp.MarkReceived(1);
        bmp.MarkReceived(1);
        bmp.MarkReceived(1);

        Assert.Single(bmp.Received);
        Assert.Equal(new[] { 1 }, bmp.Received);
    }

    [Fact]
    public void IsComplete_True_Only_When_All_Chunks_Received()
    {
        var bmp = new ChunkBitmap(totalChunks: 4);

        bmp.MarkReceived(0);
        bmp.MarkReceived(1);
        bmp.MarkReceived(2);
        Assert.False(bmp.IsComplete);

        bmp.MarkReceived(3);
        Assert.True(bmp.IsComplete);
        Assert.Empty(bmp.Missing);
    }

    [Fact]
    public void New_Bitmap_With_Zero_Chunks_Never_Completes()
    {
        var bmp = new ChunkBitmap(totalChunks: 0);

        Assert.False(bmp.IsComplete);
        Assert.Empty(bmp.Missing);
    }

    [Fact]
    public void Received_Returns_Sorted_Array()
    {
        var bmp = new ChunkBitmap(totalChunks: 5);

        bmp.MarkReceived(4);
        bmp.MarkReceived(1);
        bmp.MarkReceived(3);
        bmp.MarkReceived(0);

        Assert.Equal(new[] { 0, 1, 3, 4 }, bmp.Received);
    }

    [Fact]
    public void Missing_Returns_Sorted_Array()
    {
        var bmp = new ChunkBitmap(totalChunks: 5);

        bmp.MarkReceived(2);
        bmp.MarkReceived(4);

        Assert.Equal(new[] { 0, 1, 3 }, bmp.Missing);
    }

    // -------- 静态 ComputeMissing：发送端依据对端 Bitmap 求差集 --------

    [Fact]
    public void ComputeMissing_Returns_Difference_Between_Total_And_Peer_Received()
    {
        var peerReceived = new[] { 0, 2, 4 };

        var missing = ChunkBitmap.ComputeMissing(totalChunks: 6, peerReceived);

        Assert.Equal(new[] { 1, 3, 5 }, missing);
    }

    [Fact]
    public void ComputeMissing_Returns_All_When_Peer_Has_Nothing()
    {
        var missing = ChunkBitmap.ComputeMissing(totalChunks: 4, Array.Empty<int>());

        Assert.Equal(new[] { 0, 1, 2, 3 }, missing);
    }

    [Fact]
    public void ComputeMissing_Returns_Empty_When_Peer_Has_Everything()
    {
        var missing = ChunkBitmap.ComputeMissing(totalChunks: 4, new[] { 0, 1, 2, 3 });

        Assert.Empty(missing);
    }

    [Fact]
    public void ComputeMissing_Deduplicates_Peer_Received()
    {
        var missing = ChunkBitmap.ComputeMissing(totalChunks: 3, new[] { 1, 1, 1 });

        Assert.Equal(new[] { 0, 2 }, missing);
    }
}
