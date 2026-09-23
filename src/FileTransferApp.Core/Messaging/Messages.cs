using CommunityToolkit.Mvvm.Messaging.Messages;
using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Messaging;

// ---- 设备发现/离线事件 ----
public record DeviceDiscoveredMessage(DeviceNode Device);
public record DeviceLostMessage(string DeviceId);

/// <summary>已知设备的地址/名称发生变化（IP 漂移、重连、改名）。UI 据此就地刷新列表，避免显示陈旧 IP。</summary>
public record DeviceUpdatedMessage(DeviceNode Device);

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
