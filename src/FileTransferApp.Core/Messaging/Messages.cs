using CommunityToolkit.Mvvm.Messaging.Messages;
using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Messaging;

// ---- 设备发现/离线事件 ----
public record DeviceDiscoveredMessage(DeviceNode Device);
public record DeviceLostMessage(string DeviceId);

// ---- 传输状态更新事件 ----
public record TransferStatusChangedMessage(string FileId, TransferState NewState);

// ---- 实时网速与进度更新事件 ----
public record TransferProgressMessage(
    string FileId,
    long BytesTransferred,
    long TotalBytes,
    double SpeedBytesPerSecond);

// ---- 任务结束事件 ----
public record TransferCompletedMessage(string FileId, bool Success, string? ErrorMessage);

// ---- 任务移除事件（终态后用户手动从列表删除）----
public record TransferTaskRemovedMessage(string FileId);

// ---- 任务缩略图更新事件（接收完成后重新生成缩略图）----
public record TransferThumbnailUpdatedMessage(string FileId, string ThumbnailBase64, string? MimeType);

// ---- 新接收请求（需 UI 审批）事件 ----
public record IncomingTransferRequestMessage(Models.FileMetadata Metadata, string PeerEndPoint);
