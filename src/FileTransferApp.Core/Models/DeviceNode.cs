using System.Net;
using System.Text.Json.Serialization;
using FileTransferApp.Core.Protocols;

namespace FileTransferApp.Core.Models;

/// <summary>
/// 局域网设备节点。通过 UDP 心跳广播交换，含设备 ID/名称/类型/端口。
/// </summary>
public sealed class DeviceNode
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;
    public int Port { get; set; }
    public int ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;

    /// <summary>对端 IP（运行时填充，不参与握手 JSON 序列化）</summary>
    [JsonIgnore]
    public IPAddress? IpAddress { get; set; }

    /// <summary>最后一次收到心跳的时间 (UTC)</summary>
    [JsonIgnore]
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>本机设备标识，不广播</summary>
    [JsonIgnore]
    public bool IsSelf { get; set; }

    public bool IsOnline(TimeSpan? offlineAfter = null)
        => DateTime.UtcNow - LastSeenUtc < (offlineAfter ?? TimeSpan.FromMilliseconds(ProtocolConstants.OfflineTimeoutMs));

    public string EndPoint => $"{IpAddress}:{Port}";

    public override string ToString()
        => $"{DeviceName} ({DeviceType}) @ {EndPoint}";
}
