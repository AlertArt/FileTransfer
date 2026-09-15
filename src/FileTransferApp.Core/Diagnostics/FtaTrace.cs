using System.Diagnostics;

namespace FileTransferApp.Core.Diagnostics;

/// <summary>
/// 平台无关的 Trace 日志辅助。使用 Trace.WriteLine(无条件输出) 而非 Trace.TraceInformation，
/// 避免依赖 System.Diagnostics.Trace 的默认开关级别；桌面/移动端启动时会挂文件监听器落盘。
/// 同时保留一份内存尾缓冲，供应用内「日志页」即使落盘失败也能展示最近内容。
/// </summary>
public static class FtaTrace
{
    /// <summary>内存缓冲行数上限(约几十 KB)，仅用于日志页即时展示。</summary>
    private const int MaxBufferLines = 3000;

    private static readonly object _gate = new();
    private static readonly Queue<string> _lines = new(MaxBufferLines);

    /// <summary>普通日志：<c>[FTA.CTRL] ...</c></summary>
    public static void Info(string tag, string message)
        => Write($"[{tag}] {message}");

    /// <summary>警告日志：<c>[FTA.CTRL] WARN ...</c></summary>
    public static void Warn(string tag, string message)
        => Write($"[{tag}] WARN {message}");

    private static void Write(string line)
    {
        try
        {
            Trace.WriteLine(line);
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxBufferLines) _lines.Dequeue();
            }
        }
        catch
        {
            // 日志不可用不影响主流程
        }
    }

    /// <summary>返回最近的部分内存日志(不丢文件日志的最后一屏，供日志页兜底)。</summary>
    public static string Snapshot()
    {
        lock (_gate) return string.Join("\n", _lines);
    }
}