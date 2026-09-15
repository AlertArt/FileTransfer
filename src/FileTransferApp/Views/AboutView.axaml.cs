using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using FileTransferApp.Services;

namespace FileTransferApp.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RefreshVersion();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshVersion();
    }

    /// <summary>从程序集解析版本号写入界面。优先 InformationalVersion（含构建元数据），否则 FileVersion。</summary>
    private void RefreshVersion()
    {
        if (VersionText is null) return;
        var asm = Assembly.GetExecutingAssembly();
        string version;
        try
        {
            version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
            var plus = version.IndexOf('+');
            if (plus >= 0) version = version[..plus];
        }
        catch
        {
            version = asm.GetName().Version?.ToString() ?? string.Empty;
        }

        VersionText.Text = string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : LocalizationService.Instance.Format("About.Version", version);
    }
}