using System.Globalization;
using CommunityToolkit.WinUI.Notifications;
using FileTransferApp.Core.Services.Interfaces;
using Windows.UI.Notifications;

namespace FileTransferApp.Desktop.Services;

/// <summary>
/// Windows 系统 Toast 通知实现（桌面端替代此前的 <c>NullPlatformKeepAliveService</c>）。
///
/// 传输进行中：首次 <c>Show</c> 一个「数据绑定」进度 Toast，之后一律用
/// <c>ToastNotifier.Update(NotificationData, tag, group)</c> **原地刷新**。
/// 【闪烁坑】绝不能每次进度都 <c>Show</c>——Windows 对同 Tag/Group 会「先撤下再弹出」，
/// 每秒刷新会表现为通知不停开合；Update 不触发弹出动画。
/// 【重复坑】同一段状态文字只能出现一次：这里只用进度条的 <c>status</c> 承载状态，
/// 不再额外加独立文本行（Windows 会自动显示应用名，标题行也会与之重复）。
///
/// 传输完成 / 失败：另投一次性状态 Toast（同样只用一行正文，避免与应用名重复）。
/// 全程 try/catch，通知失败不影响传输。
/// </summary>
public sealed class WindowsNotificationService : IPlatformKeepAliveService
{
    private const string TransferTag = "fta-transfer";
    private const string TransferGroup = "fta-transfer";
    private const string StatusTag = "fta-status";
    private const string StatusGroup = "fta-status";

    // 数据绑定键：Toast XML 中占位符 = 键名，NotificationData.Values[键名] 提供真实值
    private const string KeyStatusText = "statusText";
    private const string KeyProgressValue = "progressValue";

    private bool _transferVisible;
    private uint _sequence;
    private string _lastSignature = string.Empty;

    public void StartKeepAlive(string title, string content)
        => ShowOrUpdateTransfer(content, 0.0);

    public void UpdateKeepAlive(string title, string content, double? progress)
        => ShowOrUpdateTransfer(content, progress ?? 0.0);

    public void StopKeepAlive()
    {
        try { ToastNotificationManagerCompat.History.Remove(TransferTag, TransferGroup); }
        catch { /* ignore */ }
        _transferVisible = false;
        _sequence = 0;
        _lastSignature = string.Empty;
    }

    public void ShowStatusNotification(string title, string content)
    {
        try
        {
            // 只保留一行正文：Windows 通知头部已显示应用名，再加标题行会重复。
            new ToastContentBuilder()
                .AddText(content)
                .Show(t =>
                {
                    t.Tag = StatusTag;
                    t.Group = StatusGroup;
                });
        }
        catch { /* ignore */ }
    }

    private void ShowOrUpdateTransfer(string content, double progress01)
    {
        var value = Math.Clamp(progress01, 0, 1);
        var percent = (int)Math.Round(value * 100);
        var signature = $"{content}|{percent}";
        if (signature == _lastSignature) return;

        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();

            if (_transferVisible)
            {
                var update = BuildData(value, content);
                update.SequenceNumber = ++_sequence;
                notifier.Update(update, TransferTag, TransferGroup); // 原地刷新（无弹出动画）
                _lastSignature = signature;
                return;
            }

            var data = BuildData(value, content);
            data.SequenceNumber = ++_sequence;
            var toast = new ToastNotification(BuildTransferXml())
            {
                Tag = TransferTag,
                Group = TransferGroup,
                Data = data,
            };
            notifier.Show(toast);
            _transferVisible = true;
            _lastSignature = signature;
        }
        catch { /* ignore：通知不可用不阻断传输 */ }
    }

    private static NotificationData BuildData(double value, string status)
    {
        var data = new NotificationData();
        data.Values[KeyStatusText] = status;
        data.Values[KeyProgressValue] = value.ToString("0.###", CultureInfo.InvariantCulture);
        return data;
    }

    /// <summary>
    /// 数据绑定 Toast 模板：只含一个进度条，状态文字放在其 <c>status</c> 上
    /// （占位符由 NotificationData.Values 提供，供 Update 原地刷新）。
    /// </summary>
    private static Windows.Data.Xml.Dom.XmlDocument BuildTransferXml()
    {
        var content = new ToastContent
        {
            Visual = new ToastVisual
            {
                BindingGeneric = new ToastBindingGeneric
                {
                    Children =
                    {
                        new AdaptiveProgressBar
                        {
                            Value = new BindableProgressBarValue(KeyProgressValue),
                            Status = new BindableString(KeyStatusText),
                        },
                    },
                },
            },
        };
        return content.GetXml();
    }
}
