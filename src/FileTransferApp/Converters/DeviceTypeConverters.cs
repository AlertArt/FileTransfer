using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FileTransferApp.Core.Models;

namespace FileTransferApp.Converters;

/// <summary>设备类型 → 设备头像背景色：
/// Windows = 蓝系，Android = 绿系，其它按语义匹配柔和现代色。
/// 不使用 System 主题色，保证与 Design Tokens 一致。
/// </summary>
public sealed class DeviceTypeToBrushConverter : IValueConverter
{
    public static readonly DeviceTypeToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = value is DeviceType dt ? dt : DeviceType.Unknown;
        return type switch
        {
            DeviceType.Windows => new SolidColorBrush(Color.Parse("#3B82F6")),   // Blue 500
            DeviceType.Android => new SolidColorBrush(Color.Parse("#10B981")),   // Emerald 500
            DeviceType.macOS => new SolidColorBrush(Color.Parse("#475569")),     // Slate 600
            DeviceType.Linux => new SolidColorBrush(Color.Parse("#F59E0B")),     // Amber 500
            DeviceType.iOS => new SolidColorBrush(Color.Parse("#0EA5E9")),       // Sky 500
            _ => new SolidColorBrush(Color.Parse("#94A3B8"))                     // Slate 400 (Unknown)
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>设备类型 → 头像里显示的字形 (emoji)，形成「类型头像」观感。</summary>
public sealed class DeviceTypeToGlyphConverter : IValueConverter
{
    public static readonly DeviceTypeToGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = value is DeviceType dt ? dt : DeviceType.Unknown;
        return type switch
        {
            DeviceType.Windows => "🪟",
            DeviceType.Android => "📱",
            DeviceType.macOS => "🍎",
            DeviceType.Linux => "🐧",
            DeviceType.iOS => "📲",
            _ => "💻"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
