using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using FileTransferApp.Core.Models;

namespace FileTransferApp.Converters;

/// <summary>
/// 传输状态（TransferState）转状态对应颜色：
/// 完成→绿；失败→红；取消→灰；进行中→蓝；其他→默认前景色。
/// 用于状态文本/图标的颜色化呈现，让用户一眼分辨任务健康度。
/// </summary>
public sealed class StatusToColorConverter : IValueConverter
{
    public static readonly StatusToColorConverter Instance = new();

    // 使用 Avalonia 主题颜色：尽量与系统主题协调
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TransferState state) return Brushes.Black;

        return state switch
        {
            TransferState.Completed => new SolidColorBrush(Color.Parse("#10B981")),  // Emerald 500
            TransferState.Failed => new SolidColorBrush(Color.Parse("#EF4444")),     // Red 500
            TransferState.Cancelled => new SolidColorBrush(Color.Parse("#94A3B8")),  // Slate 400
            TransferState.Transferring => new SolidColorBrush(Color.Parse("#6366F1")), // Brand Indigo 500
            TransferState.Paused => new SolidColorBrush(Color.Parse("#F59E0B")),     // Amber 500
            TransferState.Disconnected => new SolidColorBrush(Color.Parse("#DC2626")),// Red 600
            TransferState.WaitingApproval => new SolidColorBrush(Color.Parse("#8B5CF6")), // Violet 500
            TransferState.Preparing => new SolidColorBrush(Color.Parse("#3B82F6")),  // Blue 500
            TransferState.Created => new SolidColorBrush(Color.Parse("#94A3B8")),
            _ => new SolidColorBrush(Color.Parse("#475569"))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("StatusToColorConverter 不支持反向转换");
}
