using System.ComponentModel;

namespace FileTransferApp.Services;

/// <summary>
/// 本地化服务契约（便于依赖注入与单元测试；实现见 <see cref="LocalizationService"/>）。
/// 现有代码仍大量通过 <c>LocalizationService.Instance</c> 使用；新代码/可测代码优先注入本接口。
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>当前语言代码（zh-CN / en-US / pl-PL）。</summary>
    string CurrentLanguage { get; }

    /// <summary>索引器：XAML 绑定 Path=[key] 走此入口。</summary>
    string this[string key] { get; }

    /// <summary>获取本地化字符串，缺失时返回 key 本身。</summary>
    string GetString(string key);

    /// <summary>获取带格式参数的本地化字符串。</summary>
    string Format(string key, params object[] args);

    /// <summary>指定 key 当前是否有可用文案。</summary>
    bool HasString(string key);

    /// <summary>切换语言并触发 UI 刷新。</summary>
    void SetLanguage(string code);
}
