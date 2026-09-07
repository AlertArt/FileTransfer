using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FileTransferApp.Services;

/// <summary>
/// 主题偏好服务（单例）。
/// - 支持 跟随系统 / 浅色 / 深色，切换时立即覆盖 Application.RequestedThemeVariant
/// - Apply(preference) 持久化选择；InitializePersistedTheme() 在应用启动时恢复
/// - 与 LocalizationService 相同的持久化目录（%LocalAppData%/FileTransferApp/theme.txt）
/// </summary>
public sealed class ThemeService : INotifyPropertyChanged
{
    public static ThemeService Instance { get; } = new();

    /// <summary>可选项：(本地化 key, 主题偏好)。显示名走 LocalizationService，语言切换自动刷新。</summary>
    public static readonly (string Key, ThemePreference Value)[] SupportedThemes =
    {
        ("Theme.System", ThemePreference.System),
        ("Theme.Light", ThemePreference.Light),
        ("Theme.Dark", ThemePreference.Dark),
    };

    private const string SettingsDirName = "FileTransferApp";
    private const string ThemeFileName = "theme.txt";

    private ThemePreference _current = ThemePreference.System;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ThemePreference Current
    {
        get => _current;
        private set
        {
            if (_current == value) return;
            _current = value;
            OnPropertyChanged();
        }
    }

    private ThemeService() { }

    /// <summary>切换主题并立即生效、持久化。</summary>
    public void Apply(ThemePreference preference)
    {
        _current = preference;
        ApplyVariant(preference);
        OnPropertyChanged();
        Persist(preference);
    }

    /// <summary>应用启动时调用：恢复上次保存的主题偏好。失败静默回退到跟随系统。</summary>
    public void InitializePersistedTheme()
    {
        try
        {
            var path = GetThemeFilePath();
            if (!File.Exists(path)) return;
            var raw = File.ReadAllText(path).Trim();
            if (int.TryParse(raw, out var value) && Enum.IsDefined(typeof(ThemePreference), value))
            {
                _current = (ThemePreference)value;
                ApplyVariant(_current);
            }
        }
        catch
        {
            // 恢复失败保持默认（跟随系统），不影响启动
        }
    }

    private static void ApplyVariant(ThemePreference preference)
    {
        if (Avalonia.Application.Current is null) return;
        Avalonia.Application.Current.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => Avalonia.Styling.ThemeVariant.Light,
            ThemePreference.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
    }

    private static void Persist(ThemePreference preference)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SettingsDirName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ThemeFileName), ((int)preference).ToString());
        }
        catch
        {
            // 持久化写入失败（如沙箱目录只读）不影响运行时
        }
    }

    private static string GetThemeFilePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SettingsDirName, ThemeFileName);

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>主题偏好（持久化值为 int）。</summary>
public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}