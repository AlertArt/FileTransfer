using System.IO;
using System.Security.Cryptography;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Desktop.Services;

/// <summary>
/// Windows 桌面端文件存储实现。
/// 直接使用 System.IO.File 访问；接收端写入 finalPath+".tmp" 临时文件，
/// 完成后原子重命名为最终文件，取消时删除临时文件。
/// </summary>
public sealed class DesktopStorageService : IStorageService
{
    private const int BufferSize = 64 * 1024;

    public Task<Stream> OpenReadStreamAsync(string fileIdentifier)
        => Task.FromResult<Stream>(new FileStream(fileIdentifier, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true));

    public Task<(Stream Stream, string FinalPath)> OpenWriteStreamAsync(string fileName, long totalSize)
    {
        var finalPath = Path.Combine(GetDefaultReceiveDirectory(), Sanitize(fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var tempPath = TempPathFor(finalPath);
        var fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true);
        if (fs.Length != totalSize) fs.SetLength(totalSize);
        return Task.FromResult<(Stream, string)>((fs, finalPath));
    }

    public async Task FinalizeWriteAsync(string finalPath)
    {
        var tempPath = TempPathFor(finalPath);
        // 临时文件已由上层关闭，直接移动
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

    public string GetDefaultReceiveDirectory()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dir = Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(dir, "FileTransferApp");
    }

    /// <summary>启动清理：删除上次会话遗留的 .tmp 临时文件（崩溃/强杀后残留，跨重启清理）。</summary>
    public void CleanupStaleTempFiles()
    {
        try
        {
            var dir = GetDefaultReceiveDirectory();
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
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
