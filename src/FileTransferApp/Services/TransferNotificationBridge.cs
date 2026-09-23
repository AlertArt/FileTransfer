using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Services;

/// <summary>
/// 把传输进度/状态桥接到平台通知（Android 通知栏 / Windows Toast / iOS 本地通知）。
///
/// 数据来源（均由 <c>PipelinesTransferEngine</c> 经消息总线推送）：
///  - <see cref="TransferProgressMessage"/>：实时字节数与网速 → 刷新「进行中」通知（限频 ~900ms，避免通知频闪）；
///  - <see cref="TransferStatusChangedMessage"/>：等待接收 / 已暂停等非进度态 → 刷新通知文案；
///  - <see cref="TransferCompletedMessage"/>：完成 / 失败 → 投放一次性状态通知。
///
/// 生命周期：构造时即注册到消息总线；由 DI 以单例持有（<c>ServiceConfiguration</c>），
/// 只需在启动时解析一次即可生效。
/// </summary>
public sealed class TransferNotificationBridge :
    IRecipient<TransferProgressMessage>,
    IRecipient<TransferStatusChangedMessage>,
    IRecipient<TransferCompletedMessage>
{
    /// <summary>通知刷新限频：同一文件两次刷新至少间隔该毫秒数（引擎自身 100ms 推一帧，太密会频闪）。</summary>
    private const long ThrottleMs = 900;

    private readonly ITransferEngine _engine;
    private readonly IPlatformKeepAliveService _keepAlive;
    private readonly Dictionary<string, long> _lastTick = new();

    public TransferNotificationBridge(
        IMessenger messenger,
        ITransferEngine engine,
        IPlatformKeepAliveService keepAlive)
    {
        _engine = engine;
        _keepAlive = keepAlive;
        messenger.RegisterAll(this);
    }

    public void Receive(TransferProgressMessage message)
    {
        if (!ShouldTick(message.FileId)) return;
        var task = _engine.GetTask(message.FileId);
        if (task is null) return;

        var pct = message.TotalBytes <= 0
            ? 0
            : (int)Math.Clamp(message.BytesTransferred * 100.0 / message.TotalBytes, 0.0, 100.0);
        var speed = SpeedFormatter.FormatSpeed(message.SpeedBytesPerSecond);
        // 发送/接收用不同动词，方向取自任务（引擎任务在收尾前一直有效）
        var key = task.Direction == TransferDirection.Send ? "Notification.Transferring" : "Notification.Receiving";
        var content = LocalizationService.Instance.Format(key, task.FileName, pct, speed);
        // 带 fileId → Android 通知挂"暂停/取消"操作按钮
        _keepAlive.UpdateKeepAlive(LocalizationService.Instance.GetString("KeepAliveTitle"), content, pct / 100.0, task.FileId);
    }

    public void Receive(TransferStatusChangedMessage message)
    {
        switch (message.NewState)
        {
            case TransferState.WaitingApproval:
                NotifyState(message.FileId, "Notification.WaitingApproval");
                break;
            case TransferState.Paused:
                NotifyState(message.FileId, "Notification.Paused");
                break;
            case TransferState.Completed:
            case TransferState.Failed:
            case TransferState.Cancelled:
                // 终态：交给 TransferCompletedMessage 投放一次性通知；这里清掉限频记录
                _lastTick.Remove(message.FileId);
                break;
        }
    }

    public void Receive(TransferCompletedMessage message)
    {
        var task = _engine.GetTask(message.FileId);
        var name = task?.FileName ?? message.FileId;
        var title = LocalizationService.Instance.GetString("KeepAliveTitle");
        // 完成通知的"打开文件"按钮仅在接收完成、且有本地路径时提供
        var openPath = message.Success && task?.Direction == TransferDirection.Receive
            ? task.LocalPath
            : null;

        if (message.Success)
        {
            var size = SpeedFormatter.FormatSize(task?.TotalBytes ?? 0);
            _keepAlive.ShowStatusNotification(title,
                LocalizationService.Instance.Format("Notification.Completed", name, size), openPath);
        }
        else
        {
            var reason = ResolveError(task) ?? LocalizationService.Instance.GetString("State.Failed");
            _keepAlive.ShowStatusNotification(title,
                LocalizationService.Instance.Format("Notification.Failed", name, reason), null);
        }
        _lastTick.Remove(message.FileId);
    }

    private void NotifyState(string fileId, string key)
    {
        if (!ShouldTick(fileId)) return;
        var task = _engine.GetTask(fileId);
        if (task is null) return;
        var content = LocalizationService.Instance.Format(key, task.FileName);
        _keepAlive.UpdateKeepAlive(LocalizationService.Instance.GetString("KeepAliveTitle"), content, null, task.FileId);
    }

    private bool ShouldTick(string fileId)
    {
        var now = Environment.TickCount64;
        if (_lastTick.TryGetValue(fileId, out var last) && now - last < ThrottleMs) return false;
        _lastTick[fileId] = now;
        return true;
    }

    /// <summary>与 TransferItemViewModel 一致的错误本地化：优先 ErrorCode，其次 ErrorMessage。</summary>
    private static string? ResolveError(TransferTaskInfo? t)
    {
        if (t is null) return null;
        if (!string.IsNullOrEmpty(t.ErrorCode) && LocalizationService.Instance.HasString(t.ErrorCode))
            return LocalizationService.Instance.Format(t.ErrorCode, t.ErrorArgs ?? Array.Empty<object>());
        return string.IsNullOrEmpty(t.ErrorMessage) ? null : t.ErrorMessage;
    }
}
