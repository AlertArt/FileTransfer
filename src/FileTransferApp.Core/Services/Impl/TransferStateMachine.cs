using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 传输任务有限状态机 (FSM) 校验器，确保多线程并发与网络中断时状态一致性。
/// 流转图：
///   Created -> Preparing -> WaitingApproval -> Transferring <-> Paused
///                                            -> Disconnected -> ...
///   任何活动状态可 -> Failed / Cancelled；Disconnected / Failed 可恢复至 Transferring（断线/失败可重试）
/// </summary>
public static class TransferStateMachine
{
    private static readonly Dictionary<TransferState, HashSet<TransferState>> Transitions = new()
    {
        [TransferState.Created] = new() { TransferState.Preparing, TransferState.Cancelled, TransferState.Failed },
        [TransferState.Preparing] = new() { TransferState.WaitingApproval, TransferState.Transferring, TransferState.Disconnected, TransferState.Cancelled, TransferState.Failed },
        [TransferState.WaitingApproval] = new() { TransferState.Transferring, TransferState.Cancelled, TransferState.Failed },
        [TransferState.Transferring] = new() { TransferState.Paused, TransferState.Disconnected, TransferState.Completed, TransferState.Cancelled, TransferState.Failed },
        [TransferState.Paused] = new() { TransferState.Transferring, TransferState.Disconnected, TransferState.Cancelled, TransferState.Failed },
        [TransferState.Disconnected] = new() { TransferState.Transferring, TransferState.Paused, TransferState.Cancelled, TransferState.Failed },
        [TransferState.Completed] = new(),
        // Failed 可在用户点击"重试"时回到 Transferring（复用续传链路重新握手+补片）
        [TransferState.Failed] = new() { TransferState.Transferring },
        [TransferState.Cancelled] = new(),
    };

    /// <summary>判断能否从 from 流转到 to</summary>
    public static bool CanTransition(TransferState from, TransferState to)
        => Transitions.TryGetValue(from, out var set) && set.Contains(to);

    /// <summary>校验并返回目标状态；非法流转抛出 InvalidOperationException</summary>
    public static TransferState EnsureTransition(TransferState from, TransferState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"非法状态流转: {from} -> {to}");
        return to;
    }

    /// <summary>是否为终态</summary>
    public static bool IsTerminal(TransferState s)
        => s is TransferState.Completed or TransferState.Failed or TransferState.Cancelled;
}
