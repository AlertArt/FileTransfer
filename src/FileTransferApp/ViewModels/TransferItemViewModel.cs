using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FileTransferApp.ViewModels;

/// <summary>
/// 单个传输任务视图模型。监听 TransferProgressMessage / TransferStatusChangedMessage / TransferCompletedMessage，
/// 在 UI 线程上更新进度、网速、状态与缩略图，并提供暂停/恢复/取消命令。
/// </summary>
public partial class TransferItemViewModel : ObservableObject,
    IRecipient<TransferProgressMessage>,
    IRecipient<TransferStatusChangedMessage>,
    IRecipient<TransferCompletedMessage>,
    IRecipient<TransferThumbnailUpdatedMessage>
{
    private readonly IMessenger _messenger;
    private readonly ITransferEngine _engine;
    private readonly TransferTaskInfo _task;

    public string FileId => _task.FileId;
    public TransferDirection Direction => _task.Direction;
    /// <summary>接收完成后最终保存路径（发送任务为 null）</summary>
    public string? LocalPath => _task.LocalPath;

    [ObservableProperty] public partial string FileName { get; set; }
    [ObservableProperty] public partial long TotalBytes { get; set; }
    [ObservableProperty] public partial long BytesTransferred { get; set; }
    [ObservableProperty] public partial double ProgressPercentage { get; set; }
    [ObservableProperty] public partial string SpeedText { get; set; } = "0 KB/s";
    /// <summary>文件总大小的可读文本：12.3 MB / 1.2 GB</summary>
    [ObservableProperty] public partial string TotalSizeText { get; set; } = "0 B";
    /// <summary>方向 + 大小合并标签：[发送] 12.3 MB （紧凑模式下放在同一行）</summary>
    [ObservableProperty] public partial string MetaText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StateText { get; set; } = TransferState.Created.ToString();
    /// <summary>原始状态枚举，供 StatusToColorConverter 绑定上色</summary>
    [ObservableProperty] public partial TransferState State { get; set; } = TransferState.Created;
    [ObservableProperty] public partial Bitmap? ThumbnailImage { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial bool IsTerminal { get; set; }
    /// <summary>非终态且非 Created：显示取消按钮（等待审批、传输中、暂停、断开都可取消）</summary>
    [ObservableProperty] public partial bool CanCancel { get; set; }
    /// <summary>任务在终态（已完成/失败/已取消）：显示删除按钮</summary>
    [ObservableProperty] public partial bool CanDelete { get; set; }
    /// <summary>任务出错（断开/失败）：显示重试按钮，重新握手并续传剩余切片</summary>
    [ObservableProperty] public partial bool CanRetry { get; set; }
    /// <summary>已完成的接收任务：双击卡片可打开文件</summary>
    [ObservableProperty] public partial bool CanOpenFile { get; set; }
    /// <summary>
    /// 错误/警告信息：失败 / 已断开 / 已取消时展示，用于用户自助排查。
    /// 取值来源：TransferTaskInfo.ErrorMessage（由引擎填充），空串表示无错误信息不显示。
    /// </summary>
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    /// <summary>是否显示 ErrorMessage 行（节省卡片纵向空间）：非空 且 处于失败/取消/断开 任一终态</summary>
    [ObservableProperty] public partial bool ShowErrorHint { get; set; }

    public TransferItemViewModel(IMessenger messenger, ITransferEngine engine, TransferTaskInfo task)
    {
        _messenger = messenger;
        _engine = engine;
        _task = task;

        FileName = task.FileName;
        TotalBytes = task.TotalBytes;
        BytesTransferred = task.BytesTransferred;
        ProgressPercentage = task.ProgressPercentage;
        TotalSizeText = Core.Services.Impl.SpeedFormatter.FormatSize(TotalBytes);
        MetaText = BuildMetaText(Direction, TotalBytes);
        State = task.State;
        StateText = StateToText(task.State);
        RefreshStateFlags(task.State);
        ThumbnailImage = ParseThumbnail(task.ThumbnailBase64);

        _messenger.RegisterAll(this);

        // 监听语言切换：重新生成状态文本与方向标签
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Item[]" or "")) return;
        Dispatcher.UIThread.Post(() =>
        {
            StateText = StateToText(State);
            MetaText = BuildMetaText(Direction, TotalBytes);
            // 错误文案是基于之前语言解析的，切换语言后按当前语言重新解析
            SyncErrorMessageFromEngine();
        });
    }

    public void Receive(TransferProgressMessage message)
    {
        if (message.FileId != FileId) return;
        Dispatcher.UIThread.Post(() =>
        {
            BytesTransferred = message.BytesTransferred;
            TotalBytes = message.TotalBytes;
            ProgressPercentage = TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesTransferred / TotalBytes * 100, 0, 100);
            SpeedText = FormatSpeed(message.SpeedBytesPerSecond);
            // 同步 task 当前状态：防止因注册时序竞态漏接 TransferStatusChangedMessage
            var currentTask = _engine.GetTask(FileId);
            if (currentTask is not null && currentTask.State != State)
                ApplyStateChange(currentTask.State);
        });
    }

    public void Receive(TransferStatusChangedMessage message)
    {
        if (message.FileId != FileId) return;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyStateChange(message.NewState);
            // 状态变化后同步 Engine 可能已写入的 ErrorMessage（catch 块往往在 SetState 前写入 task.ErrorMessage）
            SyncErrorMessageFromEngine();
        });
    }

    /// <summary>
    /// 外部（如 MainViewModel）手动转发状态变更，防止注册前的消息丢失。
    /// </summary>
    public void ApplyStateChange(TransferState newState)
    {
        State = newState;
        StateText = StateToText(newState);
        RefreshStateFlags(newState);
        SyncErrorMessageFromEngine();
        if (newState == TransferState.Completed)
        {
            BytesTransferred = TotalBytes;
            ProgressPercentage = 100;
            RefreshThumbnailFromTask();
        }
    }

    public void Receive(TransferCompletedMessage message)
    {
        if (message.FileId != FileId) return;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyCompleted(message.Success);
            // 引擎任务在时优先取本地化错误（含 ErrorCode）；任务已被移除时退回 CompletedMessage 快照
            SyncErrorMessageFromEngine();
            if (string.IsNullOrEmpty(ErrorMessage) && !string.IsNullOrEmpty(message.ErrorMessage))
                SetError(message.ErrorMessage);
        });
    }

    /// <summary>
    /// 外部（如 MainViewModel）手动转发完成事件，防止注册前的消息丢失。
    /// </summary>
    public void ApplyCompleted(bool success)
    {
        if (success)
        {
            BytesTransferred = TotalBytes;
            ProgressPercentage = 100;
            // 接收成功后，若 ThumbnailImage 仍为空则再尝试从 task 最新 ThumbnailBase64 解析
            RefreshThumbnailFromTask();
        }
        IsTerminal = true;
        IsRunning = false;
        IsPaused = false;
        CanCancel = false;
        CanDelete = true;
        SyncErrorMessageFromEngine();
    }

    /// <summary>
    /// 从底层 task.ErrorMessage 拉取最新错误信息；空值时不覆盖之前的错误。
    /// 只要不是 TransferState.Completed 且有错误信息，就把 ShowErrorHint=true。
    /// 优先按 ErrorCode 本地化（跟随当前语言），资源缺失时回退原始 ErrorMessage。
    /// </summary>
    private void SyncErrorMessageFromEngine()
    {
        var t = _engine.GetTask(FileId);
        if (t is null) return;
        var text = ResolveError(t);
        if (!string.IsNullOrEmpty(text))
            SetError(text);
    }

    private static string? ResolveError(TransferTaskInfo t)
    {
        if (!string.IsNullOrEmpty(t.ErrorCode) && LocalizationService.Instance.HasString(t.ErrorCode))
            return LocalizationService.Instance.Format(t.ErrorCode, t.ErrorArgs ?? Array.Empty<object>());
        return t.ErrorMessage;
    }

    private void SetError(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        ErrorMessage = msg;
        // Completed 成功不显示失败错误行；其它终态（Failed/Cancelled/Disconnected）显示
        ShowErrorHint = State is TransferState.Failed or TransferState.Cancelled or TransferState.Disconnected;
    }

    public void Receive(TransferThumbnailUpdatedMessage message)
    {
        if (message.FileId != FileId) return;
        Dispatcher.UIThread.Post(() =>
        {
            // 引擎在接收完成后重新生成了缩略图，更新到 UI
            var bmp = ParseThumbnail(message.ThumbnailBase64);
            if (bmp is not null) ThumbnailImage = bmp;
        });
    }

    /// <summary>
    /// 从底层 task 的最新 ThumbnailBase64 刷新缩略图（完成时、缩略图更新消息时调用）。
    /// 仅在当前 ThumbnailImage 为空时生效，避免已展示的缩略图被重新覆盖。
    /// </summary>
    private void RefreshThumbnailFromTask()
    {
        if (ThumbnailImage is not null) return;
        var latest = _engine.GetTask(FileId);
        if (latest is null || string.IsNullOrEmpty(latest.ThumbnailBase64)) return;
        var bmp = ParseThumbnail(latest.ThumbnailBase64);
        if (bmp is not null) ThumbnailImage = bmp;
    }

    [RelayCommand]
    private Task PauseAsync() => _engine.PauseAsync(FileId);

    [RelayCommand]
    private Task ResumeAsync() => _engine.ResumeAsync(FileId);

    [RelayCommand]
    private Task RetryAsync() => _engine.RetryAsync(FileId);

    [RelayCommand]
    private Task CancelAsync() => _engine.CancelAsync(FileId);

    [RelayCommand]
    private Task DeleteAsync() => _engine.RemoveTaskAsync(FileId);

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (string.IsNullOrEmpty(LocalPath)) return;
        var svc = ServiceLocator.Services?.GetRequiredService<IFileOpenService>();
        if (svc is not null)
            await svc.OpenFileAsync(LocalPath).ConfigureAwait(false);
    }

    // ---- helpers ----
    private void RefreshStateFlags(TransferState s)
    {
        IsRunning = s == TransferState.Transferring;
        IsPaused = s == TransferState.Paused;
        IsTerminal = s is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;
        // 非 Created 且非终态 → 可取消（等待审批、传输中、暂停、断开）
        CanCancel = s != TransferState.Created && !IsTerminal;
        CanDelete = IsTerminal;
        // 断开 / 失败 → 可重试（重新握手 + 续传）
        CanRetry = s is TransferState.Disconnected or TransferState.Failed;
        // 已完成的接收任务 → 双击可打开文件
        CanOpenFile = s == TransferState.Completed
                      && _task.Direction == TransferDirection.Receive
                      && !string.IsNullOrEmpty(_task.LocalPath);
    }

    private static string StateToText(TransferState s)
    {
        var key = s switch
        {
            TransferState.Created => "State.Created",
            TransferState.Preparing => "State.Preparing",
            TransferState.WaitingApproval => "State.WaitingApproval",
            TransferState.Transferring => "State.Transferring",
            TransferState.Paused => "State.Paused",
            TransferState.Disconnected => "State.Disconnected",
            TransferState.Completed => "State.Completed",
            TransferState.Failed => "State.Failed",
            TransferState.Cancelled => "State.Cancelled",
            _ => s.ToString(),
        };
        return LocalizationService.Instance.GetString(key);
    }

    private static string FormatSpeed(double bytesPerSecond)
        => Core.Services.Impl.SpeedFormatter.FormatSpeed(bytesPerSecond);

    private static string BuildMetaText(TransferDirection dir, long totalBytes)
    {
        var dirKey = dir == TransferDirection.Send ? "Direction.Send" : "Direction.Receive";
        var dirText = LocalizationService.Instance.GetString(dirKey);
        return $"[{dirText}] {Core.Services.Impl.SpeedFormatter.FormatSize(totalBytes)}";
    }

    private static Bitmap? ParseThumbnail(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return new Bitmap(new MemoryStream(bytes));
        }
        catch { return null; }
    }
}
