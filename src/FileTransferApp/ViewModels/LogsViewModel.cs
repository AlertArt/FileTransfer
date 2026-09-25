using System.Collections.Generic;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileTransferApp.Core.Diagnostics;
using FileTransferApp.Services;

namespace FileTransferApp.ViewModels;

/// <summary>
/// 日志查看页视图模型：聚合平台日志文件内容（取文件尾部，避免超大文件卡死 UI）。
/// 供「日志」页面绑定；文件路径信息单独展示，便于用户按路径用文件管理器取日志。
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    /// <summary>单文件最多读取的字节数（超过取尾部，仅保留末尾）。</summary>
    private const long MaxReadBytesPerFile = 1_500_000;

    /// <summary>最多合并展示的日志文件数，防止外星文件把页面拖垮。</summary>
    private const int MaxFilesShown = 4;

    private readonly ILogFileProvider _provider;
    private readonly ILocalizationService _loc;

    [ObservableProperty] public partial string LogText { get; set; } = string.Empty;

    [ObservableProperty] public partial string SourceInfo { get; set; } = string.Empty;

    [ObservableProperty] public partial bool HasLogs { get; set; }

    /// <summary>
    /// 详细(Verbose)日志开关：默认关闭，避免大文件传输的"每切片/每请求"日志把文件撑大。
    /// 打开后立即生效并持久化（重启仍生效），用于排查设备断联等疑难问题。
    /// </summary>
    [ObservableProperty] public partial bool VerboseEnabled { get; set; }

    partial void OnVerboseEnabledChanged(bool value) => FtaTrace.SetVerbose(value);

    public LogsViewModel(ILogFileProvider provider, ILocalizationService localization)
    {
        _provider = provider;
        _loc = localization;
        VerboseEnabled = FtaTrace.IsVerboseEnabled; // 同步当前状态
    }

    [RelayCommand]
    private void Refresh() => Load();

    /// <summary>打开页面时调用（也可由「刷新」按钮触发）。UI 线程调用。</summary>
    public void Load()
    {
        // 内存尾缓冲兜底：即使磁盘日志缺失/未落盘，日志页也总有内容（如 Android 外部目录不可用）。
        var memory = FtaTrace.Snapshot();
        var logs = _provider.GetLogs();
        if (logs.Count == 0 && string.IsNullOrWhiteSpace(memory))
        {
            LogText = _loc.GetString("LogsEmpty");
            SourceInfo = string.Empty;
            HasLogs = false;
            return;
        }

        HasLogs = true;
        var body = new StringBuilder();
        var info = new StringBuilder();

        long totalBytes = 0;
        if (!string.IsNullOrWhiteSpace(memory))
        {
            body.Append("== runtime (内存, 最近日志) ==").AppendLine();
            body.Append(memory);
            body.AppendLine();
            totalBytes += memory.Length;
        }

        var shown = 0;
        foreach (var log in logs)
        {
            if (shown >= MaxFilesShown) break;
            try
            {
                if (!File.Exists(log.Path)) continue;

                var fi = new FileInfo(log.Path);
                var length = fi.Length;
                using var fs = File.Open(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (length > MaxReadBytesPerFile)
                    fs.Seek(length - MaxReadBytesPerFile, SeekOrigin.Begin);

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                if (length > MaxReadBytesPerFile)
                {
                    // 跳过可能被截断的半行
                    reader.ReadLine();
                }

                var content = reader.ReadToEnd();
                if (length > MaxReadBytesPerFile) content = content.TrimStart('\r', '\n');

                if (body.Length > 0) body.AppendLine();
                body.Append("== ").Append(log.Title).Append(" ==").AppendLine();
                body.AppendLine(content);

                if (info.Length > 0) info.AppendLine();
                info.Append(log.Title).Append(": ").Append(log.Path)
                    .AppendLine($" · {length / 1024d:0.#} KB");
                totalBytes += length;
                shown++;
            }
            catch
            {
                // 单个文件读取失败（占用/权限）不影响其它
            }
        }

        if (totalBytes == 0)
        {
            LogText = _loc.GetString("LogsEmpty");
            SourceInfo = string.Empty;
            HasLogs = false;
            return;
        }

        LogText = body.ToString();
        SourceInfo = info.ToString();
    }
}