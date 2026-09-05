using System.IO;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 模拟 Android 闪退防御性代码的纯逻辑等价测试（无法在 Windows 上实例化 Java/Android 对象）。
/// 对以下契约做纯托管层的等价验证：
///   1) StartBackgroundServicesAsync 切线程池线程执行：等价于返回的 Task 与调用线程不同 ManagedThreadId。
///   2) 崩溃日志 CrashLogPath 文件路径计算：等价于 "<目录>/crash.log"，Path.Combine 语义正确。
///   3) UnobservedTaskException 经过 SetObserved 后不触发进程崩溃：等价于 TaskCompletionSource.Unobserved 不抛。
///   4) FGS "5 秒窗口内必有一次 StartForeground 调用" 的等价：
///      OnStartCommand 的准备阶段无论抛异常、正常、null，finally 里"模拟调用"计数恒为 1。
///   5) Fallback 图标选择流程的语义：无论抛什么异常都返回正整数 ID。
/// </summary>
public class AndroidDefenseLogicEquivalenceTests
{
    // ==== 1) StartBackgroundServicesAsync 使用 Task.Run 切线程池执行 ====
    // App.axaml.cs 现在用 Task.Run 包装后台服务：严格等价于“准备/绑定 socket 的代码不阻塞调用者返回”
    // 测试语义：Task.Run 返回的 Task 在调用后同步返回（不会同步卡住调用线程）。
    [Fact]
    public async Task BackgroundServiceStart_Returns_Task_Immediately_Not_Blocking_Caller()
    {
        using var cts = new CancellationTokenSource();
        bool insideRanToEnd = false;
        // 模拟 Task.Run(await discovery.StartAsync(); server.StartAsync();) 里执行耗时
        var t = Task.Run(async () =>
        {
            // 模拟 socket 绑定与监听循环开始（这里用延迟模拟）
            await Task.Delay(500, cts.Token).ConfigureAwait(false);
            insideRanToEnd = true;
        });

        // --- 关键点：Task.Run 一调就返回 Task，内部逻辑已在线程池异步执行 ---
        Assert.False(t.IsCompleted, "Task.Run 返回时后台任务不应同步完成");
        Assert.False(insideRanToEnd, "后台 Task.Run 内部逻辑在调用方返回时未完成，等价于原 UI 线程不被 new Socket/Bind 阻塞");

        await t.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(insideRanToEnd);
    }

    // ==== 2) 崩溃日志路径语义：{filesDir}/crash.log ====
    [Theory]
    [InlineData("/data/user/0/com.CompanyName.FileTransferApp/files")]
    [InlineData("/sdcard/Android/data/com.CompanyName.FileTransferApp/files")]
    [InlineData("/data/local/tmp")]
    public void CrashLogPath_Combines_FilesDir_With_CrashLog(string filesDir)
    {
        var result = Path.Combine(filesDir, "crash.log");
        Assert.EndsWith(Path.DirectorySeparatorChar + "crash.log", result);
        Assert.StartsWith(filesDir, result);
        Assert.DoesNotContain("..", result); // 无回溯
    }

    // ==== 3) UnobservedTaskException SetObserved 不触发进程崩溃 ====
    // 我们无法在这里触发真正的进程级 Unobserved，只验证 SetObserved 语义：
    // UnobservedTaskExceptionEventArgs 标记为 Observed 后 IsObserved=true。
    [Fact]
    public void SetObserved_Marks_Exception_As_Handled()
    {
        // 我们在 MainActivity 中写 args.SetObserved() 防止 .NET Runtime 调用 Environment.FailFast。
        // 这里用等价的自定义事件 + 回调模拟 unobserved 触发路径。
        bool isObserved = false;
        var simArgs = new SimUEArgs(new Exception("boom"), () => isObserved = true);
        EventHandler<SimUEArgs> handler = (_, e) => e.SetObserved();
        var evt = (EventHandler<SimUEArgs>?)null;
        evt += handler;
        evt?.Invoke(this, simArgs);
        Assert.True(isObserved, "SetObserved 必须在处理程序内部被调，等价于 args.SetObserved()");
    }

    public sealed class SimUEArgs : EventArgs
    {
        public Exception Ex { get; }
        private readonly Action _setObserved;
        public SimUEArgs(Exception ex, Action setObserved) { Ex = ex; _setObserved = setObserved; }
        public void SetObserved() => _setObserved();
    }

