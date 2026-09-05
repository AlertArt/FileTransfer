using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 传输任务保活协调器 <see cref="TransferKeepAliveCoordinator"/> 单元测试。
/// 覆盖：单任务生命周期、多任务并发、暂停/失败/取消终态、幂等去重、Reset、RemoveTask。
/// 通过 <see cref="RecordingKeepAliveService"/> 记录 Start/Stop 调用次数与参数。
/// </summary>
public class TransferKeepAliveCoordinatorTests
{
    private const string Title = "FileTransferApp";
    private const string Content = "正在传输文件，保持服务运行";

    private static (TransferKeepAliveCoordinator Coordinator, RecordingKeepAliveService Fake) New()
    {
        var fake = new RecordingKeepAliveService();
        return (new TransferKeepAliveCoordinator(fake), fake);
    }

    // -------- 初始状态 --------

    [Fact]
    public void New_Coordinator_Is_Not_Started_And_Has_No_Tasks()
    {
        var (c, _) = New();

        Assert.False(c.IsStarted);
        Assert.False(c.HasActiveTasks);
        Assert.Equal(0, c.TrackedTaskCount);
    }

    // -------- 单任务生命周期 --------

    [Fact]
    public void Single_Task_First_Active_State_Triggers_Start_Exactly_Once()
    {
        var (c, fake) = New();
        var fileId = "task-1";

        c.OnTaskStateChanged(fileId, TransferState.Preparing, Title, Content);
        Assert.Equal(1, fake.StartCallCount);
        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted);

        // 同任务继续活动态变化：不应重复 Start
        c.OnTaskStateChanged(fileId, TransferState.Transferring, Title, Content);
        Assert.Equal(1, fake.StartCallCount);
        Assert.True(c.IsStarted);

