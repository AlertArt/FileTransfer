using System.IO;
using FileTransferApp.Core.Diagnostics;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// RollingFileTraceListener：写入超过上限后应轮转（生成 .1 备份），主文件保持有界，
/// 避免大文件传输把 fta.trace.log 撑大。
/// </summary>
public class RollingFileTraceListenerTests
{
    [Fact]
    public void Rotates_And_Keeps_MainFile_Bounded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fta-roll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "t.log");
        var maxBytes = 64 * 1024;
        try
        {
            using (var listener = new RollingFileTraceListener(path, maxBytes: maxBytes, maxBackups: 2))
            {
                var line = new string('x', 200);
                for (var i = 0; i < 5000; i++) listener.WriteLine(line); // ~1MB 总量
            }

            Assert.True(File.Exists(path), "主日志文件应存在");
            Assert.True(File.Exists(path + ".1"), "应产生 .1 备份");
            Assert.True(new FileInfo(path).Length <= maxBytes + 4096,
                $"主文件应保持有界，实际 {new FileInfo(path).Length}");
            Assert.False(File.Exists(path + ".3"), "超过 maxBackups 的备份应被清理");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }
}
