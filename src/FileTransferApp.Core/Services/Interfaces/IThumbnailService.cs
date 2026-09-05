namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 缩略图生成抽象接口。
/// 发送端使用 SkiaSharp / Avalonia 原生 API 提取图片与视频首帧，
/// 压缩为 128x128 WEBP/JPEG，在握手包中打包 Base64 传递。
/// </summary>
public interface IThumbnailService
{
    /// <summary>生成缩略图字节（JPEG/WEBP）。失败或非媒体文件返回 null。</summary>
    Task<byte[]?> GenerateThumbnailAsync(string filePath, int maxWidth = 128, int maxHeight = 128);
}
