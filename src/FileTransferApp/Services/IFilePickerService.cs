namespace FileTransferApp.Services;

/// <summary>跨平台文件选择器抽象</summary>
public interface IFilePickerService
{
    /// <summary>弹出文件选择对话框，返回所选文件的本地路径列表</summary>
    Task<string[]> PickFilesAsync();
}
