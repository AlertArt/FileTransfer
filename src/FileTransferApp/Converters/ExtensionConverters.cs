using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FileTransferApp.Converters;

/// <summary>
/// 按文件扩展名返回不同的柔和底色（无缩略图时的文件头像背景），
/// 以及对应的 emoji 图标 + 扩展名标签，让任务卡片不再千篇一律。
/// </summary>
public sealed class ExtensionToBrushConverter : IValueConverter
{
    public static readonly ExtensionToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ext = GetExt(value).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".tif" or ".tiff"
                => new SolidColorBrush(Color.Parse("#F472B6")),   // Pink 400 —— 图片
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".flv" or ".wmv"
                => new SolidColorBrush(Color.Parse("#8B5CF6")),   // Violet 500 —— 视频
            ".mp3" or ".flac" or ".wav" or ".aac" or ".ogg" or ".m4a"
                => new SolidColorBrush(Color.Parse("#8B5CF6")),   // Purple 500 —— 音乐
            ".pdf"
                => new SolidColorBrush(Color.Parse("#EF4444")),   // Red 500 —— PDF
            ".doc" or ".docx" or ".odt" or ".rtf"
                => new SolidColorBrush(Color.Parse("#3B82F6")),   // Blue 500 —— Word
            ".xls" or ".xlsx" or ".csv" or ".ods"
                => new SolidColorBrush(Color.Parse("#10B981")),   // Emerald 500 —— Excel
            ".ppt" or ".pptx" or ".odp"
                => new SolidColorBrush(Color.Parse("#F59E0B")),   // Amber 500 —— PPT
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".tgz" or ".bz2"
                => new SolidColorBrush(Color.Parse("#EA580C")),   // Orange 700 —— 压缩
            ".exe" or ".msi" or ".apk" or ".ipa" or ".deb" or ".dll"
                => new SolidColorBrush(Color.Parse("#6366F1")),   // Indigo 500 —— 安装包
            ".txt" or ".md" or ".log" or ".json" or ".xml" or ".yml" or ".yaml" or ".ini" or ".cfg"
                => new SolidColorBrush(Color.Parse("#64748B")),   // Slate 500 —— 文本
            ".cs" or ".py" or ".js" or ".ts" or ".java" or ".c" or ".cpp" or ".h" or ".rs" or ".go" or ".kt" or ".swift" or ".rb" or ".php"
                => new SolidColorBrush(Color.Parse("#0EA5E9")),   // Sky 500 —— 代码
            ".htm" or ".html" or ".css"
                => new SolidColorBrush(Color.Parse("#F97316")),   // Orange 500 —— 网页
            _ => new SolidColorBrush(Color.Parse("#94A3B8"))      // Slate 400 —— 默认
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string GetExt(object? value)
    {
        try
        {
            return Path.GetExtension(value?.ToString() ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>扩展名 → 文件类型 emoji 图标</summary>
public sealed class ExtensionToGlyphConverter : IValueConverter
{
    public static readonly ExtensionToGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ext = GetExt(value).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".tif" or ".tiff" => "🖼",
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".flv" or ".wmv" => "🎬",
            ".mp3" or ".flac" or ".wav" or ".aac" or ".ogg" or ".m4a" => "🎵",
            ".pdf" => "📕",
            ".doc" or ".docx" or ".odt" or ".rtf" => "📘",
            ".xls" or ".xlsx" or ".csv" or ".ods" => "📗",
            ".ppt" or ".pptx" or ".odp" => "📙",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".tgz" or ".bz2" => "🗜",
            ".exe" or ".msi" or ".apk" or ".ipa" or ".deb" or ".dll" => "📦",
            ".txt" or ".md" or ".log" => "📝",
            ".json" or ".xml" or ".yml" or ".yaml" or ".ini" or ".cfg" => "⚙",
            ".htm" or ".html" or ".css" => "🌐",
            _ => "📄"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string GetExt(object? value)
    {
        try { return Path.GetExtension(value?.ToString() ?? string.Empty); }
        catch { return string.Empty; }
    }
}

/// <summary>扩展名 → 文件头像底部显示的扩展名小标签（如 "PNG" / "MP4"），最多 4 个字符。</summary>
public sealed class ExtensionToExtLabelConverter : IValueConverter
{
    public static readonly ExtensionToExtLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            var ext = Path.GetExtension(value?.ToString() ?? string.Empty)
                           .TrimStart('.')
                           .ToUpperInvariant();
            if (string.IsNullOrEmpty(ext)) return "FILE";
            return ext.Length <= 5 ? ext : ext[..5];
        }
        catch
        {
            return "FILE";
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
