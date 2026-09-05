using System.Globalization;
using Avalonia.Data.Converters;
using FileTransferApp.Core.Services.Impl;

namespace FileTransferApp.Converters;

/// <summary>
/// 速度（bytes/s，double）转可读文本：1.23 MB/s、456 KB/s、0 B/s。
/// 用于 UI 直接绑定原始速度数值时的格式化呈现。
/// </summary>
public sealed class BytesToSpeedConverter : IValueConverter
{
    public static readonly BytesToSpeedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double bytesPerSecond) return SpeedFormatter.FormatSpeed(0);
        return SpeedFormatter.FormatSpeed(bytesPerSecond);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("BytesToSpeedConverter 不支持反向转换");
}
