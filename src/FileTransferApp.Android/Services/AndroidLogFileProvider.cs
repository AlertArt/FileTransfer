using System;
using System.Collections.Generic;
using System.IO;
using FileTransferApp.Services;

namespace FileTransferApp.Android.Services;

/// <summary>
/// Android 端日志来源：
/// fta.trace.log 位于 GetExternalFilesDir(null)/fta.trace.log（与应用复制到公共 Download 的同一文件），
/// crash.log 位于 filesDir/crash.log（崩溃捕获落盘）。
/// </summary>
public sealed class AndroidLogFileProvider : ILogFileProvider
{
    public IReadOnlyList<LogSource> GetLogs()
    {
        var list = new List<LogSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // fta.trace.log 可能位于外部文件目录(优先)或内部私有 files 目录(外部不可用/被清理时回退)
        string? extDir;
        string? filesDir;
        try
        {
            var ctx = global::Android.App.Application.Context;
            extDir = ctx?.GetExternalFilesDir(null)?.AbsolutePath;
            filesDir = ctx?.FilesDir?.AbsolutePath;
        }
        catch
        {
            extDir = null;
            filesDir = null;
        }

        AddTrace(list, seen, extDir, "fta.trace.log", "fta.trace.log");
        AddTrace(list, seen, filesDir, "fta.trace.log", "fta.trace.log (internal)");
        AddTrace(list, seen, filesDir, "crash.log", "crash.log");

        return list;
    }

    private static void AddTrace(List<LogSource> list, HashSet<string> seen, string? dir, string name, string title)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) return;
            var key = Path.GetDirectoryName(path) + name;
            if (!seen.Add(key)) return;
            list.Add(new LogSource(title, path));
        }
        catch
        {
            // ignore
        }
    }
}