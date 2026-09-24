using System.IO;
using System.Security.Cryptography;
using Android.Content;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Android.Services;

/// <summary>
/// Android 文件存储服务实现。
/// 策略：
/// 1) 优先写入公共 /sdcard/Download 目录（需 MANAGE_EXTERNAL_STORAGE 或 legacy storage）；
/// 2) 若公共目录无写权限，自动降级到应用私有外存 GetExternalFilesDir（无需任何权限），
///    路径形如 /sdcard/Android/data/com.CompanyName.FileTransferApp/files/Download/，
///    用户可通过文件管理器 → Android → data → 包名 → files → Download 找到文件。
/// </summary>
public sealed class AndroidStorageService : IStorageService
{
    private const int BufferSize = 64 * 1024;
    private readonly Context _context;

    public AndroidStorageService(Context context) => _context = context;

    public Task<Stream> OpenReadStreamAsync(string fileIdentifier)
        => Task.FromResult<Stream>(new FileStream(fileIdentifier, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true));

    public Task<(Stream Stream, string FinalPath)> OpenWriteStreamAsync(string fileName, long totalSize)
    {
        var safeName = Sanitize(fileName);
        var dir = GetDefaultReceiveDirectory();
        var finalPath = Path.Combine(dir, safeName);

        // 确保目录存在
        Directory.CreateDirectory(dir);

        var tempPath = TempPathFor(finalPath);
        FileStream fs;
        try
        {
            fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true);
            if (fs.Length != totalSize) fs.SetLength(totalSize);
        }
        catch (UnauthorizedAccessException)
        {
            // 公共 Download 目录无写权限 → 降级到应用私有外存
            var fallbackDir = GetFallbackReceiveDirectory();
            Directory.CreateDirectory(fallbackDir);
            finalPath = Path.Combine(fallbackDir, safeName);
            tempPath = TempPathFor(finalPath);
            fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true);
            if (fs.Length != totalSize) fs.SetLength(totalSize);
        }
        return Task.FromResult<(Stream, string)>((fs, finalPath));
    }

    public async Task FinalizeWriteAsync(string finalPath)
    {
        var tempPath = TempPathFor(finalPath);
        if (File.Exists(tempPath))
            await Task.Run(() => File.Move(tempPath, finalPath, overwrite: true)).ConfigureAwait(false);
    }

    public Task CancelWriteAsync(string finalPath)
    {
        var tempPath = TempPathFor(finalPath);
        return Task.Run(() =>
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* ignore */ }
        });
    }

    /// <summary>启动清理：删除上次会话遗留的 .tmp 临时文件（崩溃/强杀后残留，跨重启清理）。</summary>
    public void CleanupStaleTempFiles()
    {
        foreach (var dir in new[] { GetDefaultReceiveDirectory(), GetFallbackReceiveDirectory() })
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(f); } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    public string GetDefaultReceiveDirectory()
    {
        // 公共 Download 目录：用户可在文件管理器中直接查看接收到的文件
        // Android 10 需 requestLegacyExternalStorage + WRITE_EXTERNAL_STORAGE；
        // Android 11+ 需 MANAGE_EXTERNAL_STORAGE 或 permissive SELinux（模拟器默认放行）。
        var publicDownloads = global::Android.OS.Environment.GetExternalStoragePublicDirectory(
            global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath;
        return publicDownloads ?? "/sdcard/Download";
    }

    /// <summary>
    /// 降级目录：应用私有外存，无需任何权限即可 System.IO 直接读写。
    /// 路径形如 /sdcard/Android/data/com.CompanyName.FileTransferApp/files/Download/
    /// 用户可通过文件管理器 → Android → data → 包名 → files → Download 访问。
    /// </summary>
    private string GetFallbackReceiveDirectory()
    {
        var extFiles = _context.GetExternalFilesDir(null)?.AbsolutePath;
        if (string.IsNullOrEmpty(extFiles))
            return Path.Combine(_context.FilesDir?.AbsolutePath ?? "/data/local/tmp", "Download");
        return Path.Combine(extFiles, "Download");
    }

    public bool FileExists(string path) => File.Exists(path);

    public async Task<string> ComputeSha256Async(string filePath)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await sha.ComputeHashAsync(fs).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public long GetFileSize(string filePath) => new FileInfo(filePath).Length;

    private static string TempPathFor(string finalPath) => finalPath + ".tmp";

    private static string Sanitize(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in fileName) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
