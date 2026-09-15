using System.Diagnostics;
using System.Reflection;
using Avalonia.Media;

namespace FileTransferApp;

/// <summary>
/// �?Avalonia 12 中，FontManagerOptions（含 FontFallbacks）通过 AvaloniaLocator 注入�?/// �?AvaloniaLocator 被标�?[PrivateApi]，编译器层面无法直接访问�?/// 由于 FontManager 仅在首次文本渲染时才读取�?options，本注册器必须在 App.Initialize()
/// （渲染发生之前）把含 CJK 回退字体�?FontManagerOptions 注入 locator�?/// 否则 Android 端缺失中文字形回退会导致中文渲染成豆腐块（□□□）�?/// 注入方式�?Avalonia 框架自身读取逻辑一致（Avalonia.Media.FontManager 构造时
/// �?AvaloniaLocator.Current.GetService&lt;FontManagerOptions&gt;() 取值）�?/// </summary>
internal static class FontFallbackRegistrar
{
    public static void Register()
    {
        try
        {
            var avBase = typeof(FontFamily).Assembly;
            var locatorType = avBase.GetType("Avalonia.AvaloniaLocator");
            if (locatorType is null)
            {
                Trace.WriteLine("FTA.FONT: locatorType not found");
                return;
            }

            var currentMutableProp = locatorType.GetProperty("CurrentMutable",
                BindingFlags.Public | BindingFlags.Static);
            if (currentMutableProp?.GetValue(null) is not object locator)
            {
                Trace.WriteLine("FTA.FONT: CurrentMutable not found");
                return;
            }

            var bindMethod = locatorType.GetMethod("Bind")?.MakeGenericMethod(typeof(FontManagerOptions));
            if (bindMethod?.Invoke(locator, null) is not object helper)
            {
                Trace.WriteLine("FTA.FONT: Bind<FontManagerOptions> failed");
                return;
            }

            var toConstant = helper.GetType()
                .GetMethod("ToConstant")
                ?.MakeGenericMethod(typeof(FontManagerOptions));
            if (toConstant is null)
            {
                Trace.WriteLine("FTA.FONT: ToConstant not found");
                return;
            }

            toConstant.Invoke(helper, new object[]
            {
                new FontManagerOptions
                {
                    FontFallbacks = new[]
                    {
                        new FontFallback
                        {
                            FontFamily = new FontFamily("avares://FileTransferApp/Assets/Fonts/NotoSansCJKsc-Regular.otf#Noto Sans CJK SC"),
                        },
                    },
                },
            });
            Trace.WriteLine("FTA.FONT: FontManagerOptions registered (Noto Sans CJK SC fallback)");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FTA.FONT: registrar failed: {ex}");
        }
    }

    /// <summary>
    /// 在首次渲染后才调用：验证内嵌中文字体能否�?FontManager 解析�?    /// �?URI/资源路径写错，桌面端日志就会暴露，无需�?Android 平板上才发现�?    /// </summary>
    public static void VerifyEmbeddedFont()
    {
        try
        {
            var family = new FontFamily("avares://FileTransferApp/Assets/Fonts/NotoSansCJKsc-Regular.otf#Noto Sans CJK SC");
            var resolved = FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var gt);
            Trace.WriteLine(resolved && gt is not null
                ? $"FTA.FONT: embedded CJK font resolved OK: {gt.FamilyName}"
                : "FTA.FONT: embedded CJK font RESOLVE FAILED");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FTA.FONT: verify failed: {ex}");
        }
    }
}