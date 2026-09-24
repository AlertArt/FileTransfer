using Android.Content;
using Android.OS;
using FileTransferApp.Core.Services.Interfaces;

namespace FileTransferApp.Android.Services;

/// <summary>
/// Android 电池优化白名单服务：检查是否已豁免，并引导用户在系统弹窗中把本应用加入"不优化"名单。
/// 需在 Manifest 声明 <c>REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c>（侧载分发适用）。
/// </summary>
public sealed class AndroidBatteryOptimizationService : IBatteryOptimizationService
{
    private readonly Context _context;

    public AndroidBatteryOptimizationService(Context context) => _context = context;

    public bool IsExempt
    {
        get
        {
            try
            {
                var pm = (PowerManager?)_context.GetSystemService(Context.PowerService);
                return pm?.IsIgnoringBatteryOptimizations(_context.PackageName!) ?? true;
            }
            catch { return true; }
        }
    }

    public void RequestExemption()
    {
        try
        {
            if (IsExempt) return;
            var intent = new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
            intent.SetData(global::Android.Net.Uri.Parse("package:" + _context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
        }
        catch (System.Exception ex)
        {
            // 部分 ROM 无直接请求入口：退化为打开"电池优化"设置列表让用户手动选择
            try
            {
                var fallback = new Intent("android.settings.IGNORE_BATTERY_OPTIMIZATION_SETTINGS");
                fallback.AddFlags(ActivityFlags.NewTask);
                _context.StartActivity(fallback);
            }
            catch
            {
                global::Android.Util.Log.Warn("FTA.BOOT", "battery exemption intent failed: " + ex.Message);
            }
        }
    }
}
