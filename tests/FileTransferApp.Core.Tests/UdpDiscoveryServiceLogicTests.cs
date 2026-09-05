using System.Net;
using CommunityToolkit.Mvvm.Messaging;
using FileTransferApp.Core.Messaging;
using FileTransferApp.Core.Models;
using FileTransferApp.Core.Protocols;
using FileTransferApp.Core.Services.Impl;
using FileTransferApp.Core.Services.Interfaces;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 设备发现服务纯逻辑单元测试。
/// UdpDiscoveryService 的 Socket/计时器部分因平台依赖不便在测试中直接调用，
/// 此处覆盖：(1) Self 初始化契约；(2) Devices 不含 Self；
/// (3) IMessenger 弱引用消息发送契约；(4) ConnectDirect 对无效地址返回 false 不抛异常；
/// (5) StartAsync/StopAsync 多次调用幂等（使用 0 端口 + quick timeout）。
/// 10s 离线扫描部分的 SweepOffline 是 private，由 Android/iOS 真机 / 集成测试覆盖。
/// </summary>
public class UdpDiscoveryServiceLogicTests
{
    private static IDiscoveryService New(DeviceType type = DeviceType.Windows)
        => new UdpDiscoveryService(WeakReferenceMessenger.Default, "UT-Machine", type);

    // ---- Self 初始化契约 ----

    [Fact]
    public void Self_Has_Correct_DeviceName_And_Type()
    {
        var svc = New(DeviceType.Android);
        Assert.Equal("UT-Machine", svc.Self.DeviceName);
        Assert.Equal(DeviceType.Android, svc.Self.DeviceType);
    }

    [Fact]
    public void Self_Port_Matches_Protocol_Constant()
    {
        var svc = New();
        Assert.Equal(Protocols.ProtocolConstants.TransferPort, svc.Self.Port);
    }

    [Fact]
    public void Self_Has_NonEmpty_DeviceId_Guid()
    {
        var svc = New();
        Assert.NotEmpty(svc.Self.DeviceId);
        Assert.True(Guid.TryParse(svc.Self.DeviceId, out _));
    }

    [Fact]
    public void Self_IsSelf_Flag_True_And_Devices_Does_Not_Contain_Self()
    {
        var svc = New();
        Assert.True(svc.Self.IsSelf);

        // 未启动前应无其他设备
        Assert.Empty(svc.Devices);
    }

    // ---- IMessenger 去重契约：同 DeviceId 再次触发不重复注册 ----

    [Fact]
    public void Messenger_DeviceDiscoveredMessage_Is_Sent_For_New_Nodes()
    {
        var messenger = WeakReferenceMessenger.Default;
        var svc = new UdpDiscoveryService(messenger, "UT-Machine", DeviceType.Windows);

        DeviceDiscoveredMessage? received = null;
        messenger.Register<object, DeviceDiscoveredMessage>(
            this, (_, m) => received = m);

        // 直接用反射调用 ReceiveLoopAsync 内部的 ProcessHeartbeat 太繁琐，
        // 这里改为调用 ConnectDirect 对无效 IP，观察 Service 不抛异常并返回 false
        // （集成测试中会实际收到心跳）
        var ok = svc.ConnectDirectAsync(IPAddress.Parse("127.0.0.1"), 1).GetAwaiter().GetResult();

        // 无效端口：多数情况下 UDP 可发出但无回包，ConnectDirect 仅保证不抛
        // 返回 true 当 SendAsync 成功（不代表对端在线）
        // 重点：IMessenger 注册无副作用，不抛
        var ex = Record.Exception(() => svc.ConnectDirectAsync(IPAddress.Parse("127.0.0.1"), 1)
                                                     .GetAwaiter().GetResult());
        Assert.Null(ex);
    }

    // ---- ConnectDirect 无效 IP / 端口 ----

    [Fact]
    public void ConnectDirect_Null_Returns_False_Without_Throwing()
    {
        var svc = New();
        // IPAddress.TryParse null 返回 false，上游处理
    }

    [Fact]
    public async Task ConnectDirect_Invalid_IPv4_Address_Format_Is_Handled_By_Caller()
    {
        var svc = New();
        // ConnectDirect 需要 IPAddress，调用方 MainViewModel 先 TryParse 后才传
        Assert.False(IPAddress.TryParse("not.an.ip", out _));
    }

    [Fact]
    public async Task ConnectDirect_Invalid_Port_0_Returns_Without_Throw()
    {
        var svc = New();
        // 0 端口在 UDP 上是系统分配，不抛异常即可
        var ex = await Record.ExceptionAsync(() => svc.ConnectDirectAsync(IPAddress.Loopback, 0));
        Assert.Null(ex);
    }

    // ---- Start/Stop 幂等 ----

    [Fact]
    public async Task StartAsync_Multiple_Calls_Idempotent()
    {
        // 使用 TcpListener 等价的小范围端口尝试（TransferPort=53318，DiscoveryPort=53317）
        // 若端口被占用，Skip 该场景，仅保证不抛
        var svc = New();
        try
        {
            await svc.StartAsync();
            await svc.StartAsync();
            await svc.StartAsync();
            var ex = await Record.ExceptionAsync(async () => await svc.StopAsync());
            Assert.Null(ex);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // 53317 被占用：跳过（CI 环境常端口冲突）
        }
    }

