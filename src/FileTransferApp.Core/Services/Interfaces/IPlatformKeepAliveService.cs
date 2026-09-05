namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 移动端前台保活服务接口。
/// Android 8.0+ 必须绑定 Notifications 的 Foreground Service；iOS 提示用户置顶前台以避免后台冻结。
/// </summary>
public interface IPlatformKeepAliveService
{
    void StartKeepAlive(string title, string content);
    void StopKeepAlive();
}
