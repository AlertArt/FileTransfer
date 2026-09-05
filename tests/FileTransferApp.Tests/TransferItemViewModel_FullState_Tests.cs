using FileTransferApp.Core.Models;
using FileTransferApp.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Services.Interfaces;
using Xunit;

namespace FileTransferApp.Tests;

/// <summary>
/// 设计文档 7.1 第四条：TransferItemViewModel.RefreshStateFlags 全状态覆盖测试。
/// 实际构造 TransferItemViewModel 子类并以真实枚举全量驱动 RefreshStateFlags，
/// 保证 IsRunning/IsPaused/IsTerminal 三元组在 9 个 TransferState 上严格遵守 UI 契约，
/// 且构造函数对初始状态正确初始化、进度为 0。
/// </summary>
public class TransferItemViewModel_StateFlags_Tests
{
    /// <summary>直接驱动 RefreshStateFlags：封装为 internal helper，使用反射调用 VM 的 private 方法。</summary>
    private static void CallRefreshStateFlags(TransferItemViewModel vm, TransferState s)
    {
        var mi = typeof(TransferItemViewModel).GetMethod(
            "RefreshStateFlags",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(mi);
        mi!.Invoke(vm, new object[] { s });
    }

    // 通过子类公开构造函数；实际驱动 RefreshStateFlags 仍走反射（避免 InternalsVisibleTo 到 private 成员）
    public sealed class TestableTransferItemViewModel : TransferItemViewModel
    {
        public TestableTransferItemViewModel(IMessenger m, ITransferEngine e, TransferTaskInfo t)
            : base(m, e, t) { }
    }

    private static (TestableTransferItemViewModel vm, TransferTaskInfo task) Build(
        TransferState initialState = TransferState.Created)
    {
        var task = new TransferTaskInfo
        {
            FileId = "f-test",
            FileName = "a.bin",
            TotalBytes = 1024,
            BytesTransferred = 0,
            State = initialState,
        };
        var messenger = WeakReferenceMessenger.Default;
        var engine = new NSubstituteEngine();
        var vm = new TestableTransferItemViewModel(messenger, engine, task);
        return (vm, task);
    }

    [Theory]
    [InlineData(TransferState.Created,           false, false, false)]
    [InlineData(TransferState.Preparing,         false, false, false)]
    [InlineData(TransferState.WaitingApproval,   false, false, false)]
    [InlineData(TransferState.Transferring,       true, false, false)]
    [InlineData(TransferState.Paused,            false,  true, false)]
    [InlineData(TransferState.Disconnected,      false, false, false)]
    [InlineData(TransferState.Completed,         false, false,  true)]
    [InlineData(TransferState.Failed,            false, false,  true)]
    [InlineData(TransferState.Cancelled,         false, false,  true)]
    public void RefreshStateFlags_Covers_All_9_States_With_Correct_Triad(
        TransferState s, bool expRunning, bool expPaused, bool expTerminal)
    {
        var (vm, _) = Build();

        CallRefreshStateFlags(vm, s);

        Assert.Equal(expRunning,  vm.IsRunning);
        Assert.Equal(expPaused,   vm.IsPaused);
        Assert.Equal(expTerminal, vm.IsTerminal);
    }

    [Fact]
    public void Constructor_Init_Flags_From_Task_Initial_State_Transferring()
    {
        var (vm, _) = Build(TransferState.Transferring);
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsPaused);
        Assert.False(vm.IsTerminal);
        Assert.Equal(TransferState.Transferring, vm.State);
        Assert.Equal(0, vm.BytesTransferred);
    }

