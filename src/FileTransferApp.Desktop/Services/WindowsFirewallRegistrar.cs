using System.Diagnostics;
using System.Runtime.Versioning;
using FileTransferApp.Core.Protocols;

namespace FileTransferApp.Desktop.Services;

/// <summary>
/// Windows 防火墙入站规则注册器（设计文档第 6.1 节）。
/// 应用首次运行时为 TCP 53318 端口注册允许入站规则，确保对端可发起文件传输握手。
/// 非管理员模式下静默失败并写入 Trace 日志，不影响应用启动。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsFirewallRegistrar
{
    private const string RuleName = "FileTransferApp";

    /// <summary>若规则不存在则注册；返回是否最终已存在规则（注册成功或原本就有）</summary>
    public static bool EnsureInboundRule()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (RuleExists()) return true;
            return TryAddRule();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"注册防火墙规则失败: {ex.Message}");
            return false;
        }
    }

    private static bool RuleExists()
    {
        // netsh show rule 命令在规则不存在时返回非零退出码
        var (exitCode, _) = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\"");
        return exitCode == 0;
    }

    private static bool TryAddRule()
    {
        var port = ProtocolConstants.TransferPort;
        // dir=in 表示入站；action=allow 允许；protocol=TCP；localport=53318
        var (exitCode, output) = RunNetsh(
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port}");

        if (exitCode != 0)
        {
            // 常见失败原因：非管理员权限（"请求的操作需要提升"）
            Trace.WriteLine($"netsh add rule 失败 (exit={exitCode}): {output}");
            return false;
        }
        return true;
    }

    /// <summary>同步执行 netsh 命令并捕获输出</summary>
    private static (int ExitCode, string Output) RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var p = Process.Start(psi);
        if (p is null) return (-1, "Process.Start returned null");

        // 同步等待退出，5 秒超时避免阻塞启动
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(); } catch { /* ignore */ }
            return (-1, "netsh timeout");
        }

        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        return (p.ExitCode, string.IsNullOrEmpty(output) ? err : output);
    }
}
