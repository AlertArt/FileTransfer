using FileTransferApp.Core.Models;
using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 传输任务有限状态机 (FSM) 单元测试。
/// 校验合法流转允许、非法流转拒绝并抛异常、终态识别。
/// 流转图参见 <see cref="TransferStateMachine"/> 顶部注释。
/// </summary>
public class TransferStateMachineTests
{
    // -------- 合法流转 --------

    [Theory]
    [InlineData(TransferState.Created, TransferState.Preparing)]
    [InlineData(TransferState.Created, TransferState.Cancelled)]
    [InlineData(TransferState.Created, TransferState.Failed)]
    [InlineData(TransferState.Preparing, TransferState.WaitingApproval)]
    [InlineData(TransferState.Preparing, TransferState.Transferring)]
    [InlineData(TransferState.Preparing, TransferState.Disconnected)]
    [InlineData(TransferState.Preparing, TransferState.Cancelled)]
    [InlineData(TransferState.Preparing, TransferState.Failed)]
    [InlineData(TransferState.WaitingApproval, TransferState.Transferring)]
    [InlineData(TransferState.WaitingApproval, TransferState.Cancelled)]
    [InlineData(TransferState.WaitingApproval, TransferState.Failed)]
    [InlineData(TransferState.Transferring, TransferState.Paused)]
    [InlineData(TransferState.Transferring, TransferState.Disconnected)]
    [InlineData(TransferState.Transferring, TransferState.Completed)]
    [InlineData(TransferState.Transferring, TransferState.Cancelled)]
    [InlineData(TransferState.Transferring, TransferState.Failed)]
    [InlineData(TransferState.Paused, TransferState.Transferring)]
    [InlineData(TransferState.Paused, TransferState.Disconnected)]
    [InlineData(TransferState.Paused, TransferState.Cancelled)]
    [InlineData(TransferState.Paused, TransferState.Failed)]
    [InlineData(TransferState.Disconnected, TransferState.Transferring)]
    [InlineData(TransferState.Disconnected, TransferState.Paused)]
    [InlineData(TransferState.Disconnected, TransferState.Cancelled)]
    [InlineData(TransferState.Disconnected, TransferState.Failed)]
    public void CanTransition_Allows_Legal_Transitions(TransferState from, TransferState to)
    {
        Assert.True(TransferStateMachine.CanTransition(from, to));
    }

    // -------- 非法流转 --------

