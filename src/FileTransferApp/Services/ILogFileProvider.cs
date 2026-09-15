using System.Collections.Generic;

namespace FileTransferApp.Services;

/// <summary>日志文件条目：UI 显示名 + 磁盘绝对路径。</summary>
public sealed record LogSource(string Title, string Path);

/// <summary>
/// 提供本平台运行日志（fta.trace.log / crash.log）的文件路径。
/// 桌面日志在 EXE 同级（或 LocalAppData 回退），Android 在应用外部文件目录。
/// </summary>
public interface ILogFileProvider
{
    IReadOnlyList<LogSource> GetLogs();
}