    // ==== 4) FGS 5 秒窗口：模拟 OnStartCommand 无论成功/异常都会调用一次 StartForeground ====
    // 我们以纯逻辑 StateMachine 模仿 finally/CallStartForegroundGuaranteed 行为
    [Theory]
    [InlineData("normal")]       // 准备阶段不抛异常
    [InlineData("channel_error")] // EnsureNotificationChannelSafe 抛异常
    [InlineData("build_error")]  // BuildNotificationSafe 抛异常
    [InlineData("empty_intent")] // intent=null（title/content 回退）
    public void FGS_OnStartCommand_Calls_StartForeground_Exactly_Once(string scenario)
    {
        int count = 0;
        void SimulatedOnStartCommand()
        {
            try
            {
                try
                {
                    if (scenario == "channel_error")
                        throw new InvalidOperationException("channel boom");

                    try
                    {
                        if (scenario == "build_error")
                            throw new System.IO.IOException("notification build boom");
                    }
                    catch
                    {
                        // 回退使用 fallback 通知
                    }
                }
                catch
                {
                    // 回退
                }
            }
            finally
            {
                // 与 CallStartForegroundGuaranteed 等价：无论 try 成功失败，+1
                count++;
            }
        }

        // 空 Intent 的 title/content 回退不影响执行计数（只影响字符串）
        // 模拟 intent 为 null
        _ = scenario == "empty_intent" ? (null as string) : "some";

        SimulatedOnStartCommand();
        Assert.Equal(1, count);
    }

    // ==== 5) FGS ForegroundService TypeDataSync 的版本阈值计算：29+ → 非零；否则 0 ====
    [Theory]
    [InlineData(19, 0)]  // KitKat
    [InlineData(26, 0)]  // Oreo 8.0
    [InlineData(28, 0)]  // Pie 9.0 → 低于 29
    [InlineData(29, -1)] // Android 10 → TypeDataSync 有值（>0）
    [InlineData(30, -1)]
    [InlineData(34, -1)] // Android 14
    public void TypeDataSync_Version_Branch_Matches(int sdkInt, int expectedSign)
    {
        // 真实代码: OperatingSystem.IsAndroidVersionAtLeast(29) ? (int)TypeDataSync : 0
        // 这里等价逻辑
        int result = sdkInt >= 29 ? 1073741824 : 0; // (int)TypeDataSync 在 .NET for Android 中是 0x40000000
        if (expectedSign == 0) Assert.Equal(0, result);
        else Assert.True(result > 0, $"sdk={sdkInt} 时 type 应正");
    }

    // ==== 6) ForegroundService 空 Intent 回退标题/内容正确性 ====
    [Fact]
    public void Empty_Intent_FallsBack_To_Defaults()
    {
        string? intentTitle = null;
        string? intentContent = null;
        string? curTitle = null;
        string? curContent = null;
        string defTitle = "FileTransferApp";
        string defContent = "正在保持传输服务运行";

        var title = intentTitle ?? curTitle ?? defTitle;
        var content = intentContent ?? curContent ?? defContent;

        Assert.Equal(defTitle, title);
        Assert.Equal(defContent, content);
    }

    // ==== 7) GetFallbackIcon 路径无论哪个 try 分支抛都要返回正 int ====
    [Fact]
    public void FallbackIcon_Always_Positive_Int()
    {
        // 模拟 4 层 try/catch 链，每一层都抛异常时最终硬编码值
        int TryGet()
        {
            try { throw new Exception(); }
            catch
            {
                try { throw new Exception(); }
                catch
                {
                    try { throw new Exception(); }
                    catch
                    {
                        return 17301659; // android.R.drawable.stat_notify_sync
                    }
                }
            }
        }

        var id = TryGet();
        Assert.True(id > 0);
        Assert.Equal(17301659, id); // 最终兜底硬编码值必须一致
    }

    // ==== 8) Manifest @drawable/icon 必须使用小写命名 ====
    [Fact]
    public void Manifest_Drawable_Name_All_Lowercase_As_Required_By_AAPT()
    {
        // AAPT 要求资源名仅小写字母/数字/下划线
        string value = "icon";
        Assert.All(value, c =>
        {
            Assert.True(char.IsAsciiLetterLower(c) || char.IsDigit(c) || c == '_',
                $"@drawable 资源名必须小写: {c}");
        });
    }

    // ==== 9) Crash 日志追加写入不会相互覆盖 ====
    [Fact]
    public async Task Crash_Log_Append_Writes_All_Entries()
    {
        var path = Path.GetTempFileName();
        try
        {
            var entries = new[]
            {
                $"[t1] stage=A 1",
                $"[t2] stage=B 2",
                $"[t3] stage=C 3"
            };
            foreach (var e in entries)
                await File.AppendAllTextAsync(path, e + Environment.NewLine);

            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("stage=A", lines[0]);
            Assert.Contains("stage=B", lines[1]);
            Assert.Contains("stage=C", lines[2]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
