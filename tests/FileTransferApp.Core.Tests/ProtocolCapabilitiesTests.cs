using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>协议能力协商：令牌解析（分号分隔、大小写不敏感）、空值降级。</summary>
public class ProtocolCapabilitiesTests
{
    [Theory]
    [InlineData("pairing;chunk-gcm;resume", ProtocolCapabilities.ChunkGcm, true)]
    [InlineData("PAIRING;CHUNK-GCM", ProtocolCapabilities.ChunkGcm, true)]
    [InlineData("chunk-gcm", ProtocolCapabilities.Pairing, false)]
    [InlineData("", ProtocolCapabilities.ChunkGcm, false)]
    [InlineData(null, ProtocolCapabilities.ChunkGcm, false)]
    public void Supports_Parses_Tokens(string? caps, string capability, bool expected)
        => Assert.Equal(expected, ProtocolCapabilities.Supports(caps, capability));

    [Fact]
    public void DeviceNode_Supports_Uses_Its_Capabilities()
    {
        var node = new DeviceNode { Capabilities = ProtocolCapabilities.Self };
        Assert.True(node.Supports(ProtocolCapabilities.ChunkGcm));
        Assert.False(new DeviceNode().Supports(ProtocolCapabilities.ChunkGcm)); // 旧端无能力字段 → 降级
    }
}
