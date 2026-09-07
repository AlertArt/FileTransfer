using Android.Content;
using AndroidX.Core.Content;

namespace FileTransferApp.Android.Services;

/// <summary>
/// Android 文件打开：通过 FileProvider 生成安全的 content:// URI，
/// 以 Intent.ACTION_VIEW + Intent.GrantReadUriPermission 启动系统默认查看器。
/// </summary>
public sealed class AndroidFileOpenService : FileTransferApp.Services.IFileOpenService
{
    private readonly Context _context;
    private const string AuthoritySuffix = ".fileprovider";

    public AndroidFileOpenService(Context context) => _context = context;

    public Task<bool> OpenFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Task.FromResult(false);

        try
        {
            var javaFile = new Java.IO.File(filePath);
            var uri = FileProvider.GetUriForFile(
                _context,
                _context.PackageName + AuthoritySuffix,
                javaFile);

            var contentResolver = _context.ContentResolver;
            var mime = contentResolver is not null ? contentResolver.GetType(uri!) : "*/*";

            var intent = new Intent(Intent.ActionView)
                .SetDataAndType(uri, mime)
                .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);

            _context.StartActivity(intent);
            return Task.FromResult(true);
        }
        catch
        {
            // 无关联程序 / URI 构造失败 / Activity 不存在 → 静默失败
            return Task.FromResult(false);
        }
    }
}
