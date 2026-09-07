using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FileTransferApp.Desktop.Services;

/// <summary>
/// 桌面端文件打开：通过 Process.Start 以 ShellExecute 方式调用系统关联程序。
/// Windows：explorer.exe 用默认应用（图片/视频/PDF 等）打开；
/// Linux/macOS：xdg-open / open 命令。
/// </summary>
public sealed class DesktopFileOpenService : FileTransferApp.Services.IFileOpenService
{
    public Task<bool> OpenFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.FromResult(false);

        try
        {
            var psi = new ProcessStartInfo(filePath) { UseShellExecute = true };
            var p = Process.Start(psi);
            return Task.FromResult(p is not null);
        }
        catch
        {
            // 系统无关联程序 / 文件损坏 / 权限不足 → 静默失败
            return Task.FromResult(false);
        }
    }
}
