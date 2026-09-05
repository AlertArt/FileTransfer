using FileTransferApp.Core.Services.Impl;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 平台保活服务共享状态机 <see cref="KeepAliveStatus"/> 单元测试。
/// 覆盖：Idle/Running 状态切换、提示去重逻辑、文案回退、Stop 幂等性、Reset。
/// </summary>
public class KeepAliveStatusTests
{
    private const string DefaultTitle = "FileTransferApp";
    private const string DefaultContent = "正在保持传输服务运行";
    private const string AppTitle = "传输中";
    private const string AppContent = "正在向 MacBook-Pro 发送";

    private static KeepAliveStatus New() => new();

    // -------- 初始状态 --------

    [Fact]
    public void New_Status_Is_Idle_With_Empty_Text_And_No_Prompt()
    {
        var s = New();

        Assert.False(s.IsRunning);
        Assert.Equal(string.Empty, s.Title);
        Assert.Equal(string.Empty, s.Content);
        Assert.False(s.PromptShown);
    }

    // -------- Start：状态切换 + 首次提示 --------

    [Fact]
    public void Start_From_Idle_Sets_Running_And_Requests_Prompt_First_Time()
    {
        var s = New();

        var result = s.Start(AppTitle, AppContent, DefaultTitle, DefaultContent);

        Assert.True(s.IsRunning);
        Assert.True(result.ShouldShowPrompt);
        Assert.True(s.PromptShown);
        Assert.Equal(AppTitle, s.Title);
        Assert.Equal(AppContent, s.Content);
        Assert.Equal(AppTitle, result.EffectiveTitle);
        Assert.Equal(AppContent, result.EffectiveContent);
    }

    [Fact]
    public void Start_While_Running_Does_Not_Request_Prompt_Again()
    {
        // 关键契约：同一运行周期内多次 Start（如进度更新触发）不应重复弹窗
        var s = New();
        s.Start(AppTitle, AppContent, DefaultTitle, DefaultContent);

        var result = s.Start("进度 50%", "100MB/s", DefaultTitle, DefaultContent);

        Assert.True(s.IsRunning);
        Assert.False(result.ShouldShowPrompt);
        Assert.True(s.PromptShown); // 整个周期内 PromptShown 仍为 true
    }

    [Fact]
    public void Start_While_Running_Still_Refreshes_Text()
    {
        // 不重复弹窗，但每次 Start 都应刷新文案（用于通知更新）
        var s = New();
        s.Start(AppTitle, AppContent, DefaultTitle, DefaultContent);

        s.Start("进度 50%", "100MB/s", DefaultTitle, DefaultContent);

        Assert.Equal("进度 50%", s.Title);
        Assert.Equal("100MB/s", s.Content);
    }

    // -------- Stop：清理与下一周期 --------

    [Fact]
    public void Stop_Sets_Idle_And_Allows_Next_Start_To_Prompt_Again()
    {
        var s = New();
        s.Start(AppTitle, AppContent, DefaultTitle, DefaultContent);
        Assert.True(s.PromptShown);

        s.Stop();

        Assert.False(s.IsRunning);
        Assert.False(s.PromptShown); // Stop 重置提示标记

        var result = s.Start("传输中", "重启后继续", DefaultTitle, DefaultContent);
        Assert.True(result.ShouldShowPrompt);
        Assert.True(s.PromptShown);
    }

    [Fact]
    public void Stop_Is_Idempotent()
    {
        var s = New();

        s.Stop(); // Idle 状态下 Stop 不应抛异常
        s.Stop();
        s.Stop();

        Assert.False(s.IsRunning);
        Assert.False(s.PromptShown);
    }

    // -------- 文案回退 --------

    [Fact]
    public void Start_Falls_Back_To_Default_When_Title_Is_Null_Or_Whitespace()
    {
        var s = New();

        var r1 = s.Start(null!, AppContent, DefaultTitle, DefaultContent);
        Assert.Equal(DefaultTitle, r1.EffectiveTitle);
        Assert.Equal(DefaultTitle, s.Title);

        s.Stop();
        var r2 = s.Start("   ", AppContent, DefaultTitle, DefaultContent);
        Assert.Equal(DefaultTitle, r2.EffectiveTitle);
        Assert.Equal(DefaultTitle, s.Title);

        s.Stop();
        var r3 = s.Start(string.Empty, AppContent, DefaultTitle, DefaultContent);
        Assert.Equal(DefaultTitle, r3.EffectiveTitle);
    }

