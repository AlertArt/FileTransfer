namespace FileTransferApp.Core.Protocols;

/// <summary>
/// 协议能力令牌：随 UDP 心跳广播（<see cref="Models.DeviceNode.Capabilities"/>），
/// 对端据此启用 / 降级特性。向后兼容：旧端无该字段时视为不支持，功能自动降级到基础路径。
/// </summary>
public static class ProtocolCapabilities
{
    /// <summary>v2 配对加密。</summary>
    public const string Pairing = "pairing";
    /// <summary>每片 AES-GCM 完整性（接收方可据此信任未做整文件预哈希的传输）。</summary>
    public const string ChunkGcm = "chunk-gcm";
    /// <summary>断点续传（切片 Bitmap）。</summary>
    public const string Resume = "resume";

    /// <summary>本机声明支持的能力（分号分隔）。</summary>
    public static string Self => $"{Pairing};{ChunkGcm};{Resume}";

    /// <summary>判断能力串是否包含指定能力（分号分隔，大小写不敏感）。</summary>
    public static bool Supports(string? capabilities, string capability)
    {
        if (string.IsNullOrEmpty(capabilities) || string.IsNullOrEmpty(capability)) return false;
        foreach (var token in capabilities.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token.Trim(), capability, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
