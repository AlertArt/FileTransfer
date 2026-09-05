namespace FileTransferApp.Core.Models;

/// <summary>设备类型枚举</summary>
public enum DeviceType
{
    Windows,
    Android,
    iOS,
    macOS,
    Linux,
    Unknown
}

/// <summary>
/// 传输任务有限状态机 (FSM)
/// [Created] -> [Preparing] -> [WaitingApproval] -> [Transferring] <-> [Paused]
/// | v
/// [Disconnected] -> [Completed] / [Failed] / [Cancelled]
/// </summary>
public enum TransferState
{
    Created,
    Preparing,            // 握手中
    WaitingApproval,      // 等待对方同意
    Transferring,
    Paused,
    Disconnected,         // 断开异常
    Completed,
    Failed,
    Cancelled
}

/// <summary>传输方向</summary>
public enum TransferDirection
{
    Send,
    Receive
}

/// <summary>传输控制动作 (对应 /control 接口)</summary>
public enum TransferAction
{
    PAUSE,
    RESUME,
    CANCEL
}
