using System.Diagnostics;
using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 设计文档 1.2 "基于 1 秒滑动窗口算法计算实时网速" 的单元测试。
/// 覆盖：空样本/单样本返回 0、线性样本 delta/dt 计算正确、Prune 旧样本、GetEtaSeconds、Reset、
/// 并发多线程 AddSample 不抛异常、高速率 120MB/s 精度。
/// </summary>
public class SpeedCalculatorTests
{
    // ====== 基础边界 ======

    [Fact]
    public void New_Instance_Returns_Zero()
    {
        var s = new SpeedCalculator();
        Assert.Equal(0, s.GetSpeedBytesPerSecond(), 0);
    }

    [Fact]
    public void Single_Sample_Returns_Zero()
    {
        var s = new SpeedCalculator();
        s.AddSample(1_000_000);
        Assert.Equal(0, s.GetSpeedBytesPerSecond(), 0);
    }

    // ====== 核心：滑动窗口精度 ======

    [Fact]
    public void Two_Samples_Same_Size_Returns_Zero()
    {
        var s = new SpeedCalculator();
        // 用 Stopwatch 控制真实时间戳：两个样本间隔 200ms，总字节差 0
        s.AddSample(0);
        PreciseSleep(200);
        s.AddSample(0);
        Assert.Equal(0, s.GetSpeedBytesPerSecond(), 0);
    }

    [Fact]
    public void Two_Samples_Linear_Speed_Computed_Correctly()
    {
        // 200ms 内推进 1MB，速率应为 1MB / 0.2s = 5MB/s
        var s = new SpeedCalculator();
        var sw = Stopwatch.StartNew();

        s.AddSample(0);
        PreciseSleep(200);
        s.AddSample(1 * 1024 * 1024);

        var speed = s.GetSpeedBytesPerSecond();
        // 1 MB / 0.2s ≈ 5,242,880 B/s；允许 ±15% 计时误差
        var expected = 5d * 1024 * 1024;
        Assert.InRange(speed, expected * 0.85, expected * 1.15);
    }

    [Fact]
    public void Three_Linear_Samples_Reflect_Last_Window()
    {
        // 三样本以 200ms 间隔推进 256KB：每段速度 ≈ 1.25MB/s
        var s = new SpeedCalculator();

        s.AddSample(0);
        PreciseSleep(200);
        s.AddSample(256 * 1024);
        PreciseSleep(200);
        s.AddSample(512 * 1024);

        var speed = s.GetSpeedBytesPerSecond();
        // 512KB / 0.4s ≈ 1,310,720 B/s
        var expected = (512 * 1024) / 0.4;
        Assert.InRange(speed, expected * 0.8, expected * 1.2);
    }

    [Fact]
    public void Eta_Seconds_Computed_From_Current_Speed()
    {
        var s = new SpeedCalculator();
        s.AddSample(0);
        PreciseSleep(200);
        s.AddSample(1_000_000); // ~ 5 MB/s
        PreciseSleep(200);
        s.AddSample(2_000_000); // 保持 5 MB/s

        var eta = s.GetEtaSeconds(totalBytes: 6_000_000, transferredBytes: 2_000_000);
        // 剩余 4MB，速率 5MB/s → ETA ≈ 0.8s；允许 ±50% 计时容错
        Assert.InRange(eta, 0.4, 1.4);
    }

    [Fact]
    public void Eta_Zero_Speed_Returns_PositiveInfinity()
    {
        var s = new SpeedCalculator(); // 无样本 → speed=0
        var eta = s.GetEtaSeconds(totalBytes: 100, transferredBytes: 0);
        Assert.Equal(double.PositiveInfinity, eta);
    }

    // ====== Prune：旧样本超出 1s 窗口被剔除 ======

    [Fact]
    public void Samples_Older_Than_1s_Are_Pruned()
    {
        var s = new SpeedCalculator();
        // 先加入一对 1.2s 前的"旧窗口"样本（产生较低速度）
        s.AddSample(0);
        PreciseSleep(300);
        s.AddSample(100_000);
        // 等待 900ms："0"样本已老于 1s 会被剔除
        PreciseSleep(900);
        // 此时加入第二对 200ms 前的"新窗口"样本
        s.AddSample(1_000_000);
        PreciseSleep(200);
        s.AddSample(2_000_000);

        // 计算时 oldest 应该是 1_000_000（1MB 差 / 0.2s ≈ 5MB/s）
        var speed = s.GetSpeedBytesPerSecond();
        // 1MB / 0.2s = 5,242,880 B/s
        var expected = 5d * 1024 * 1024;
        Assert.InRange(speed, expected * 0.8, expected * 1.2);
    }

    [Fact]
    public void All_Samples_Pruned_Returns_Zero()
    {
        var s = new SpeedCalculator();
        s.AddSample(0);
        PreciseSleep(200);
        s.AddSample(500_000);
        // 等待 1.2s：两个样本都 > 1s
        PreciseSleep(1200);

        Assert.Equal(0, s.GetSpeedBytesPerSecond(), 0);
    }

    // ====== Reset ======

    [Fact]
    public void Reset_Clears_All_Samples()
    {
        var s = new SpeedCalculator();
        s.AddSample(0);
        PreciseSleep(200);
        s.AddSample(1_000_000);
        Assert.True(s.GetSpeedBytesPerSecond() > 0);

        s.Reset();

        Assert.Equal(0, s.GetSpeedBytesPerSecond(), 0);
    }

    // ====== 高速率精度 ======

    [Fact]
    public void High_Speed_120MBps_Not_Observed_As_Overflow()
    {
        // 100ms 内推进 12MB ≈ 120 MB/s；不溢出 long/double
        var s = new SpeedCalculator();
        s.AddSample(0);
        PreciseSleep(100);
        s.AddSample(12 * 1024 * 1024);

        var speed = s.GetSpeedBytesPerSecond();
        Assert.True(speed > 0);
        Assert.False(double.IsInfinity(speed));
        Assert.False(double.IsNaN(speed));
    }

    // ====== 并发 ======

    [Fact]
    public void Concurrent_AddSample_From_Multiple_Threads_Does_Not_Throw()
    {
        var s = new SpeedCalculator();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        var ex = Record.Exception(() =>
            Parallel.For(0, 2000, options, i => s.AddSample(i)));
        Assert.Null(ex);
    }

    // ---- 辅助：用 SpinWait 精确等待，避免 Thread.Sleep 低分辨率 ----
    private static void PreciseSleep(int ms)
    {
        var sw = Stopwatch.StartNew();
        var target = TimeSpan.FromMilliseconds(ms);
        while (sw.Elapsed < target)
        {
            if (sw.Elapsed < target - TimeSpan.FromMilliseconds(5))
                Thread.Sleep(1);
            else
                Thread.SpinWait(100);
        }
    }
}
