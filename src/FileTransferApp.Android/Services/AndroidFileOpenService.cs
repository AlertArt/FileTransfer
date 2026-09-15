using Android.Content;
using Android.Widget;
using AndroidX.Core.Content;
using FileTransferApp.Services;

namespace FileTransferApp.Android.Services;

/// <summary>
/// Android 文件打开：通过 FileProvider 生成安全的 content:// URI，
/// 以 Intent.ACTION_VIEW + Intent.GrantReadUriPermission 启动系统默认查看器。
/// 此前失败被静默吞掉（无任何反馈）；现在增加 FTA.FILE 日志与 Toast 提示，
/// 不再出现“点了没反应”。
/// </summary>
public sealed class AndroidFileOpenService : FileTransferApp.Services.IFileOpenService
{
    private readonly Context _context;
    private const string AuthoritySuffix = ".fileprovider";

    public AndroidFileOpenService(Context context) => _context = context;

    public Task<bool> OpenFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            LogAndToast("OPEN-FILE-MISSING", filePath, i18n("FileOpen.Missing", "文件不存在或已删除"));
            return Task.FromResult(false);
        }

        try
        {
            var javaFile = new Java.IO.File(filePath);
            var uri = FileProvider.GetUriForFile(
                _context,
                _context.PackageName + AuthoritySuffix,
                javaFile);

            var mime = _context.ContentResolver?.GetType(uri!) ?? "*/*";

            var intent = new Intent(Intent.ActionView)
                .SetDataAndType(uri, mime)
                .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);

            var resolve = intent.ResolveActivity(_context.PackageManager!);
            if (resolve is null)
            {
                LogAndToast($"OPEN-NO-VIEWER mime={mime}", filePath, i18n("FileOpen.NoViewer", "无应用可打开该文件"));
                return Task.FromResult(false);
            }

            _context.StartActivity(intent);
            global::Android.Util.Log.Info("FTA.FILE", $"OPEN-OK mime={mime} path={filePath}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LogAndToast($"OPEN-EX {ex.Message}", filePath, i18n("FileOpen.Failed", "打开文件失败"));
            return Task.FromResult(false);
        }
    }

    private static void LogAndToast(string what, string filePath, string message)
    {
        global::Android.Util.Log.Warn("FTA.FILE", $"{what} path={filePath}");
        try
        {
            Toast.MakeText(global::Android.App.Application.Context, message, ToastLength.Short)?.Show();
        }
        catch
        {
            // Toast 不可用不影响返回值
        }
    }

    private static string i18n(string key, string fallback)
    {
        try
        {
            var value = LocalizationService.Instance.GetString(key);
            return string.IsNullOrEmpty(value) || value == key ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }
}