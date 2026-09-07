using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FileTransferApp.Services;

/// <summary>
/// 多语言服务（单例）。
/// - 从嵌入资源加载 JSON 语言文件（Assets/Localization/{code}.json）
/// - 支持 zh-CN / en-US / pl-PL
/// - 通过 this[key] 索引器获取字符串，缺失 key 时回退到默认语言，再缺失返回 key 本身
/// - SetLanguage(code) 切换语言并触发 PropertyChanged，XAML 绑定自动刷新
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    public const string DefaultLanguage = "zh-CN";

    /// <summary>支持的语言列表：(code, 显示名)</summary>
    public static readonly (string Code, string DisplayName)[] SupportedLanguages =
    {
        ("zh-CN", "简体中文"),
        ("en-US", "English"),
        ("pl-PL", "Polski"),
    };

    private const string SettingsDirName = "FileTransferApp";
    private const string LanguageFileName = "language.txt";

    private readonly Dictionary<string, string> _strings = new();
    private string _currentLanguage = DefaultLanguage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentLanguage
    {
        get => _currentLanguage;
        private set
        {
            if (_currentLanguage == value) return;
            _currentLanguage = value;
            OnPropertyChanged();
        }
    }

    private LocalizationService()
    {
        LoadLanguage(DefaultLanguage);
    }

    /// <summary>索引器：获取本地化字符串。XAML 绑定 Path=[key] 走此索引器。</summary>
    public string this[string key] => GetString(key);

    /// <summary>获取本地化字符串，缺失时返回 key 本身。</summary>
    public string GetString(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        return _strings.TryGetValue(key, out var value) ? value : key;
    }

    /// <summary>获取带格式参数的本地化字符串。</summary>
    public string Format(string key, params object[] args)
    {
        var fmt = GetString(key);
        try { return string.Format(fmt, args); }
        catch { return fmt; }
    }

    /// <summary>指定 key 当前是否有可用的本地化文案（用于错误码等可尖退场景）。</summary>
    public bool HasString(string key)
        => !string.IsNullOrEmpty(key) && _strings.ContainsKey(key);

    /// <summary>切换语言并触发 UI 刷新。</summary>
    public void SetLanguage(string code)
    {
        if (string.IsNullOrEmpty(code)) code = DefaultLanguage;
        if (!SupportedLanguages.Any(l => l.Code == code)) code = DefaultLanguage;
        LoadLanguage(code);
        CurrentLanguage = code;
        PersistLanguage(code);
        // 通知所有绑定刷新：索引器 this[key] 对应 PropertyChanged 名为 "Item[]"
        OnPropertyChanged(string.Empty);
        OnPropertyChanged("Item[]");
    }

    /// <summary>
    /// 应用启动时调用：恢复用户上次保存的语言。
    /// 无保存记录或语言已从支持列表移除时保持默认语言（zh-CN）。
    /// 显式调用而非在构造函数中自动恢复：保证单元测试环境恒为默认语言。
    /// </summary>
    public void InitializePersistedLanguage()
    {
        try
        {
            var path = GetLanguageFilePath();
            if (!File.Exists(path)) return;
            var code = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(code) || !SupportedLanguages.Any(l => l.Code == code)) return;
            LoadLanguage(code);
            CurrentLanguage = code;
        }
        catch
        {
            // 恢复失败时保持默认语言（zh-CN），不影响启动
        }
    }

    private static void PersistLanguage(string code)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SettingsDirName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, LanguageFileName), code);
        }
        catch
        {
            // 持久化写入失败（如沙箱目录只读）不影响运行时
        }
    }

    private static string GetLanguageFilePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SettingsDirName, LanguageFileName);

    private void LoadLanguage(string code)
    {
        _strings.Clear();
        // 先加载默认语言作为兜底
        var defaultDict = LoadFromResource(DefaultLanguage);
        foreach (var kv in defaultDict) _strings[kv.Key] = kv.Value;

        if (code != DefaultLanguage)
        {
            var targetDict = LoadFromResource(code);
            foreach (var kv in targetDict) _strings[kv.Key] = kv.Value;
        }
    }

    private static Dictionary<string, string> LoadFromResource(string code)
    {
        var result = new Dictionary<string, string>();
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"FileTransferApp.Assets.Localization.{code}.json";
        try
        {
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return result;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is not null)
                foreach (var kv in dict) result[kv.Key] = kv.Value;
        }
        catch
        {
            // 语言文件损坏时静默返回空（已由默认语言兜底）
        }
        return result;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
