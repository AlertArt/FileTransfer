namespace FileTransferApp.Services;

/// <summary>
/// 主题服务契约（便于依赖注入与单元测试；实现见 <see cref="ThemeService"/>）。
/// </summary>
public interface IThemeService
{
    /// <summary>当前主题偏好。</summary>
    ThemePreference Current { get; }

    /// <summary>切换主题并立即生效、持久化。</summary>
    void Apply(ThemePreference preference);
}
