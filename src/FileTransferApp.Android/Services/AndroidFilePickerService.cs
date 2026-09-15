using FileTransferApp.Services;

namespace FileTransferApp.Android.Services;

/// <summary>
/// Android 原生文件选择器：委托当前 MainActivity 启动系统 SAF 文档选择器
/// （ACTION_OPEN_DOCUMENT），避免依赖 Avalonia StorageProvider。
/// </summary>
public sealed class AndroidFilePickerService : IFilePickerService
{
    public Task<string[]> PickFilesAsync()
    {
        var activity = MainActivity.Current;
        if (activity is null)
        {
            global::Android.Util.Log.Warn("FTA.FILE", "PICK-NO-ACTIVITY");
            return Task.FromResult(Array.Empty<string>());
        }
        return activity.PickFilesNativeAsync();
    }
}