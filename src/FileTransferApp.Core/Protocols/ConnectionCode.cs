using System.Net;
using FileTransferApp.Core.Models;

namespace FileTransferApp.Core.Protocols;

/// <summary>
/// 连接码（二维码配对用）：把"如何连上本机"编码成一段可扫码 / 可粘贴的文本。
/// 格式：<c>fta://connect?ip=192.168.1.5&amp;port=53318&amp;id=&lt;deviceId&gt;&amp;name=&lt;urlencoded&gt;&amp;v=2</c>
/// 兼容：也接受裸 <c>ip</c> / <c>ip:port</c>（手动输入的地址）。
/// Android 端已注册 <c>fta://connect</c> 深链，用系统相机扫到该二维码可直接唤起本应用。
/// </summary>
public static class ConnectionCode
{
    public const string Scheme = "fta";
    public const string Host = "connect";

    /// <summary>把本机节点编码为连接码（IpAddress 需已填好，通常取本机 LAN IP）。</summary>
    public static string Build(DeviceNode self)
    {
        var ip = self.IpAddress?.ToString() ?? string.Empty;
        var id = Uri.EscapeDataString(self.DeviceId ?? string.Empty);
        var name = Uri.EscapeDataString(self.DeviceName ?? string.Empty);
        return $"{Scheme}://{Host}?ip={ip}&port={self.Port}&id={id}&name={name}&v={self.ProtocolVersion}";
    }

    /// <summary>解析连接码为设备节点；失败返回 false。兼容 fta:// 连接码与裸 ip/ip:port。</summary>
    public static bool TryParse(string? text, out DeviceNode? node)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        try
        {
            if (text.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(text);
                var q = ParseQuery(uri.Query);
                if (!q.TryGetValue("ip", out var ipStr) || !IPAddress.TryParse(ipStr, out var ip)) return false;

                var port = q.TryGetValue("port", out var pStr) && int.TryParse(pStr, out var p) && p > 0
                    ? p : ProtocolConstants.TransferPort;
                var id = q.TryGetValue("id", out var idStr) && !string.IsNullOrWhiteSpace(idStr)
                    ? idStr : $"code:{ip}:{port}";
                var name = q.TryGetValue("name", out var nStr) && !string.IsNullOrWhiteSpace(nStr)
                    ? nStr : $"手动直连 {ip}:{port}";
                var ver = q.TryGetValue("v", out var vStr) && int.TryParse(vStr, out var v) && v > 0
                    ? v : ProtocolConstants.ProtocolVersion;

                node = new DeviceNode
                {
                    DeviceId = id,
                    DeviceName = name,
                    DeviceType = DeviceType.Unknown,
                    IpAddress = ip,
                    Port = port,
                    ProtocolVersion = ver,
                    LastSeenUtc = DateTime.UtcNow,
                };
                return true;
            }

            // 兼容裸 ip / ip:port（与"手动直连"输入一致）
            var parts = text.Split(':', 2);
            if (IPAddress.TryParse(parts[0], out var bareIp))
            {
                var barePort = parts.Length == 2 && int.TryParse(parts[1], out var bp) && bp > 0
                    ? bp : ProtocolConstants.TransferPort;
                node = new DeviceNode
                {
                    DeviceId = $"code:{bareIp}:{barePort}",
                    DeviceName = $"手动直连 {bareIp}:{barePort}",
                    DeviceType = DeviceType.Unknown,
                    IpAddress = bareIp,
                    Port = barePort,
                    ProtocolVersion = ProtocolConstants.ProtocolVersion,
                    LastSeenUtc = DateTime.UtcNow,
                };
                return true;
            }
        }
        catch { /* 非法文本按解析失败处理 */ }

        return false;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[key] = val;
        }
        return result;
    }
}
