using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace FileTransferApp.Services;

/// <summary>
/// 把文本渲染为二维码位图。使用纯托管的 ZXing 编码出 <see cref="BitMatrix"/>，
/// 再手工写入 Avalonia <see cref="WriteableBitmap"/>——不依赖 System.Drawing，跨平台（含 Android）。
/// 失败时返回 null（不抛），由调用方决定回退。
/// </summary>
internal static class QrCodeRenderer
{
    public static Bitmap? Render(string text, int pixelsPerModule = 6, int quietZoneModules = 2)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                { EncodeHintType.MARGIN, quietZoneModules },
                { EncodeHintType.CHARACTER_SET, "UTF-8" },
            };
            // width/height 传 0 → 由 ZXing 返回最小模块矩阵，随后按 pixelsPerModule 放大
            var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints);
            if (matrix is null) return null;

            int modules = matrix.Width;
            int width = modules * pixelsPerModule;
            int height = modules * pixelsPerModule;

            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bitmap.Lock())
            {
                var row = new byte[fb.RowBytes];
                for (int y = 0; y < height; y++)
                {
                    int my = y / pixelsPerModule;
                    for (int x = 0; x < width; x++)
                    {
                        int mx = x / pixelsPerModule;
                        byte v = matrix[mx, my] ? (byte)0x00 : (byte)0xFF; // 黑模块→黑，白→白
                        int o = x * 4;
                        row[o] = v;     // B
                        row[o + 1] = v; // G
                        row[o + 2] = v; // R
                        row[o + 3] = 0xFF;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), row.Length);
                }
            }
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
