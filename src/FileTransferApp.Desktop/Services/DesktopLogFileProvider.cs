using System;
using System.Collections.Generic;
using System.IO;
using FileTransferApp.Services;

namespace FileTransferApp.Desktop.Services;

/// <summary>桌面端日志来源：Trace 监听器写入 EXE 同级 fta.trace.log（不可写时回退到 LocalAppData）。</summary>
public sealed class DesktopLogFileProvider : ILogFileProvider
{
    public IReadOnlyList<LogSource> GetLogs()
    {
        var list = new List<LogSource>();
        try
        {
            var exeDir = Path.Combine(AppContext.BaseDirectory, "fta.trace.log");
            if (File.Exists(exeDir))
                list.Add(new LogSource("fta.trace.log", exeDir));
        }
        catch { /* 单个来源失败不影响整体 */ }

        try
        {
            var localApp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileTransferApp", "fta.trace.log");
            if (File.Exists(localApp) && !list.Exists(x => x.Path == localApp))
                list.Add(new LogSource("fta.trace.log (LocalAppData)", localApp));
        }
        catch { /* ignore */ }

        return list;
    }
}