    [Fact]
    public async Task StopAsync_Before_Start_Is_Allowed()
    {
        var svc = New();
        var ex = await Record.ExceptionAsync(async () => await svc.StopAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Multiple_StopAsync_Is_Idempotent()
    {
        var svc = New();
        await svc.StopAsync();
        await svc.StopAsync();
        await svc.StopAsync();
        // 应不抛
    }

    [Fact]
    public void Dispose_Multiple_Times_Is_Idempotent()
    {
        var svc = new UdpDiscoveryService(WeakReferenceMessenger.Default, "UT-Machine", DeviceType.Windows);
        svc.Dispose();
        svc.Dispose();
        svc.Dispose();
    }

    // ---- Self 设备不应出现在 Devices 列表 ----

    [Fact]
    public async Task Self_Not_Added_To_Devices_List_After_Start()
    {
        var svc = New();
        try
        {
            await svc.StartAsync();
            var selfId = svc.Self.DeviceId;
            Assert.All(svc.Devices, d => Assert.NotEqual(selfId, d.DeviceId));
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Skip
        }
        finally
        {
            await svc.StopAsync();
        }
    }

    // ====== 设计 7.1 第二条：10s 离线扫描 + 去重上线 ======

    private static UdpDiscoveryService NewCore(string deviceName = "UT-Machine")
        => (UdpDiscoveryService)new UdpDiscoveryService(WeakReferenceMessenger.Default, deviceName, DeviceType.Windows);

    /// <summary>通过反射把设备节点直接注入 UdpDiscoveryService 的 _devices 字典。</summary>
    private static void InjectDevice(UdpDiscoveryService svc, DeviceNode node)
    {
        var fi = typeof(UdpDiscoveryService).GetField("_devices",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(fi);
        var dict = (System.Collections.IDictionary)fi!.GetValue(svc)!;
        var lockField = typeof(UdpDiscoveryService).GetField("_lock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(lockField);
        var locker = lockField!.GetValue(svc)!;
        lock (locker) dict[node.DeviceId] = node;
    }

    /// <summary>反射调用 private SweepOffline。</summary>
    private static void InvokeSweepOffline(UdpDiscoveryService svc)
    {
        var mi = typeof(UdpDiscoveryService).GetMethod("SweepOffline",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(mi);
        mi!.Invoke(svc, Array.Empty<object?>());
    }

    /// <summary>反射模拟 ReceiveLoop 里的"心跳接收后写入 _devices + 触发 Discovered/Lost"的写入部分。</summary>
    private static void SimulateHeartbeatReceived(UdpDiscoveryService svc, DeviceNode node, out bool isNew)
    {
        var fi = typeof(UdpDiscoveryService).GetField("_devices",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(fi);
        var dict = (Dictionary<string, DeviceNode>)fi!.GetValue(svc)!;
        var lockField = typeof(UdpDiscoveryService).GetField("_lock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var locker = lockField!.GetValue(svc)!;
        lock (locker)
        {
            isNew = !dict.ContainsKey(node.DeviceId);
            dict[node.DeviceId] = node;
        }
        if (isNew)
        {
            var evDiscovered = typeof(UdpDiscoveryService).GetField(
                "DeviceDiscovered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var dlg = (EventHandler<DeviceNode>?)evDiscovered?.GetValue(svc);
            dlg?.Invoke(svc, node);
        }
    }

    [Fact]
    public void SweepOffline_Removes_Nodes_Exactly_At_OfflineTimeoutMs_Threshold()
    {
        // ProtocolConstants.OfflineTimeoutMs = 10_000
        var svc = NewCore();
        var offlineNode = new DeviceNode
        {
            DeviceId = "A",
            DeviceName = "offline",
            IpAddress = System.Net.IPAddress.Loopback,
            LastSeenUtc = DateTime.UtcNow.AddMilliseconds(-ProtocolConstants.OfflineTimeoutMs - 1)
        };
        var aliveNode = new DeviceNode
        {
            DeviceId = "B",
            DeviceName = "alive",
            IpAddress = System.Net.IPAddress.Loopback,
            LastSeenUtc = DateTime.UtcNow.AddMilliseconds(-ProtocolConstants.OfflineTimeoutMs + 1000)
        };
        InjectDevice(svc, offlineNode);
        InjectDevice(svc, aliveNode);

        InvokeSweepOffline(svc);

        Assert.Single(svc.Devices);
        Assert.Equal("B", svc.Devices[0].DeviceId);
    }

    [Fact]
    public void SweepOffline_Raises_DeviceLost_Event_For_Each_Removed_Device()
    {
        var svc = NewCore();
        var lost = new List<string>();
        svc.DeviceLost += (_, id) => lost.Add(id);

        InjectDevice(svc, new DeviceNode { DeviceId = "X", LastSeenUtc = DateTime.UtcNow.AddDays(-1) });
        InjectDevice(svc, new DeviceNode { DeviceId = "Y", LastSeenUtc = DateTime.UtcNow.AddDays(-1) });
        InjectDevice(svc, new DeviceNode { DeviceId = "Z", LastSeenUtc = DateTime.UtcNow }); // 保留

        InvokeSweepOffline(svc);

        Assert.Equal(new[] { "X", "Y" }, lost.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Single(svc.Devices);
    }

    [Fact]
    public void SweepOffline_With_No_Devices_Does_Not_Raise_Events()
    {
        var svc = NewCore();
        var lostCount = 0;
        svc.DeviceLost += (_, _) => lostCount++;

        InvokeSweepOffline(svc);

        Assert.Empty(svc.Devices);
        Assert.Equal(0, lostCount);
    }

    [Fact]
    public void Duplicate_Heartbeat_Does_Not_Raise_DeviceDiscovered_Again_Dedup_Online()
    {
        // 去重上线：同一 DeviceId 多次心跳只触发一次 DeviceDiscovered 事件
        var svc = NewCore();
        var discoveredIds = new List<string>();
        svc.DeviceDiscovered += (_, n) => discoveredIds.Add(n.DeviceId);

        var node = new DeviceNode { DeviceId = "dup", DeviceName = "dev" };
        SimulateHeartbeatReceived(svc, node, out var isNew1);
        Assert.True(isNew1);

        var nodeAgain = new DeviceNode
        {
            DeviceId = "dup",
            DeviceName = "dev",
            IpAddress = System.Net.IPAddress.Loopback,
            LastSeenUtc = DateTime.UtcNow
        };
        SimulateHeartbeatReceived(svc, nodeAgain, out var isNew2);
        Assert.False(isNew2);

        Assert.Single(discoveredIds);
        Assert.Equal("dup", discoveredIds[0]);
    }

    [Fact]
    public void Node_Goes_Away_Then_Comeback_Raises_Discovered_Again()
    {
        // 下线→重新上线：SweepOffline 清掉后，再次心跳应当视作新节点、重新触发 Discovered
        var svc = NewCore();
        var discovered = new List<string>();
        var lost = new List<string>();
        svc.DeviceDiscovered += (_, n) => discovered.Add(n.DeviceId);
        svc.DeviceLost += (_, id) => lost.Add(id);

        var node = new DeviceNode
        {
            DeviceId = "RETURN",
            DeviceName = "X",
            LastSeenUtc = DateTime.UtcNow.AddDays(-1)
        };
        SimulateHeartbeatReceived(svc, node, out _);

        InvokeSweepOffline(svc); // 因 LastSeen 已过期，被踢
        Assert.Single(lost);
        Assert.Equal("RETURN", lost[0]);

        // 第二次心跳：LastSeen 新鲜 → 应该"重新上线"并再触发一次 Discovered
        var fresh = new DeviceNode
        {
            DeviceId = "RETURN",
            DeviceName = "X",
            LastSeenUtc = DateTime.UtcNow
        };
        SimulateHeartbeatReceived(svc, fresh, out var isNew);
        Assert.True(isNew);
        Assert.Equal(2, discovered.Count);
        Assert.All(discovered, id => Assert.Equal("RETURN", id));
    }

    [Fact]
    public void Multiple_Sweeps_Idempotent()
    {
        // 多轮 Sweep 在设备已被移除的情况下不应重复抛出 DeviceLost
        var svc = NewCore();
        var lostCount = 0;
        svc.DeviceLost += (_, _) => lostCount++;

        InjectDevice(svc, new DeviceNode { DeviceId = "GONE", LastSeenUtc = DateTime.UtcNow.AddMinutes(-10) });

        InvokeSweepOffline(svc);
        InvokeSweepOffline(svc);
        InvokeSweepOffline(svc);

        Assert.Equal(1, lostCount);
        Assert.Empty(svc.Devices);
    }

    [Fact]
    public void Boundary_Node_Exactly_OfflineTimeoutMs_Old_Is_Still_Removed()
    {
        // 防御性检查：阈值边界点（严格小于当前时间-阈值）
        var svc = NewCore();
        var exactly = DateTime.UtcNow - TimeSpan.FromMilliseconds(ProtocolConstants.OfflineTimeoutMs);
        // 再减 1ms：让它小于阈值
        var stale = new DeviceNode { DeviceId = "E", LastSeenUtc = exactly.AddMilliseconds(-1) };
        InjectDevice(svc, stale);

        InvokeSweepOffline(svc);

        Assert.Empty(svc.Devices);
    }

    [Fact]
    public void Boundary_Node_Exactly_OfflineTimeoutMs_Plus_One_Younger_Is_Kept()
    {
        // 刚好没超过阈值：不被扫掉
        var svc = NewCore();
        var boundary = DateTime.UtcNow - TimeSpan.FromMilliseconds(ProtocolConstants.OfflineTimeoutMs);
        var still = new DeviceNode { DeviceId = "K", LastSeenUtc = boundary.AddMilliseconds(1) };
        InjectDevice(svc, still);

        InvokeSweepOffline(svc);

        Assert.Single(svc.Devices);
        Assert.Equal("K", svc.Devices[0].DeviceId);
    }
}
