using System.Diagnostics;
using System.IO;
using System.Text;

namespace FileTransferApp.Core.Diagnostics;

/// <summary>
/// 带大小上限 + 轮转的文件 Trace 监听器。
/// 当日志超过 <c>maxBytes</c> 时：删除最旧备份 → 依次 path.(N-1)→path.N、…、path→path.1，
/// 然后重新从空文件开始写。目的：避免长时间运行 / 大文件传输把 <c>fta.trace.log</c> 撑到几百 MB。
/// 线程安全（Trace 可能从多个后台线程写入）。
/// </summary>
public sealed class RollingFileTraceListener : TraceListener
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxBackups;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private long _size;

    /// <param name="path">主日志文件路径。</param>
    /// <param name="maxBytes">单文件大小上限，超过即轮转（默认约 1MB）。</param>
    /// <param name="maxBackups">保留的备份个数（path.1 .. path.N）。</param>
    public RollingFileTraceListener(string path, long maxBytes = 1_000_000, int maxBackups = 3)
    {
        _path = path;
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _maxBackups = Math.Max(1, maxBackups);
        TryOpen();
    }

    public override void Write(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                _writer.Write(message);
                _size += message.Length;
                if (_size >= _maxBytes) Rotate();
            }
            catch { /* 写失败不影响主流程 */ }
        }
    }

    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine(message);
                _size += (message?.Length ?? 0) + Environment.NewLine.Length;
                if (_size >= _maxBytes) Rotate();
            }
            catch { /* ignore */ }
        }
    }

    public override void Flush()
    {
        lock (_gate) { try { _writer?.Flush(); } catch { /* ignore */ } }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
        }
        base.Dispose(disposing);
    }

    private void Rotate()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;

        try
        {
            var oldest = $"{_path}.{_maxBackups}";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var i = _maxBackups - 1; i >= 1; i--)
            {
                var src = $"{_path}.{i}";
                var dst = $"{_path}.{i + 1}";
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            if (File.Exists(_path)) File.Move(_path, $"{_path}.1", overwrite: true);
        }
        catch { /* 轮转失败则继续追加，不影响运行 */ }

        TryOpen();
    }

    private void TryOpen()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var fi = new FileInfo(_path);
            _size = fi.Exists ? fi.Length : 0;
            var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        }
        catch
        {
            _writer = null; // 不可写：退化为不落盘（内存缓冲仍可用）
        }
    }
}
