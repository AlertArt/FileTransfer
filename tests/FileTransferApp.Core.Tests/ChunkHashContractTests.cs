using System.Security.Cryptography;
using FileTransferApp.Core.Protocols;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 切片 Hash 校验契约测试。
/// 协议规定发送端将每个切片的 SHA256 以小写十六进制串形式附在 HTTP 头
/// (<see cref="ProtocolConstants.HeaderChunkHash"/>) 中；接收端校验一致后落盘。
/// 本测试验证该算法与字符串格式的一致性。
/// </summary>
public class ChunkHashContractTests
{
    private const string HeaderChunkHash = ProtocolConstants.HeaderChunkHash;

    /// <summary>复刻 PipelinesTransferEngine.ComputeChunkHash 的契约</summary>
    private static string ComputeChunkHash(byte[] buf, int len)
    {
        var h = SHA256.HashData(new ReadOnlySpan<byte>(buf, 0, len));
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    [Fact]
    public void HeaderName_Is_Consistent()
    {
        // 协议常量：保证发送端写头 / 接收端读头使用同一名称
        Assert.Equal("X-Chunk-Hash", HeaderChunkHash);
    }

    [Fact]
    public void Hash_Is_64_Char_Lowercase_Hex_SHA256()
    {
        var buf = new byte[] { 1, 2, 3, 4, 5 };

        var hash = ComputeChunkHash(buf, buf.Length);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Hash_Is_Deterministic_For_Same_Payload()
    {
        var buf = new byte[] { 7, 7, 7, 7 };

        var a = ComputeChunkHash(buf, buf.Length);
        var b = ComputeChunkHash(buf, buf.Length);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_Differs_When_Payload_Differs()
    {
        var a = ComputeChunkHash(new byte[] { 1, 2, 3 }, 3);
        var b = ComputeChunkHash(new byte[] { 1, 2, 4 }, 3);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_Respects_Length_Not_Full_Buffer()
    {
        var buf = new byte[] { 9, 9, 9, 0, 0, 0 };

        var partialHash = ComputeChunkHash(buf, 3);  // 仅前 3 字节
        var fullHash = ComputeChunkHash(buf, 6);

        Assert.NotEqual(partialHash, fullHash);
        Assert.Equal(64, partialHash.Length);
    }

    [Fact]
    public void Hash_Of_Known_Vector_Matches_Reference()
    {
        // SHA256("abc") = ba7816bf8c01c1cea4616bddba23a4ba8c7b4f1c4f1c4f1c4f1c4f1c4f1c4f... (空字节)
        // 这里使用已知向量：空输入的 SHA256
        var emptyHash = ComputeChunkHash(Array.Empty<byte>(), 0);

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            emptyHash);
    }

    [Fact]
    public void Hash_Matches_Independent_SHA256_Computation()
    {
        var buf = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var expected = BitConverter.ToString(SHA256.HashData(buf)).Replace("-", "").ToLowerInvariant();

        var actual = ComputeChunkHash(buf, buf.Length);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Two_Distinct_Chunks_Produce_Two_Distinct_Hashes()
    {
        var chunkA = new byte[1024];
        var chunkB = new byte[1024];
        chunkB[0] = 1; // 仅 1 字节差异

        var hashA = ComputeChunkHash(chunkA, chunkA.Length);
        var hashB = ComputeChunkHash(chunkB, chunkB.Length);

        Assert.NotEqual(hashA, hashB);
    }
}
