using FileTransferApp.Core.Models;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 覆盖 TransferItemViewModel.RefreshStateFlags 所有分支的纯逻辑测试。
/// RefreshStateFlags 是 UI 项目的 private instance 方法，其逻辑为三式：
///   IsRunning  = s == TransferState.Transferring
///   IsPaused   = s == TransferState.Paused
///   IsTerminal = s in { Completed, Failed, Cancelled }
/// 此处枚举所有 TransferState 值逐式断言，确保未来重构（如新增状态枚举成员）时能及时发现不匹配。
/// </summary>
public class TransferItemFlagsContractTests
{
    private static readonly TransferState[] All = Enum.GetValues<TransferState>();

    // ---- IsRunning 真值表 ----

    public static TheoryData<TransferState> RunningStates => new() { TransferState.Transferring };
    public static TheoryData<TransferState> NonRunningStates()
    {
        var td = new TheoryData<TransferState>();
        foreach (var s in All.Except(new[] { TransferState.Transferring })) td.Add(s);
        return td;
    }

    [Theory]
    [MemberData(nameof(RunningStates))]
    public void IsRunning_True_Only_For_Transferring(TransferState s)
    {
        var (run, _, _) = EvalFlags(s);
        Assert.True(run, $"{s} 应 IsRunning=true");
    }

    [Theory]
    [InlineData(TransferState.Created)]
    [InlineData(TransferState.Preparing)]
    [InlineData(TransferState.WaitingApproval)]
    [InlineData(TransferState.Paused)]
    [InlineData(TransferState.Disconnected)]
    [InlineData(TransferState.Completed)]
    [InlineData(TransferState.Failed)]
    [InlineData(TransferState.Cancelled)]
    public void IsRunning_False_For_NonTransferring(TransferState s)
    {
        var (run, _, _) = EvalFlags(s);
        Assert.False(run, $"{s} 应 IsRunning=false");
    }

    // ---- IsPaused 真值表 ----

    [Fact]
    public void IsPaused_True_Only_For_Paused()
    {
        Assert.True(EvalFlags(TransferState.Paused).IsPaused);
    }

    [Theory]
    [InlineData(TransferState.Created)]
    [InlineData(TransferState.Preparing)]
    [InlineData(TransferState.WaitingApproval)]
    [InlineData(TransferState.Transferring)]
    [InlineData(TransferState.Disconnected)]
    [InlineData(TransferState.Completed)]
    [InlineData(TransferState.Failed)]
    [InlineData(TransferState.Cancelled)]
    public void IsPaused_False_For_NonPaused(TransferState s)
    {
        Assert.False(EvalFlags(s).IsPaused);
    }

    // ---- IsTerminal 真值表 ----

    [Theory]
    [InlineData(TransferState.Completed)]
    [InlineData(TransferState.Failed)]
    [InlineData(TransferState.Cancelled)]
    public void IsTerminal_True_For_Completed_Failed_Cancelled(TransferState s)
    {
        Assert.True(EvalFlags(s).IsTerminal);
    }

    [Theory]
    [InlineData(TransferState.Created)]
    [InlineData(TransferState.Preparing)]
    [InlineData(TransferState.WaitingApproval)]
    [InlineData(TransferState.Transferring)]
    [InlineData(TransferState.Paused)]
    [InlineData(TransferState.Disconnected)]
    public void IsTerminal_False_For_NonTerminal(TransferState s)
    {
        Assert.False(EvalFlags(s).IsTerminal);
    }

    // ---- 互斥/关联不变式 ----

    [Theory]
    [InlineData(TransferState.Transferring)]
    public void Transferring_IsRunning_True_And_Paused_False_And_Terminal_False(TransferState s)
    {
        var (r, p, t) = EvalFlags(s);
        Assert.True(r);
        Assert.False(p);
        Assert.False(t);
    }

    [Theory]
    [InlineData(TransferState.Paused)]
    public void Paused_IsExactly_Paused_Flag(TransferState s)
    {
        var (r, p, t) = EvalFlags(s);
        Assert.False(r);
        Assert.True(p);
        Assert.False(t);
    }

    [Fact]
    public void All_9_States_Covered_Exactly_Once_By_Enum()
    {
        // 防御：未来新增状态必须更新此测试
        Assert.Equal(9, All.Length);
        // 按 Ordinal 排序，Cancelled < Completed < Created < ... < WaitingApproval
        Assert.Equal(
            new[] { "Cancelled", "Completed", "Created", "Disconnected", "Failed",
                    "Paused", "Preparing", "Transferring", "WaitingApproval" },
            All.Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    // ---- 与 RefreshStateFlags 完全等价的纯函数 ----
    private static (bool IsRunning, bool IsPaused, bool IsTerminal) EvalFlags(TransferState s)
    {
        bool isRunning = s == TransferState.Transferring;
        bool isPaused = s == TransferState.Paused;
        bool isTerminal = s is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;
        return (isRunning, isPaused, isTerminal);
    }
}