    [Fact]
    public void Start_Falls_Back_To_Default_When_Content_Is_Null_Or_Whitespace()
    {
        var s = New();

        var r1 = s.Start(AppTitle, null!, DefaultTitle, DefaultContent);
        Assert.Equal(DefaultContent, r1.EffectiveContent);
        Assert.Equal(DefaultContent, s.Content);

        s.Stop();
        var r2 = s.Start(AppTitle, "   ", DefaultTitle, DefaultContent);
        Assert.Equal(DefaultContent, r2.EffectiveContent);

        s.Stop();
        var r3 = s.Start(AppTitle, string.Empty, DefaultTitle, DefaultContent);
        Assert.Equal(DefaultContent, r3.EffectiveContent);
    }

    [Fact]
    public void Start_Keeps_Provided_Title_When_Non_Whitespace()
    {
        var s = New();

        s.Start("我的标题", "我的内容", DefaultTitle, DefaultContent);

        Assert.Equal("我的标题", s.Title);
        Assert.Equal("我的内容", s.Content);
    }

    // -------- 端到端典型周期 --------

    [Fact]
    public void Typical_Lifecycle_Only_Prompts_Once_Per_Active_Cycle()
    {
        var s = New();

        // 第一次 Start：提示
        Assert.True(s.Start(AppTitle, AppContent, DefaultTitle, DefaultContent).ShouldShowPrompt);
        // 进度更新触发 Start：不提示，仅刷新文案
        Assert.False(s.Start("50%", "100MB/s", DefaultTitle, DefaultContent).ShouldShowPrompt);
        Assert.False(s.Start("90%", "100MB/s", DefaultTitle, DefaultContent).ShouldShowPrompt);

        // Stop → 新周期
        s.Stop();

        // 新周期首次 Start：重新提示
        Assert.True(s.Start("传输中", "续传中", DefaultTitle, DefaultContent).ShouldShowPrompt);
        // 同周期再次 Start：不提示
        Assert.False(s.Start("传输中", "续传中", DefaultTitle, DefaultContent).ShouldShowPrompt);
    }

    [Fact]
    public void Stop_And_Start_Cycle_Can_Repeat_Indefinitely()
    {
        var s = New();

        for (int i = 0; i < 5; i++)
        {
            var r = s.Start($"会话 {i}", $"内容 {i}", DefaultTitle, DefaultContent);
            Assert.True(r.ShouldShowPrompt);          // 每个新周期首次都应提示
            Assert.Equal($"会话 {i}", r.EffectiveTitle);

            Assert.False(s.Start("更新", "更新", DefaultTitle, DefaultContent).ShouldShowPrompt);
            s.Stop();
            Assert.False(s.IsRunning);
        }
    }

    // -------- Reset：完全清理 --------

    [Fact]
    public void Reset_Clears_All_State()
    {
        var s = New();
        s.Start(AppTitle, AppContent, DefaultTitle, DefaultContent);

        s.Reset();

        Assert.False(s.IsRunning);
        Assert.False(s.PromptShown);
        Assert.Equal(string.Empty, s.Title);
        Assert.Equal(string.Empty, s.Content);
    }

    // -------- 边界：默认值也是空 --------

    [Fact]
    public void Start_With_Empty_Defaults_Does_Not_Throw()
    {
        var s = New();

        var r = s.Start(null!, null!, string.Empty, string.Empty);

        Assert.True(s.IsRunning);
        Assert.Equal(string.Empty, r.EffectiveTitle);
        Assert.Equal(string.Empty, r.EffectiveContent);
    }

    [Fact]
    public void StartResult_Carries_EffectiveTitle_And_Content()
    {
        // 验证 StartResult 结构体字段契约（被平台服务用于构造 Intent / UIAlert）
        var s = New();

        var r = s.Start("标题", "内容", DefaultTitle, DefaultContent);

        Assert.Equal("标题", r.EffectiveTitle);
        Assert.Equal("内容", r.EffectiveContent);
        Assert.True(r.ShouldShowPrompt);
    }
}
