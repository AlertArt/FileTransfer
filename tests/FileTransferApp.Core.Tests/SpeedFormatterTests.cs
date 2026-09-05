using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 速度与大小格式化器单元测试。
/// 覆盖各区间边界、负值、零值、NaN 与跨数量级字符串格式契约。
/// </summary>
public class SpeedFormatterTests
{
    private const long KB = 1L << 10;
    private const long MB = 1L << 20;
    private const long GB = 1L << 30;

    // -------- FormatSpeed --------

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(500, "500 B/s")]
    [InlineData(1023, "1023 B/s")]
    public void FormatSpeed_Bytes_Level(double v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSpeed(v));

    [Theory]
    [InlineData(1024, "1 KB/s")]        // 1 KB 边界
    [InlineData(1536, "1.5 KB/s")]
    [InlineData(10240, "10 KB/s")]
    [InlineData(1023.99, "1024 B/s")]    // 边界下一档
    public void FormatSpeed_KB_Level(double v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSpeed(v));

    [Theory]
    [InlineData(1048576, "1 MB/s")]     // 1 MB 边界
    [InlineData(1572864, "1.5 MB/s")]
    [InlineData(5242880, "5 MB/s")]
    public void FormatSpeed_MB_Level(double v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSpeed(v));

    [Theory]
    [InlineData(1073741824, "1 GB/s")]
    [InlineData(2147483648, "2 GB/s")]
    public void FormatSpeed_GB_Level(double v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSpeed(v));

    [Theory]
    [InlineData(-1, "0 B/s")]          // 负值
    [InlineData(-1024, "0 B/s")]
    public void FormatSpeed_Negative_Returns_Zero(double v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSpeed(v));

    [Fact]
    public void FormatSpeed_NaN_Returns_Zero()
        => Assert.Equal("0 B/s", SpeedFormatter.FormatSpeed(double.NaN));

    [Fact]
    public void FormatSpeed_Large_GB_Does_Not_Overflow()
    {
        // 1 TB/s 不应溢出 long 边界
        var result = SpeedFormatter.FormatSpeed(1L << 40);
        Assert.EndsWith("GB/s", result);
    }

    // -------- FormatSize --------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(500, "500 B")]
    public void FormatSize_Bytes_Level(long v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSize(v));

    [Theory]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10240, "10 KB")]
    public void FormatSize_KB_Level(long v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSize(v));

    [Theory]
    [InlineData(1048576, "1 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(5 * MB, "5 MB")]
    public void FormatSize_MB_Level(long v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSize(v));

    [Theory]
    [InlineData(GB, "1 GB")]
    [InlineData(2 * GB, "2 GB")]
    [InlineData(1024L * GB, "1024 GB")]  // 1 TB 仍以 GB 显示
    public void FormatSize_GB_Level(long v, string expected)
        => Assert.Equal(expected, SpeedFormatter.FormatSize(v));

    [Fact]
    public void FormatSize_Negative_Returns_Zero()
        => Assert.Equal("0 B", SpeedFormatter.FormatSize(-1));

    // -------- 跨数量级一致性 --------

    [Fact]
    public void FormatSpeed_And_FormatSize_Are_Consistent_Across_Ranges()
    {
        // 同一数值在 FormatSpeed 应带 /s 后缀，FormatSize 不带
        foreach (var v in new long[] { 0, 1023, 1024, 1048575, 1048576 })
        {
            var speed = SpeedFormatter.FormatSpeed(v);
            var size = SpeedFormatter.FormatSize(v);
            Assert.Equal(size + "/s", speed);
        }
    }
}
