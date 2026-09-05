namespace FileTransferApp.Core.Services.Impl;

/// <summary>
/// 平台保活服务的共享状态机与文案回退逻辑。
/// 抽取自 <c>AndroidKeepAliveService</c> / <c>IOSKeepAliveService</c>：
/// 1) 跟踪 Idle ↔ Running 状态切换；
/// 2) 仅在首个 Start 周期标记 <see cref="PromptShown"/>（避免重复弹窗提示用户保持前台）；
/// 3) Stop 后允许下一周期重新提示；
/// 4) 空 title/content 回退到默认值。
/// 平台无关、无副作用，便于在标准 .NET 测试项目下单元测试。
/// </summary>
public sealed class KeepAliveStatus
{
    public bool IsRunning { get; private set; }

    /// <summary>当前周期的标题（已应用回退）</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>当前周期的内容（已应用回退）</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>本周期是否已展示过用户提示</summary>
    public bool PromptShown { get; private set; }

    /// <summary>
    /// 进入运行态。返回本次调用是否应展示用户提示。
    /// 同一运行周期内重复调用不再触发提示；Stop 后再次 Start 重新允许提示。
    /// 空 title/content 会用提供的默认值回退。
    /// </summary>
    public StartResult Start(string title, string content, string defaultTitle, string defaultContent)
    {
        var effectiveTitle = string.IsNullOrWhiteSpace(title) ? defaultTitle : title;
        var effectiveContent = string.IsNullOrWhiteSpace(content) ? defaultContent : content;

        Title = effectiveTitle;
        Content = effectiveContent;

        bool shouldShowPrompt;
        if (!IsRunning)
        {
            // 从 Idle 进入 Running：首次进入本周期
            IsRunning = true;
            shouldShowPrompt = !PromptShown;
            if (shouldShowPrompt) PromptShown = true;
        }
        else
        {
            // 已在 Running 态重复调用：不重复弹窗，仅刷新文案
            shouldShowPrompt = false;
        }

        return new StartResult(shouldShowPrompt, effectiveTitle, effectiveContent);
    }

    /// <summary>停止运行，重置本周期提示标记以允许下次 Start 重新提示</summary>
    public void Stop()
    {
        IsRunning = false;
        PromptShown = false;
    }

    /// <summary>完全重置（测试与初始化场景使用）</summary>
    public void Reset()
    {
        IsRunning = false;
        Title = string.Empty;
        Content = string.Empty;
        PromptShown = false;
    }

    /// <summary>Start 调用结果</summary>
    public readonly record struct StartResult(
        bool ShouldShowPrompt,
        string EffectiveTitle,
        string EffectiveContent);
}
