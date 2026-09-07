namespace FileTransferApp.Services;

/// <summary>
/// 平台文件打开服务接口：使用系统默认程序打开本地文件（双击接收卡片时触发）。
/// Desktop → Process.Start(UseShellExecute)；Android → FileProvider + Intent；iOS → UIDocumentInteractionController。
/// </summary>
public interface IFileOpenService
{
    /// <summary>使用系统关联程序打开本地文件，成功返回 true</summary>
    Task<bool> OpenFileAsync(string filePath);
}
