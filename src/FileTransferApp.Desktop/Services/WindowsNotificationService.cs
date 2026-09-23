using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.WinUI.Notifications;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Notifications;

namespace FileTransferApp.Desktop.Services;

/// <summary>
/// Windows 系统 Toast 通知实现。
///
/// 传输进行中：首次 <c>Show</c> 一个「数据绑定」进度 Toast（带「取消」按钮），之后一律用
/// <c>ToastNotifier.Update(NotificationData, tag, group)</c> **原地刷新**。
/// 【闪烁坑】绝不能每次进度都 <c>Show</c>——Windows 对同 Tag/Group 会「先撤下再弹出」，
/// 每秒刷新会表现为通知不停开合；Update 不触发弹出动画。
/// 【重复坑】同一段状态文字只能出现一次：这里只用进度条的 <c>status</c> 承载状态。
///
/// 传输完成 / 失败：另投一次性状态 Toast（完成且有本地路径时带「打开文件夹」按钮）。
/// 操作按钮的点击通过 <c>ToastNotificationManagerCompat.OnActivated</c> 回调处理。
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

    public WindowsNotificationService()
    {
        // 操作按钮点击回调（应用运行/被唤起时触发）
        try { ToastNotificationManagerCompat.OnActivated += OnToastActivated; }
        catch { /* 通知不可用不影响运行 */ }
    }

    public void StartKeepAlive(string title, string content)
    {
        // Windows 无"常驻保活"概念：不在此投放 Toast，等首个带 fileId 的进度更新再显示
        // （这样进度 Toast 才能带上"取消"按钮）。
    }

    public void UpdateKeepAlive(string title, string content, double? progress, string? fileId)
        => ShowOrUpdateTransfer(content, progress ?? 0.0, fileId);

    public void StopKeepAlive()
    {
        try { ToastNotificationManagerCompat.History.Remove(TransferTag, TransferGroup); }
        catch { /* ignore */ }
        _transferVisible = false;
        _sequence = 0;
        _lastSignature = string.Empty;
    }

    public void ShowStatusNotification(string title, string content, string? openPath)
    {
        try
        {
            // 只保留一行正文：Windows 通知头部已显示应用名，再加标题行会重复。
            var builder = new ToastContentBuilder().AddText(content);
            if (!string.IsNullOrEmpty(openPath))
            {
                builder.AddButton(new ToastButton(
                    LocalizationService.Instance.GetString("Notification.Open"),
                    BuildArgs(("action", "open"), ("path", openPath!))));
            }
            builder.Show(t =>
            {
                t.Tag = StatusTag;
                t.Group = StatusGroup;
            });
        }
        catch { /* ignore */ }
    }

    private void ShowOrUpdateTransfer(string content, double progress01, string? fileId)
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
            var toast = new ToastNotification(BuildTransferXml(fileId))
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
    /// 数据绑定 Toast 模板：进度条承载状态文字（供 Update 原地刷新）；
    /// fileId 非空时附加「取消」按钮（参数携带 fileId，点击经 OnActivated 处理）。
    /// </summary>
    private static Windows.Data.Xml.Dom.XmlDocument BuildTransferXml(string? fileId)
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

        if (!string.IsNullOrEmpty(fileId))
        {
            content.Actions = new ToastActionsCustom
            {
                Buttons =
                {
                    new ToastButton(
                        LocalizationService.Instance.GetString("Notification.Cancel"),
                        BuildArgs(("action", "cancel"), ("id", fileId!))),
                },
            };
        }

        return content.GetXml();
    }

    // ===================== 操作按钮回调 =====================

    private static void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ParseArgs(e.Argument);
            if (!args.TryGetValue("action", out var action)) return;

            switch (action)
            {
                case "cancel":
                    if (args.TryGetValue("id", out var fileId) && !string.IsNullOrEmpty(fileId))
                        _ = CancelTaskAsync(fileId);
                    break;

                case "open":
                    if (args.TryGetValue("path", out var path))
                        OpenInExplorer(path);
                    break;
            }
        }
        catch { /* 回调失败不影响运行 */ }
    }

    private static async Task CancelTaskAsync(string fileId)
    {
        try
        {
            var engine = ServiceLocator.Services?.GetService<ITransferEngine>();
            if (engine is not null) await engine.CancelAsync(fileId).ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    /// <summary>在资源管理器中定位/打开接收到的文件（失败静默）。</summary>
    private static void OpenInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>把 (key,value) 序列化为 Toast 按钮参数串（形如 <c>action=cancel&amp;id=xxx</c>）。</summary>
    private static string BuildArgs(params (string Key, string Value)[] pairs)
        => string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static Dictionary<string, string> ParseArgs(string? argument)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(argument)) return result;
        foreach (var pair in argument.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[key] = val;
        }
        return result;
    }
}
