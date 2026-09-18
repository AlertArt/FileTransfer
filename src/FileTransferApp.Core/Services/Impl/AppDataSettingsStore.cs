using System.IO;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 平台默认设置存储：键 → 应用私有数据目录中的一个 JSON 文件。
/// 目录取 LocalApplicationData（桌面 %LOCALAPPDATA%\FileTransferApp；
/// Android 映射到应用私有数据目录 /data/data/pkg；iOS 沙箱 Library）。与现有语言设置文件同目录。
/// </summary>
public sealed class AppDataSettingsStore : ISettingsStore
{
    public const string SettingsDirName = "FileTransferApp";
    private const string Extension = ".json";

    private readonly string _dir;

    public AppDataSettingsStore()
        => _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsDirName);

    internal AppDataSettingsStore(string dir) => _dir = dir;

    private string GetPath(string key)
        => Path.Combine(_dir, key.Trim().Trim('/').Replace("/", "__") + Extension);

    public string? ReadRaw(string key)
    {
        try
        {
            var path = GetPath(key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            // 读取失败（损坏/权限）按不存在处理，上层重新生成
            return null;
        }
    }

    public void WriteRaw(string key, string json)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(GetPath(key), json);
        }
        catch
        {
            // 持久化失败（沙箱只读等）不影响本次运行
        }
    }

    public void Remove(string key)
    {
        try
        {
            var path = GetPath(key);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* ignore */ }
    }
}