using System.Diagnostics;
using System.IO;

namespace FileTransferApp.Core.Diagnostics;

/// <summary>日志级别（数值越大越严重）。默认只输出 Info 及以上，Verbose 明细需显式开启。</summary>
public enum FtaLogLevel
{
    Verbose = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 平台无关的 Trace 日志辅助。使用 Trace.WriteLine(无条件输出) 而非 Trace.TraceInformation，
/// 避免依赖 System.Diagnostics.Trace 的默认开关级别；桌面/移动端启动时会挂文件监听器落盘。
/// 同时保留一份内存尾缓冲，供应用内「日志页」即使落盘失败也能展示最近内容。
///
/// 【分级】<see cref="MinLevel"/> 默认 <see cref="FtaLogLevel.Info"/>。
/// 大文件传输时"每切片 / 每 HTTP 请求"这类高噪声日志走 <see cref="Verbose"/>，
/// 默认**不落盘**——避免日志文件随传输量线性暴涨。需要排查（如设备断联）时，
/// 在「日志」页打开"详细日志"开关即可（<see cref="SetVerbose"/> 会持久化，重启仍生效）。
/// </summary>
public static class FtaTrace
{
    /// <summary>内存缓冲行数上限(约几十 KB)，仅用于日志页即时展示。</summary>
    private const int MaxBufferLines = 3000;

    private const string SettingsDirName = "FileTransferApp";
    private const string VerboseFlagFileName = "verbose-logging.txt";

    private static readonly object _gate = new();
    private static readonly Queue<string> _lines = new(MaxBufferLines);

    private static FtaLogLevel _minLevel = ResolveStartupLevel();

    /// <summary>运行期最低日志级别。设为 Verbose 即打开"详细日志"。</summary>
    public static FtaLogLevel MinLevel
    {
        get => _minLevel;
        set
        {
            if (_minLevel == value) return;
            _minLevel = value;
            Info("FTA.LOG", value <= FtaLogLevel.Verbose
                ? "verbose logging ENABLED"
                : $"verbose logging disabled (level={value})");
        }
    }

    /// <summary>当前是否输出详细(Verbose)日志。</summary>
    public static bool IsVerboseEnabled => _minLevel <= FtaLogLevel.Verbose;

    /// <summary>
    /// 明细日志：仅 Verbose 级别输出。用于"每切片 / 每请求 / 心跳"等高噪声路径，
    /// 默认关闭以保证大文件传输日志不暴涨。
    /// </summary>
    public static void Verbose(string tag, string message)
    {
        if (_minLevel > FtaLogLevel.Verbose) return;
        Write($"[{tag}] {message}");
    }

    /// <summary>普通日志：<c>[FTA.CTRL] ...</c></summary>
    public static void Info(string tag, string message)
    {
        if (_minLevel > FtaLogLevel.Info) return;
        Write($"[{tag}] {message}");
    }

    /// <summary>警告日志：<c>[FTA.CTRL] WARN ...</c></summary>
    public static void Warn(string tag, string message)
    {
        if (_minLevel > FtaLogLevel.Warn) return;
        Write($"[{tag}] WARN {message}");
    }

    /// <summary>错误日志：<c>[FTA.CTRL] ERROR ...</c></summary>
    public static void Error(string tag, string message)
    {
        if (_minLevel > FtaLogLevel.Error) return;
        Write($"[{tag}] ERROR {message}");
    }

    /// <summary>
    /// 打开/关闭详细日志并持久化（重启后仍生效）。供「日志」页开关调用。
    /// </summary>
    public static void SetVerbose(bool enabled)
    {
        MinLevel = enabled ? FtaLogLevel.Verbose : FtaLogLevel.Info;
        try
        {
            var path = GetVerboseFlagPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, enabled ? "1" : "0");
        }
        catch { /* 持久化失败不影响本次运行 */ }
    }

    /// <summary>返回最近的部分内存日志(不丢文件日志的最后一屏，供日志页兜底)。</summary>
    public static string Snapshot()
    {
        lock (_gate) return string.Join("\n", _lines);
    }

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

    /// <summary>启动级别：环境变量 FTA_LOG_LEVEL 优先，其次持久化开关文件，默认 Info。</summary>
    private static FtaLogLevel ResolveStartupLevel()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("FTA_LOG_LEVEL");
            if (!string.IsNullOrWhiteSpace(env) && Enum.TryParse<FtaLogLevel>(env, ignoreCase: true, out var lvl))
                return lvl;
        }
        catch { /* ignore */ }

        try
        {
            var path = GetVerboseFlagPath();
            if (File.Exists(path) && File.ReadAllText(path).Trim() == "1")
                return FtaLogLevel.Verbose;
        }
        catch { /* ignore */ }

        return FtaLogLevel.Info;
    }

    private static string GetVerboseFlagPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SettingsDirName,
            VerboseFlagFileName);
}
