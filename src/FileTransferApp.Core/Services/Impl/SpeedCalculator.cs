using System.Collections.Concurrent;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 基于 1 秒滑动窗口的实时网速计算器。
/// 记录每次进度的 (时间戳, 累计字节)，剔除 1s 前的样本，计算 delta bytes / delta seconds。
/// </summary>
public sealed class SpeedCalculator
{
    private readonly ConcurrentQueue<Sample> _samples = new();
    private readonly TimeSpan _window = TimeSpan.FromSeconds(1);

    public void AddSample(long totalBytesTransferred)
    {
        _samples.Enqueue(new Sample(DateTime.UtcNow.Ticks, totalBytesTransferred));
        Prune(DateTime.UtcNow);
    }

    /// <summary>返回当前网速 (字节/秒)</summary>
    public double GetSpeedBytesPerSecond()
    {
        var now = DateTime.UtcNow;
        Prune(now);
        if (_samples.Count < 2) return 0;

        Sample? oldest = null;
        Sample? newest = null;
        foreach (var s in _samples)
        {
            oldest ??= s;
            newest = s;
        }
        if (oldest is null || newest is null) return 0;

        var deltaBytes = newest.Value.TotalBytes - oldest.Value.TotalBytes;
        var deltaSeconds = (newest.Value.Ticks - oldest.Value.Ticks) / (double)TimeSpan.TicksPerSecond;
        return deltaSeconds <= 0 ? 0 : deltaBytes / deltaSeconds;
    }

    public double GetEtaSeconds(long totalBytes, long transferredBytes)
    {
        var speed = GetSpeedBytesPerSecond();
        if (speed <= 0) return double.PositiveInfinity;
        return (totalBytes - transferredBytes) / speed;
    }

    public void Reset() { while (_samples.TryDequeue(out _)) { } }

    private void Prune(DateTime now)
    {
        var threshold = now - _window;
        while (_samples.TryPeek(out var s) && s.ToDateTime() < threshold)
        {
            _samples.TryDequeue(out _);
        }
    }

    private readonly record struct Sample(long Ticks, long TotalBytes)
    {
        public DateTime ToDateTime() => new(Ticks);
    }
}
