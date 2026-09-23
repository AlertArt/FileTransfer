namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 移动端前台保活服务接口。
/// Android 8.0+ 必须绑定 Notifications 的 Foreground Service；iOS 提示用户置顶前台以避免后台冻结。
/// 同时承担「传输状态通知」职责：
///  - <see cref="UpdateKeepAlive"/> 在传输进行中刷新通知（速度 / 进度 / 状态），并可携带当前任务 ID 以挂操作按钮；
///  - <see cref="ShowStatusNotification"/> 在传输完成 / 失败时投放一次性通知，可携带打开路径。
/// </summary>
public interface IPlatformKeepAliveService
{
    void StartKeepAlive(string title, string content);

    /// <summary>
    /// 刷新「进行中」通知的标题/内容（不重启服务，避免通知频闪）。
    /// <paramref name="progress"/> 为 0..1 的完成度；传 null 表示只更新文案、不显示进度条。
    /// <paramref name="fileId"/> 为当前传输任务 ID（用于"暂停 / 取消"操作按钮）；null 表示不带按钮。
    /// </summary>
    void UpdateKeepAlive(string title, string content, double? progress, string? fileId);

    void StopKeepAlive();

    /// <summary>
    /// 投放一次性状态通知（传输完成 / 失败 / 已取消），可自动消失、不占用保活通知。
    /// <paramref name="openPath"/> 为已完成接收文件的本地路径（用于"打开文件"按钮）；null 表示不带按钮。
    /// </summary>
    void ShowStatusNotification(string title, string content, string? openPath);
}
