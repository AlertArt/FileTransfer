using System.IO;
using System.Security.Cryptography;
using FileTransferApp.Core.Services.Interfaces;
using Foundation;

namespace FileTransferApp.iOS.Services;

/// <summary>
/// iOS 文件存储服务实现。
/// 应用沙箱内（Documents / Library/Caches）可直接 System.IO 访问；
/// 文件写入 Documents 目录后用户可通过 Files.app 查看，或后续通过 UIDocumentPickerViewController 导出。
/// </summary>
public sealed class IOSStorageService : IStorageService
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
        // NSFileManager.DocumentsDirectory 返回沙箱 Documents 目录，用户可见
        var docs = NSFileManager.DefaultManager.GetUrl(NSSearchPathDirectory.DocumentDirectory,
            NSSearchPathDomain.User, null, false, out _)?.Path;
        return Path.Combine(docs ?? Directory.GetCurrentDirectory(), "Received");
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
