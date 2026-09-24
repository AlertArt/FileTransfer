using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Interfaces;
using FileTransferApp.Services;
using Xunit;

namespace FileTransferApp.Tests;

/// <summary>
/// TransferNotificationBridge：进度 → 进行中通知（限频），完成/失败 → 一次性状态通知。
/// 使用隔离的 <see cref="WeakReferenceMessenger"/> 实例，避免与全局 Default messenger 串扰。
/// </summary>
public class TransferNotificationBridge_Tests
{
    private static (RecordingKeepAlive keepAlive, StubEngine engine, IMessenger messenger) Build()
    {
        var messenger = new WeakReferenceMessenger();
        var engine = new StubEngine();
        var keepAlive = new RecordingKeepAlive();
        _ = new TransferNotificationBridge(messenger, engine, keepAlive, LocalizationService.Instance);
        return (keepAlive, engine, messenger);
    }

    [Fact]
    public void Progress_Updates_KeepAlive_With_Percent_And_Progress()
    {
        var (keepAlive, engine, messenger) = Build();
        engine.Tasks["f1"] = new TransferTaskInfo
        {
            FileId = "f1", FileName = "a.bin", TotalBytes = 1000,
            Direction = TransferDirection.Send, State = TransferState.Transferring,
        };

        messenger.Send(new TransferProgressMessage("f1", 500, 1000, 1_500_000));

        Assert.Equal(1, keepAlive.UpdateCount);
        Assert.NotNull(keepAlive.LastProgress);
        Assert.Equal(0.5, keepAlive.LastProgress!.Value, 2);
        Assert.Contains("50%", keepAlive.LastContent);
    }

    [Fact]
    public void Progress_Is_Throttled_Within_Interval()
    {
        var (keepAlive, engine, messenger) = Build();
        engine.Tasks["f1"] = new TransferTaskInfo
        {
            FileId = "f1", FileName = "a.bin", TotalBytes = 1000,
            Direction = TransferDirection.Send, State = TransferState.Transferring,
        };

        messenger.Send(new TransferProgressMessage("f1", 100, 1000, 1000));
        messenger.Send(new TransferProgressMessage("f1", 200, 1000, 1000));

        Assert.Equal(1, keepAlive.UpdateCount);
    }

    [Fact]
    public void Completed_Success_Posts_Status_With_FileName()
    {
        var (keepAlive, engine, messenger) = Build();
        engine.Tasks["f1"] = new TransferTaskInfo
        {
            FileId = "f1", FileName = "movie.mp4", TotalBytes = 2048,
            Direction = TransferDirection.Receive, State = TransferState.Completed,
        };

        messenger.Send(new TransferCompletedMessage("f1", true, null));

        Assert.Equal(1, keepAlive.StatusCount);
        Assert.Contains("movie.mp4", keepAlive.LastStatusContent);
    }

    [Fact]
    public void Completed_Failure_Posts_Status_With_Reason()
    {
        var (keepAlive, engine, messenger) = Build();
        engine.Tasks["f1"] = new TransferTaskInfo
        {
            FileId = "f1", FileName = "movie.mp4", TotalBytes = 2048,
            Direction = TransferDirection.Receive, State = TransferState.Failed,
            ErrorMessage = "connection reset",
        };

        messenger.Send(new TransferCompletedMessage("f1", false, "connection reset"));

        Assert.Equal(1, keepAlive.StatusCount);
        Assert.Contains("connection reset", keepAlive.LastStatusContent);
    }

    private sealed class RecordingKeepAlive : IPlatformKeepAliveService
    {
        public int UpdateCount { get; private set; }
        public int StatusCount { get; private set; }
        public double? LastProgress { get; private set; }
        public string LastContent { get; private set; } = string.Empty;
        public string LastStatusContent { get; private set; } = string.Empty;

        public void StartKeepAlive(string title, string content) { }
        public void StopKeepAlive() { }
        public void UpdateKeepAlive(string title, string content, double? progress, string? fileId)
        {
            UpdateCount++;
            LastContent = content;
            LastProgress = progress;
        }
        public void ShowStatusNotification(string title, string content, string? openPath)
        {
            StatusCount++;
            LastStatusContent = content;
        }
    }

    private sealed class StubEngine : ITransferEngine
    {
#pragma warning disable CS0067 // mock 不触发状态事件，仅满足接口签名
        public event EventHandler<TransferStatusChangedMessage>? StateChanged;
#pragma warning restore CS0067

        public Dictionary<string, TransferTaskInfo> Tasks { get; } = new();

        public Task ApplyControlAsync(string fileId, TransferAction action) => Task.CompletedTask;
        public Task CancelAsync(string fileId) => Task.CompletedTask;
        public Task<TransferTaskInfo> CreateSendTaskAsync(string filePath, DeviceNode peer, CancellationToken ct = default) => throw new NotSupportedException();
        public TransferTaskInfo? GetTask(string fileId) => Tasks.TryGetValue(fileId, out var t) ? t : null;
        public IReadOnlyList<TransferTaskInfo> GetTasks() => Tasks.Values.ToList();
        public Task PauseAsync(string fileId) => Task.CompletedTask;
        public Task<PrepareResponse> RegisterReceiveTaskAsync(PrepareRequest prepare, DeviceNode peer) => throw new NotSupportedException();
        public Task ResumeAsync(string fileId) => Task.CompletedTask;
        public Task RetryAsync(string fileId) => Task.CompletedTask;
        public Task RemoveTaskAsync(string fileId) => Task.CompletedTask;
        public Task StartSendAsync(string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> WriteChunkAsync(string fileId, int chunkIndex, int totalChunks, ReadOnlyMemory<byte> data) => throw new NotSupportedException();
    }
}
