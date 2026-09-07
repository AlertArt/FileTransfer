using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 文件传输引擎接口。
/// 负责发送侧调度（HTTP Metadata 握手 + 切片推送）与接收侧写入（Pipelines 零拷贝落盘 + 断点续传）。
/// 严格遵循传输任务 FSM 状态机。
/// </summary>
public interface ITransferEngine
{
    // ---- 发送侧 ----
    /// <summary>创建发送任务：计算 SHA256、生成缩略图、构造握手元数据</summary>
    Task<TransferTaskInfo> CreateSendTaskAsync(string filePath, DeviceNode peer, CancellationToken ct = default);

    /// <summary>启动发送：发起 /prepare 握手，根据对端已收切片 Bitmap 推送剩余切片</summary>
    Task StartSendAsync(string fileId, CancellationToken ct = default);

    // ---- 控制命令 (发送方 / 接收方通用) ----
    Task PauseAsync(string fileId);
    Task ResumeAsync(string fileId);
    /// <summary>重试失败任务（Disconnected / Failed）：发送方向重新握手+推送缺失切片，接收方向通知对端重推</summary>
    Task RetryAsync(string fileId);
    Task CancelAsync(string fileId);

    /// <summary>从引擎中移除任务（仅在终态时允许，发送 TransferTaskRemovedMessage）。若任务非终态会先 Cancel。</summary>
    Task RemoveTaskAsync(string fileId);

    // ---- 接收侧（被 TransferServer 调用）----
    /// <summary>接收端收到 /prepare 后注册任务：请求 UI 审批，打开写入流，回传已存在切片 Bitmap 以进行续传</summary>
    Task<PrepareResponse> RegisterReceiveTaskAsync(PrepareRequest prepare, DeviceNode peer);

    /// <summary>接收端写入一个切片（Pipelines 零拷贝落盘），更新进度与 Bitmap</summary>
    Task<bool> WriteChunkAsync(string fileId, int chunkIndex, int totalChunks, ReadOnlyMemory<byte> data);

    /// <summary>接收端应用控制命令</summary>
    Task ApplyControlAsync(string fileId, TransferAction action);

    // ---- 查询 ----
    IReadOnlyList<TransferTaskInfo> GetTasks();
    TransferTaskInfo? GetTask(string fileId);

    /// <summary>状态变更通知（FSM 流转）</summary>
    event EventHandler<TransferStatusChangedMessage>? StateChanged;
}