    [Theory]
    [InlineData(TransferState.Created, TransferState.Transferring)]   // 跳过 Preparing
    [InlineData(TransferState.Created, TransferState.Completed)]     // 跳过整个流程
    [InlineData(TransferState.Created, TransferState.Paused)]
    [InlineData(TransferState.Created, TransferState.WaitingApproval)]
    [InlineData(TransferState.WaitingApproval, TransferState.Paused)] // 未传输不可暂停
    [InlineData(TransferState.Transferring, TransferState.Created)]  // 不可回溯
    [InlineData(TransferState.Paused, TransferState.Completed)]       // 必须先恢复
    [InlineData(TransferState.Completed, TransferState.Transferring)] // 终态不可再变
    [InlineData(TransferState.Failed, TransferState.Transferring)]
    [InlineData(TransferState.Cancelled, TransferState.Transferring)]
    [InlineData(TransferState.Cancelled, TransferState.Failed)]
    public void CanTransition_Rejects_Illegal_Transitions(TransferState from, TransferState to)
    {
        Assert.False(TransferStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void EnsureTransition_Returns_Target_When_Legal()
    {
        Assert.Equal(
            TransferState.Preparing,
            TransferStateMachine.EnsureTransition(TransferState.Created, TransferState.Preparing));
    }

    [Theory]
    [InlineData(TransferState.Created, TransferState.Completed)]
    [InlineData(TransferState.WaitingApproval, TransferState.Paused)]
    [InlineData(TransferState.Completed, TransferState.Failed)]
    [InlineData(TransferState.Cancelled, TransferState.Created)]
    public void EnsureTransition_Throws_On_Illegal_Transition(TransferState from, TransferState to)
    {
        Assert.Throws<InvalidOperationException>(
            () => TransferStateMachine.EnsureTransition(from, to));
    }

    // -------- 终态判定 --------

    [Theory]
    [InlineData(TransferState.Completed)]
    [InlineData(TransferState.Failed)]
    [InlineData(TransferState.Cancelled)]
    public void IsTerminal_True_For_Terminal_States(TransferState s)
    {
        Assert.True(TransferStateMachine.IsTerminal(s));
    }

    [Theory]
    [InlineData(TransferState.Created)]
    [InlineData(TransferState.Preparing)]
    [InlineData(TransferState.WaitingApproval)]
    [InlineData(TransferState.Transferring)]
    [InlineData(TransferState.Paused)]
    [InlineData(TransferState.Disconnected)]
    public void IsTerminal_False_For_Active_States(TransferState s)
    {
        Assert.False(TransferStateMachine.IsTerminal(s));
    }

    // -------- 端到端典型路径验证 --------

    [Fact]
    public void Typical_Send_Lifecycle_Passes_All_Legal_States()
    {
        // Created -> Preparing -> WaitingApproval -> Transferring -> Completed
        var current = TransferState.Created;
        current = TransferStateMachine.EnsureTransition(current, TransferState.Preparing);
        current = TransferStateMachine.EnsureTransition(current, TransferState.WaitingApproval);
        current = TransferStateMachine.EnsureTransition(current, TransferState.Transferring);
        current = TransferStateMachine.EnsureTransition(current, TransferState.Completed);

        Assert.Equal(TransferState.Completed, current);
        Assert.True(TransferStateMachine.IsTerminal(current));
    }

    [Fact]
    public void Typical_Pause_Resume_Then_Complete_Lifecycle()
    {
        var current = TransferState.Created;
        current = TransferStateMachine.EnsureTransition(current, TransferState.Preparing);
        current = TransferStateMachine.EnsureTransition(current, TransferState.Transferring);
        current = TransferStateMachine.EnsureTransition(current, TransferState.Paused);
        current = TransferStateMachine.EnsureTransition(current, TransferState.Transferring);
        current = TransferStateMachine.EnsureTransition(current, TransferState.Completed);

        Assert.True(TransferStateMachine.IsTerminal(current));
    }

    [Fact]
    public void Disconnected_Can_Recover_To_Transferring_Or_Degrade_To_Cancelled()
    {
        // 网络中断 -> 恢复继续传
        var current = TransferState.Disconnected;
        current = TransferStateMachine.EnsureTransition(current, TransferState.Transferring);
        Assert.Equal(TransferState.Transferring, current);

        // 网络中断 -> 用户取消
        var current2 = TransferState.Disconnected;
        current2 = TransferStateMachine.EnsureTransition(current2, TransferState.Cancelled);
        Assert.True(TransferStateMachine.IsTerminal(current2));
    }

    [Fact]
    public void Any_Active_State_Can_Flow_To_Failed()
    {
        foreach (var from in new[]
                 {
                     TransferState.Created,
                     TransferState.Preparing,
                     TransferState.WaitingApproval,
                     TransferState.Transferring,
                     TransferState.Paused,
                     TransferState.Disconnected
                 })
        {
            Assert.True(
                TransferStateMachine.CanTransition(from, TransferState.Failed),
                $"状态 {from} 应当能流转到 Failed");
        }
    }

    [Fact]
    public void Any_Active_State_Can_Flow_To_Cancelled()
    {
        foreach (var from in new[]
                 {
                     TransferState.Created,
                     TransferState.Preparing,
                     TransferState.WaitingApproval,
                     TransferState.Transferring,
                     TransferState.Paused,
                     TransferState.Disconnected
                 })
        {
            Assert.True(
                TransferStateMachine.CanTransition(from, TransferState.Cancelled),
                $"状态 {from} 应当能流转到 Cancelled");
        }
    }
}
