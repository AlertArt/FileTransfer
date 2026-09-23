using System.Net;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>连接码（二维码配对）构建/解析：往返一致、兼容裸地址、非法输入拒绝。</summary>
public class ConnectionCodeTests
{
    [Fact]
    public void Build_Then_Parse_RoundTrips_All_Fields()
    {
        var self = new DeviceNode
        {
            DeviceId = "dev-123",
            DeviceName = "我的 手机 📱",
            DeviceType = DeviceType.Android,
            IpAddress = IPAddress.Parse("192.168.1.42"),
            Port = 53318,
            ProtocolVersion = 2,
        };

        var code = ConnectionCode.Build(self);
        Assert.StartsWith("fta://connect?", code);

        Assert.True(ConnectionCode.TryParse(code, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("dev-123", parsed!.DeviceId);
        Assert.Equal("我的 手机 📱", parsed.DeviceName);
        Assert.Equal("192.168.1.42", parsed.IpAddress!.ToString());
        Assert.Equal(53318, parsed.Port);
        Assert.Equal(2, parsed.ProtocolVersion);
    }

    [Theory]
    [InlineData("192.168.1.9:53318", "192.168.1.9", 53318)]
    [InlineData("10.0.0.5", "10.0.0.5", 53318)]
    public void Parse_Accepts_Bare_Ip_Or_IpPort(string text, string expectedIp, int expectedPort)
    {
        Assert.True(ConnectionCode.TryParse(text, out var node));
        Assert.Equal(expectedIp, node!.IpAddress!.ToString());
        Assert.Equal(expectedPort, node.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a code")]
    [InlineData("fta://connect?ip=not-an-ip")]
    [InlineData("http://example.com")]
    public void Parse_Rejects_Invalid(string text)
    {
        Assert.False(ConnectionCode.TryParse(text, out var node));
        Assert.Null(node);
    }
}