        // 暂停仍是活动态：不应 Stop
        c.OnTaskStateChanged(fileId, TransferState.Paused, Title, Content);
        Assert.Equal(1, fake.StartCallCount);
        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted);
    }

    [Fact]
    public void Single_Task_Terminal_State_Triggers_Stop_Exactly_Once()
    {
        var (c, fake) = New();
        var fileId = "task-1";

        c.OnTaskStateChanged(fileId, TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged(fileId, TransferState.Completed, Title, Content);

        Assert.Equal(1, fake.StartCallCount);
        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
        Assert.False(c.HasActiveTasks);
    }

    [Fact]
    public void Single_Task_Failed_Triggers_Stop()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("t", TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged("t", TransferState.Failed, Title, Content);

        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    [Fact]
    public void Single_Task_Cancelled_Triggers_Stop()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("t", TransferState.Preparing, Title, Content);
        c.OnTaskStateChanged("t", TransferState.Cancelled, Title, Content);

        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    // -------- 多任务并发 --------

    [Fact]
    public void Multiple_Tasks_Start_Only_Once_And_Stop_Only_When_All_Done()
    {
        var (c, fake) = New();

        // 任务 A 进入活动态
        c.OnTaskStateChanged("A", TransferState.Transferring, Title, Content);
        Assert.Equal(1, fake.StartCallCount);

        // 任务 B 进入活动态：不重复 Start
        c.OnTaskStateChanged("B", TransferState.Transferring, Title, Content);
        Assert.Equal(1, fake.StartCallCount);
        Assert.True(c.IsStarted);

        // 任务 A 完成：B 仍活动，不应 Stop
        c.OnTaskStateChanged("A", TransferState.Completed, Title, Content);
        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted);

        // 任务 B 完成：所有任务终止，Stop
        c.OnTaskStateChanged("B", TransferState.Completed, Title, Content);
        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    [Fact]
    public void Multiple_Tasks_One_Paused_Other_Running_Does_Not_Stop()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("A", TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged("B", TransferState.Transferring, Title, Content);

        c.OnTaskStateChanged("A", TransferState.Paused, Title, Content);
        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted);

        c.OnTaskStateChanged("B", TransferState.Paused, Title, Content);
        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted); // 两个暂停任务都仍是活动态
    }

    // -------- OnTaskCompleted --------

    [Fact]
    public void OnTaskCompleted_Success_Marks_Task_As_Completed_And_Stops_If_All_Done()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("t", TransferState.Transferring, Title, Content);
        c.OnTaskCompleted("t", success: true, Title, Content);

        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    [Fact]
    public void OnTaskCompleted_Failure_Marks_Task_As_Failed_And_Stops_If_All_Done()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("t", TransferState.Preparing, Title, Content);
        c.OnTaskCompleted("t", success: false, Title, Content);

        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    [Fact]
    public void OnTaskCompleted_Does_Not_Stop_When_Other_Tasks_Still_Active()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("A", TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged("B", TransferState.Transferring, Title, Content);

        c.OnTaskCompleted("A", success: true, Title, Content);

        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted);
    }

    // -------- 幂等去重 --------

    [Fact]
    public void Repeated_Same_Active_State_Does_Not_Duplicate_Start()
    {
        var (c, fake) = New();

        for (int i = 0; i < 5; i++)
            c.OnTaskStateChanged("t", TransferState.Transferring, Title, Content);

        Assert.Equal(1, fake.StartCallCount);
        Assert.Equal(0, fake.StopCallCount);
    }

    [Fact]
    public void Repeated_Same_Terminal_State_Does_Not_Duplicate_Stop()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("t", TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged("t", TransferState.Completed, Title, Content);

        for (int i = 0; i < 5; i++)
            c.OnTaskStateChanged("t", TransferState.Completed, Title, Content);

        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    // -------- 边界 --------

    [Fact]
    public void First_State_Change_Is_Terminal_Does_Not_Start_Or_Stop()
    {
        // 任务一上来就标记为完成（异常但可能场景）：不应触发 Start，也不应误触发 Stop
        var (c, fake) = New();

        c.OnTaskStateChanged("t", TransferState.Completed, Title, Content);

        Assert.Equal(0, fake.StartCallCount);
        Assert.Equal(0, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    [Fact]
    public void StartKeepAlive_Receives_Provided_Title_And_Content()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("t", TransferState.Transferring, "我的标题", "我的内容");

        Assert.Equal("我的标题", fake.LastStartTitle);
        Assert.Equal("我的内容", fake.LastStartContent);
    }

    // -------- RemoveTask --------

    [Fact]
    public void RemoveTask_Re_Syncs_And_Stops_If_No_Active_Left()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("A", TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged("B", TransferState.Transferring, Title, Content);

        // 移除活动任务 A：B 仍活动，不应 Stop
        c.RemoveTask("A", Title, Content);
        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted);

        // 移除活动任务 B：无任务，应 Stop
        c.RemoveTask("B", Title, Content);
        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    // -------- Reset --------

    [Fact]
    public void Reset_Stops_KeepAlive_And_Clears_All_State()
    {
        var (c, fake) = New();

        c.OnTaskStateChanged("A", TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged("B", TransferState.Preparing, Title, Content);

        c.Reset();

        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
        Assert.Equal(0, c.TrackedTaskCount);
        Assert.False(c.HasActiveTasks);
    }

    [Fact]
    public void Reset_When_Not_Started_Does_Not_Call_Stop()
    {
        var (c, fake) = New();

        c.Reset();

        Assert.Equal(0, fake.StopCallCount);
    }

    // -------- 端到端典型周期 --------

    [Fact]
    public void Typical_Send_Lifecycle_Start_And_Stop_Exactly_Once()
    {
        var (c, fake) = New();
        var fileId = "task-1";

        // 真实任务状态变化驱动 Coordinator（不依赖占位 fileId 预启动）
        c.OnTaskStateChanged(fileId, TransferState.Preparing, Title, Content);
        Assert.Equal(1, fake.StartCallCount);

        c.OnTaskStateChanged(fileId, TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged(fileId, TransferState.Paused, Title, Content);
        c.OnTaskStateChanged(fileId, TransferState.Transferring, Title, Content);
        c.OnTaskStateChanged(fileId, TransferState.Completed, Title, Content);

        Assert.Equal(1, fake.StartCallCount);
        Assert.Equal(1, fake.StopCallCount);
    }

    [Fact]
    public void Pending_Placeholder_Task_Never_Terminating_Does_Not_Block_Stop()
    {
        // 回归测试：之前的 bug —— 占位 fileId "__pending__" 永不终结导致
        // Coordinator 误判 HasActiveTasks=true，真实任务终态后 Stop 不触发。
        // 当前契约：所有跟踪的 fileId 都必须经过终态才会 Stop。
        // 若调用方残留占位任务，应使用 RemoveTask 清理。
        var (c, fake) = New();

        c.OnTaskStateChanged("__pending__", TransferState.Preparing, Title, Content);
        c.OnTaskStateChanged("real", TransferState.Preparing, Title, Content);
        c.OnTaskStateChanged("real", TransferState.Completed, Title, Content);

        // 占位任务仍活动，Stop 不应触发
        Assert.Equal(1, fake.StartCallCount);
        Assert.Equal(0, fake.StopCallCount);
        Assert.True(c.IsStarted);

        // 调用方清理占位任务后才 Stop
        c.RemoveTask("__pending__", Title, Content);
        Assert.Equal(1, fake.StopCallCount);
        Assert.False(c.IsStarted);
    }

    [Fact]
    public void Multiple_Send_Cycles_Each_Start_And_Stop()
    {
        var (c, fake) = New();

        for (int i = 0; i < 3; i++)
        {
            var id = $"task-{i}";
            c.OnTaskStateChanged(id, TransferState.Transferring, Title, Content);
            c.OnTaskStateChanged(id, TransferState.Completed, Title, Content);
        }

        Assert.Equal(3, fake.StartCallCount);
        Assert.Equal(3, fake.StopCallCount);
    }

    /// <summary>记录 Start/Stop 调用次数与最后传入参数的 IPlatformKeepAliveService 替身</summary>
    private sealed class RecordingKeepAliveService : IPlatformKeepAliveService
    {
        public int StartCallCount { get; private set; }
        public int StopCallCount { get; private set; }
        public string? LastStartTitle { get; private set; }
        public string? LastStartContent { get; private set; }

        public void StartKeepAlive(string title, string content)
        {
            StartCallCount++;
            LastStartTitle = title;
            LastStartContent = content;
        }

        public void StopKeepAlive()
        {
            StopCallCount++;
        }
    }
}