    [Fact]
    public void Constructor_Init_Flags_From_Task_Initial_State_Paused()
    {
        var (vm, _) = Build(TransferState.Paused);
        Assert.True(vm.IsPaused);
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsTerminal);
    }

    [Fact]
    public void Constructor_Init_Flags_From_Task_Initial_State_Completed()
    {
        var (vm, _) = Build(TransferState.Completed);
        Assert.True(vm.IsTerminal);
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsPaused);
    }

    [Fact]
    public void Constructor_Initializes_Properties_From_Task()
    {
        var task = new TransferTaskInfo
        {
            FileId = "f123",
            FileName = "hello.txt",
            TotalBytes = 4096,
            BytesTransferred = 1024,
            State = TransferState.Transferring,
        };
        var vm = new TestableTransferItemViewModel(WeakReferenceMessenger.Default, new NSubstituteEngine(), task);

        Assert.Equal("f123",       vm.FileId);
        Assert.Equal("hello.txt",  vm.FileName);
        Assert.Equal(4096,         vm.TotalBytes);
        Assert.Equal(1024,         vm.BytesTransferred);
        Assert.Equal(25.0,         vm.ProgressPercentage, 6);  // 1024/4096*100 = 25.0
        Assert.Equal("传输中",     vm.StateText); // Transferring
    }

    [Fact]
    public void Switching_From_Transferring_To_Completed_Clears_Running_And_Sets_Terminal()
    {
        var (vm, _) = Build(TransferState.Transferring);
        Assert.True(vm.IsRunning);

        CallRefreshStateFlags(vm, TransferState.Completed);

        Assert.False(vm.IsRunning);
        Assert.False(vm.IsPaused);
        Assert.True(vm.IsTerminal);
    }

    [Fact]
    public void Switching_From_Transferring_To_Paused_Flips_Running_Paused()
    {
        var (vm, _) = Build(TransferState.Transferring);
        CallRefreshStateFlags(vm, TransferState.Paused);
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsPaused);
        Assert.False(vm.IsTerminal);
    }

    [Fact]
    public void Switching_From_Paused_To_Cancelled_Forces_Terminal()
    {
        var (vm, _) = Build(TransferState.Paused);
        CallRefreshStateFlags(vm, TransferState.Cancelled);
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsPaused);
        Assert.True(vm.IsTerminal);
    }

    [Fact]
    public void Disconnected_State_Has_None_Of_Three_Flags_Set()
    {
        // 防御性：断开是"非终态、非运行、非暂停"，留给 UI 自己显示橙色警示
        var (vm, _) = Build(TransferState.Disconnected);
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsPaused);
        Assert.False(vm.IsTerminal);
    }

    [Fact]
    public void WaitingApproval_State_Is_Not_Paused_Or_Running_Or_Terminal()
    {
        var (vm, _) = Build(TransferState.WaitingApproval);
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsPaused);
        Assert.False(vm.IsTerminal);
    }

    [Fact]
    public void Empty_File_Progress_Does_Not_DivideByZero()
    {
        var task = new TransferTaskInfo
        {
            FileId = "f-zero",
            FileName = "empty.txt",
            TotalBytes = 0,
            State = TransferState.Created,
        };
        var vm = new TestableTransferItemViewModel(WeakReferenceMessenger.Default, new NSubstituteEngine(), task);
        // ProgressPercentage 不崩溃、在 [0, 100]
        Assert.InRange(vm.ProgressPercentage, 0, 100);
    }

    // ---- helpers ----
    private sealed class NSubstituteEngine : ITransferEngine
    {
        public event EventHandler<Core.Messaging.TransferStatusChangedMessage>? StateChanged;
        public Task ApplyControlAsync(string fileId, TransferAction action) => Task.CompletedTask;
        public Task CancelAsync(string fileId) => Task.CompletedTask;
        public Task<TransferTaskInfo> CreateSendTaskAsync(string filePath, DeviceNode peer, CancellationToken ct = default) => throw new System.NotSupportedException();
        public TransferTaskInfo? GetTask(string fileId) => null;
        public IReadOnlyList<TransferTaskInfo> GetTasks() => Array.Empty<TransferTaskInfo>();
        public Task PauseAsync(string fileId) => Task.CompletedTask;
        public Task<PrepareResponse> RegisterReceiveTaskAsync(PrepareRequest prepare, DeviceNode peer) => throw new System.NotSupportedException();
        public Task ResumeAsync(string fileId) => Task.CompletedTask;
        public Task StartSendAsync(string fileId, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<bool> WriteChunkAsync(string fileId, int chunkIndex, int totalChunks, ReadOnlyMemory<byte> data) => throw new System.NotSupportedException();
    }
}
