using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Services;

/// <summary>
/// 基于 Avalonia 内置 Skia 图像处理的跨平台缩略图服务。
/// 提取图片缩略图压缩为 128x128，在握手包中以 Base64 传递实现接收前预览。
/// 视频首帧需平台原生 API（本实现暂对非图片返回 null）。
/// </summary>
public sealed class AvaloniaThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".ico"
    };

    /// <summary>
    /// 超过该大小跳过缩略图：超大图片（数百 MB 全景/扫描件）会整幅解码，
    /// 造成数秒级阻塞与数 GB 内存峰值，预览价值远低于代价。
    /// </summary>
    private const long MaxThumbnailFileSizeBytes = 64L * 1024 * 1024;

    public Task<byte[]?> GenerateThumbnailAsync(string filePath, int maxWidth = 128, int maxHeight = 128)
    {
        byte[]? result = null;
        var ext = Path.GetExtension(filePath);
        if (!ImageExtensions.Contains(ext))
            return Task.FromResult<byte[]?>(null);

        try
        {
            // 大文件直接跳过（见 MaxThumbnailFileSizeBytes 注释）
            if (!File.Exists(filePath) || new FileInfo(filePath).Length > MaxThumbnailFileSizeBytes)
                return Task.FromResult<byte[]?>(null);

            using var src = new Bitmap(filePath);
            // 等比缩放，保持宽高比
            double scale = Math.Min(
                (double)maxWidth / src.PixelSize.Width,
                (double)maxHeight / src.PixelSize.Height);
            scale = scale > 1 ? 1 : scale; // 不放大
            var target = new PixelSize(
                Math.Max(1, (int)(src.PixelSize.Width * scale)),
                Math.Max(1, (int)(src.PixelSize.Height * scale)));

            using var scaled = src.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
            using var ms = new MemoryStream();
            // Avalonia 12: 旧 Save(Stream, int?) 已过时，须显式指定编码选项
            scaled.Save(ms, JpegBitmapEncoderOptions.Default);
            result = ms.ToArray();
        }
        catch
        {
            // 非支持的图片格式或解码失败 → 返回 null
            result = null;
        }
        return Task.FromResult(result);
    }
}
