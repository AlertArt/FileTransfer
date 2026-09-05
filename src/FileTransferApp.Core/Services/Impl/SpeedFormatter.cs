namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 字节/秒速度与文件大小格式化器。
/// 统一 TransferItemViewModel.FormatSpeed、BytesToSpeedConverter、DialogTransferApprovalService.FormatSize 的格式契约。
/// 单元测试覆盖各区间边界。
/// </summary>
public static class SpeedFormatter
{
    /// <summary>速度（字节/秒）转可读文本：1.23 MB/s、456.7 KB/s、0 B/s</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond < 0) return "0 B/s";
        return bytesPerSecond switch
        {
            >= 1L << 30 => $"{bytesPerSecond / (1L << 30):0.##} GB/s",
            >= 1L << 20 => $"{bytesPerSecond / (1L << 20):0.##} MB/s",
            >= 1L << 10 => $"{bytesPerSecond / (1L << 10):0.##} KB/s",
            _ => $"{bytesPerSecond:0} B/s"
        };
    }

    /// <summary>字节数转可读大小：1.23 GB、456.7 MB、123 B</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "0 B";
        return bytes switch
        {
            >= 1L << 30 => $"{(double)bytes / (1L << 30):0.##} GB",
            >= 1L << 20 => $"{(double)bytes / (1L << 20):0.##} MB",
            >= 1L << 10 => $"{(double)bytes / (1L << 10):0.##} KB",
            _ => $"{bytes} B"
        };
    }
}
