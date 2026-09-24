using System.Diagnostics;
using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>带宽节流：不限速立即返回；限速时按速率等待。</summary>
public class BandwidthThrottleTests
{
    [Fact]
    public async Task Unlimited_Does_Not_Wait()
    {
        var throttle = new BandwidthThrottle(0);
        var sw = Stopwatch.StartNew();
        await throttle.AcquireAsync(10_000_000);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"elapsed={sw.ElapsedMilliseconds}");
    }

    [Fact]
    public async Task Limited_Waits_Roughly_To_Maintain_Rate()
    {
        var throttle = new BandwidthThrottle(100_000); // 100 KB/s
        var sw = Stopwatch.StartNew();
        await throttle.AcquireAsync(50_000); // 空桶下发 50KB 约需 500ms
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 300, $"elapsed={sw.ElapsedMilliseconds}");
    }

    [Fact]
    public async Task Changing_Limit_To_Zero_Stops_Throttling()
    {
        var throttle = new BandwidthThrottle(100_000);
        throttle.LimitBytesPerSecond = 0;
        var sw = Stopwatch.StartNew();
        await throttle.AcquireAsync(10_000_000);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"elapsed={sw.ElapsedMilliseconds}");
    }
}
