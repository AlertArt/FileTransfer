using Android.Content;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.Android.Services;

/// <summary>
/// 通知操作按钮的广播接收器：
///  - 进行中通知的「暂停 / 取消」→ 调用 <see cref="ITransferEngine"/>；
///  - 完成通知的「打开文件」→ 调用 <see cref="IFileOpenService"/>。
/// 由 <see cref="TransferForegroundService"/> 以显式 Intent 触发（Exported=false，仅本应用可达）。
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class NotificationActionReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action;
        if (string.IsNullOrEmpty(action)) return;

        if (action == TransferForegroundService.ActionOpenFile)
        {
            var path = intent!.GetStringExtra(TransferForegroundService.ExtraOpenPath);
            if (string.IsNullOrEmpty(path)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var svc = ServiceLocator.Services?.GetService<IFileOpenService>();
                    if (svc is not null) await svc.OpenFileAsync(path!).ConfigureAwait(false);
                }
                catch { /* 打开失败静默 */ }
            });
            return;
        }

        var fileId = intent!.GetStringExtra(TransferForegroundService.ExtraFileId);
        if (string.IsNullOrEmpty(fileId)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var engine = ServiceLocator.Services?.GetService<ITransferEngine>();
                if (engine is null) return;
                if (action == TransferForegroundService.ActionPause)
                    await engine.PauseAsync(fileId!).ConfigureAwait(false);
                else if (action == TransferForegroundService.ActionCancel)
                    await engine.CancelAsync(fileId!).ConfigureAwait(false);
            }
            catch { /* 操作失败静默 */ }
        });
    }
}
