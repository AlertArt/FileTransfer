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

    public Task<byte[]?> GenerateThumbnailAsync(string filePath, int maxWidth = 128, int maxHeight = 128)
    {
        byte[]? result = null;
        var ext = Path.GetExtension(filePath);
        if (!ImageExtensions.Contains(ext))
            return Task.FromResult<byte[]?>(null);

        try
        {
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
