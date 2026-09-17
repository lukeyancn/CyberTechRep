using System.Text;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// NapCat 一键启动辅助逻辑测试：登录方式启动参数组装（快速登录传裸 QQ 号/扫码不传参）、
/// webui.json 解析（端口/token → WebUI 地址）、webui.json 目录查找、
/// 已运行检测（不重复拉起）、启动前结束 QQ 进程开关、WebUI 未监听时的打开失败原因。
/// </summary>
public class NapCatRunnerServiceTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // ================= 登录方式 → 启动参数 =================

    [Fact]
    public void 扫码登录_不传启动参数()
    {
        var args = NapCatRunnerService.BuildArguments(NapCatLoginMode.ScanQrCode, "123456789", out var error);

        Assert.Null(error);
        Assert.Equal("", args);
    }

    [Fact]
    public void 快速登录_传入裸QQ号()
    {
        // NapCat 启动器只认裸 QQ 号（它自己转成 NTQQ 的 -q 参数）；直接传 "-q QQ号" 会被丢弃，
        // 实测会退回扫码登录，故此处断言不带 -q 前缀。
        var args = NapCatRunnerService.BuildArguments(NapCatLoginMode.QuickLoginQQ, " 123456789 ", out var error);

        Assert.Null(error);
        Assert.Equal("123456789", args);
        Assert.DoesNotContain("-q", args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc123")]
    [InlineData("123")]
    [InlineData("12345678901234567890")]
    public void 快速登录_QQ号非法_返回错误且不传参(string qq)
    {
        var args = NapCatRunnerService.BuildArguments(NapCatLoginMode.QuickLoginQQ, qq, out var error);

        Assert.NotNull(error);
        Assert.Contains("QQ 号", error);
        Assert.Equal("", args);
    }

    // ================= webui.json 解析 =================

    [Fact]
    public void 解析webui配置_端口与token()
    {
        const string json = """{ "port": 6099, "token": "napcat-token-1", "host": "0.0.0.0" }""";

        var (port, token) = NapCatRunnerService.ParseWebUiConfig(json);

        Assert.Equal(6099, port);
        Assert.Equal("napcat-token-1", token);
    }

    [Fact]
    public void 解析webui配置_缺token_端口仍可用()
    {
        const string json = """{ "port": 12345 }""";

        var (port, token) = NapCatRunnerService.ParseWebUiConfig(json);

        Assert.Equal(12345, port);
        Assert.Equal("", token);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{ "port": "abc" }""")]
    public void 解析webui配置_非法内容_返回零端口(string json)
    {
        var (port, _) = NapCatRunnerService.ParseWebUiConfig(json);

        Assert.Equal(0, port);
    }

    // ================= webui.json 目录查找 =================

    [Fact]
    public void 查找webui配置_napcat_config目录命中()
    {
        var root = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(root, "napcat", "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "webui.json");
        File.WriteAllText(configPath, """{ "port": 6099 }""");
        try
        {
            // exe 位于安装根目录（如指向启动器）
            var found = NapCatRunnerService.FindWebUiConfigPath(root);

            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(configPath), Path.GetFullPath(found!));
            _output.WriteLine($"found: {found}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 查找webui配置_exe在napcat目录内_向上查找命中()
    {
        var root = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        var napcatDir = Path.Combine(root, "napcat");
        var configDir = Path.Combine(napcatDir, "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "webui.json");
        File.WriteAllText(configPath, """{ "port": 6099 }""");
        try
        {
            // exe 直接位于 napcat 目录内（如指向 NapCatWinBootMain.exe）
            var found = NapCatRunnerService.FindWebUiConfigPath(napcatDir);

            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(configPath), Path.GetFullPath(found!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 查找webui配置_无任何候选_返回null()
    {
        var root = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(NapCatRunnerService.FindWebUiConfigPath(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ================= 启动信息（无窗口 + 输出重定向） =================

    [Fact]
    public void 启动信息_exe_无控制台窗口且重定向UTF8输出()
    {
        var startInfo = NapCatRunnerService.BuildProcessStartInfo(
            @"C:\napcat\NapCatWinBootMain.exe", @"C:\napcat", "123456789");

        Assert.Equal(@"C:\napcat\NapCatWinBootMain.exe", startInfo.FileName);
        Assert.Equal("123456789", startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(Encoding.UTF8, startInfo.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, startInfo.StandardErrorEncoding);
        Assert.Equal(@"C:\napcat", startInfo.WorkingDirectory);
    }

    [Fact]
    public void 启动信息_exe_扫码模式_无启动参数()
    {
        var startInfo = NapCatRunnerService.BuildProcessStartInfo(
            @"C:\napcat\NapCatWinBootMain.exe", null, "");

        Assert.Equal("", startInfo.Arguments);
        Assert.Equal(@"C:\napcat", startInfo.WorkingDirectory);
    }

    [Fact]
    public void 启动信息_工作目录为空_回退可执行文件所在目录()
    {
        var startInfo = NapCatRunnerService.BuildProcessStartInfo(
            @"C:\napcat\sub\NapCatWinBootMain.exe", "   ", "");

        Assert.Equal(@"C:\napcat\sub", startInfo.WorkingDirectory);
    }

    [Theory]
    [InlineData(@"C:\napcat\启动.bat")]
    [InlineData(@"C:\napcat\启动.cmd")]
    public void 启动信息_bat或cmd_经cmd包装并保留原参数(string scriptPath)
    {
        var startInfo = NapCatRunnerService.BuildProcessStartInfo(scriptPath, null, "123456789");

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/d /s /c ", startInfo.Arguments);
        Assert.Contains($"\"{scriptPath}\"", startInfo.Arguments);
        Assert.Contains("123456789", startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void 启动信息_bat无参数_脚本路径整体加引号()
    {
        const string scriptPath = @"C:\napcat\启动.bat";

        var startInfo = NapCatRunnerService.BuildProcessStartInfo(scriptPath, null, "");

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"/d /s /c \"\"{scriptPath}\"\"", startInfo.Arguments);
    }

    // ================= 日志缓冲接线 =================

    [Fact]
    public void 日志缓冲_容量来自连接设置()
    {
        var runner = CreateRunner(new ConnectionSettings { NapCatLogBufferLines = 321 });

        Assert.Equal(321, runner.LogBuffer.Capacity);
    }

    [Fact]
    public async Task 启动_真实bat进程_stdout进入日志缓冲且退出写入系统行()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // cmd 包装仅 Windows 可用
        }

        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "fake-napcat.bat");
        await File.WriteAllTextAsync(scriptPath,
            "@echo off\r\necho CyberTechRep-NapCat-日志标记\r\nping -n 8 127.0.0.1 >nul\r\n");
        var runner = CreateRunner(new ConnectionSettings
        {
            NapCatExePath = scriptPath,
            NapCatReversePort = 0
        });
        try
        {
            await runner.StartNapCatAsync();

            var marker = await WaitForLogAsync(runner, "CyberTechRep-NapCat-日志标记");
            Assert.NotNull(marker);
            Assert.Equal(NapCatLogStream.StdOut, marker!.Stream);

            var running = await WaitForTerminalAsync(runner);
            Assert.Equal(NapCatRunnerState.Running, running.State);

            // 进程自然退出（ping 计时结束）：写入系统行并转为 Stopped
            var exitLine = await WaitForLogAsync(runner, "进程已退出");
            Assert.NotNull(exitLine);
            Assert.Equal(NapCatLogStream.System, exitLine!.Stream);
            Assert.Equal(NapCatRunnerState.Stopped, runner.Status.State);
        }
        finally
        {
            await runner.StopNapCatAsync();
            runner.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    // ================= 已运行检测：不重复拉起 =================

    [Fact]
    public async Task 启动_反向WS已连接_跳过重复启动()
    {
        var killed = 0;
        var runner = CreateRunner(
            new ConnectionSettings
            {
                NapCatExePath = @"C:\napcat\不存在但无需校验的启动器.bat",
                NapCatReversePort = 0
            },
            connectionStatus: () => ConnectionStatus.Connected,
            endExistingQqProcesses: () => ++killed);
        using (runner)
        {
            await runner.StartNapCatAsync();
            await WaitForTerminalAsync(runner);

            Assert.Equal(NapCatRunnerState.Running, runner.Status.State);
            Assert.Contains("已在运行", runner.Status.Detail);
            Assert.DoesNotContain("pid", runner.Status.Detail);
            Assert.Equal(0, killed); // 已在运行：既不结束 QQ 进程，也不拉起新进程
        }
    }

    [Fact]
    public async Task 启动_WebUI端口在监听_跳过重复启动()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 依赖 Windows 路径布局
        }

        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(dir, "napcat", "config");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, "webui.json"), """{ "port": 6260 }""");
        var killed = 0;
        var runner = CreateRunner(
            new ConnectionSettings
            {
                NapCatExePath = Path.Combine(dir, "napcat", "launcher-user.bat"),
                NapCatReversePort = 0
            },
            probePort: port => port == 6260,
            endExistingQqProcesses: () => ++killed);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "napcat", "launcher-user.bat"), "@echo off\r\n");
            await runner.StartNapCatAsync();
            await WaitForTerminalAsync(runner);

            Assert.Equal(NapCatRunnerState.Running, runner.Status.State);
            Assert.Contains("6260", runner.Status.Detail);
            Assert.Equal(0, killed);
        }
        finally
        {
            runner.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    // ================= 启动前结束已有 QQ 进程 =================

    [Fact]
    public async Task 启动_结束QQ进程开关开启_调用结束委托并写入日志()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "fake-napcat.bat");
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\nping -n 4 127.0.0.1 >nul\r\n");
        var killed = 0;
        var runner = CreateRunner(
            new ConnectionSettings
            {
                NapCatExePath = scriptPath,
                NapCatReversePort = 0,
                NapCatEndExistingQq = true
            },
            endExistingQqProcesses: () => { killed++; return 2; });
        try
        {
            await runner.StartNapCatAsync();

            var line = await WaitForLogAsync(runner, "已结束 2 个已在运行的 QQ 进程");
            Assert.NotNull(line);
            Assert.Equal(1, killed);
        }
        finally
        {
            await runner.StopNapCatAsync();
            runner.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    [Fact]
    public async Task 启动_结束QQ进程开关关闭_不调用结束委托()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "fake-napcat.bat");
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\nping -n 4 127.0.0.1 >nul\r\n");
        var killed = 0;
        var runner = CreateRunner(
            new ConnectionSettings
            {
                NapCatExePath = scriptPath,
                NapCatReversePort = 0,
                NapCatEndExistingQq = false
            },
            endExistingQqProcesses: () => { killed++; return 1; });
        try
        {
            await runner.StartNapCatAsync();
            await WaitForTerminalAsync(runner);

            Assert.Equal(0, killed);
        }
        finally
        {
            await runner.StopNapCatAsync();
            runner.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    // ================= WebUI 探活：不打开死页面 =================

    [Fact]
    public void 打开WebUI_尚未发现地址_写入失败原因()
    {
        var runner = CreateRunner(new ConnectionSettings(), probePort: _ => false);
        using (runner)
        {
            runner.OpenWebUi();

            var line = runner.LogBuffer.Snapshot().FirstOrDefault(l => l.Text.Contains("打开 WebUI 失败"));
            Assert.NotNull(line);
            Assert.Contains("尚未发现 WebUI 地址", line!.Text);
        }
    }

    [Fact]
    public async Task 打开WebUI_端口未监听_不打开并写入端口原因()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(dir, "napcat", "config");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(Path.Combine(configDir, "webui.json"), """{ "port": 6260, "token": "t" }""");
        var scriptPath = Path.Combine(dir, "napcat", "launcher-user.bat");
        // 假进程需比 WebUI 探活重试（约 10 秒）活得更久，否则进程退出会提前结束探活
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\nping -n 40 127.0.0.1 >nul\r\n");
        // 探活一律失败：模拟端口被系统保留（NapCat 报 EACCES）时 WebUI 起不来的场景
        var runner = CreateRunner(
            new ConnectionSettings
            {
                NapCatExePath = scriptPath,
                NapCatReversePort = 0,
                NapCatOpenWebUiOnStart = false
            },
            probePort: _ => false,
            endExistingQqProcesses: () => 0);
        try
        {
            await runner.StartNapCatAsync();

            var hint = await WaitForLogAsync(runner, "WebUI 未在端口 6260 监听", attempts: 300); // 探活重试约 10 秒后才写提示
            Assert.NotNull(hint);
            Assert.Contains("excludedportrange", hint!.Text);

            Assert.False(runner.WebUiReady);
            Assert.NotNull(runner.WebUiUrl); // 地址已解析，但未探活通过

            runner.OpenWebUi();

            var failure = runner.LogBuffer.Snapshot().FirstOrDefault(l => l.Text.Contains("打开 WebUI 失败：端口 6260"));
            Assert.NotNull(failure);
        }
        finally
        {
            await runner.StopNapCatAsync();
            runner.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    // ================= 有头形态（Framework：显示 QQ 界面） =================

    [Fact]
    public void 有头形态_注入启动器入口_快速登录仍传裸QQ号()
    {
        var args = NapCatRunnerService.BuildArguments(
            NapCatRunMode.Framework, NapCatLoginMode.QuickLoginQQ,
            @"D:\NapCatFramework\NapCatWinBootMain.exe", "1223437061", out var error);

        Assert.Null(error);
        Assert.Equal("1223437061", args);
    }

    [Fact]
    public void 有头形态_官方QQ入口_不传快速登录参数且不因QQ号为空报错()
    {
        // 官方 QQ.exe 不吃「裸 QQ 号」参数：此时登录在 QQ 界面里完成，
        // 即使设置选了快速登录、QQ 号为空也不应把启动判成失败
        var args = NapCatRunnerService.BuildArguments(
            NapCatRunMode.Framework, NapCatLoginMode.QuickLoginQQ,
            @"C:\Program Files\Tencent\QQNT\QQ.exe", "", out var error);

        Assert.Null(error);
        Assert.Equal("", args);
    }

    [Fact]
    public void 无头形态_入口检查不生效_沿用既有校验()
    {
        // 零回归：无头形态下仍按登录设置校验（快速登录必须填合法 QQ 号）
        var args = NapCatRunnerService.BuildArguments(
            NapCatRunMode.Headless, NapCatLoginMode.QuickLoginQQ,
            @"C:\Program Files\Tencent\QQNT\QQ.exe", "", out var error);

        Assert.NotNull(error);
        Assert.Equal("", args);
    }

    [Theory]
    [InlineData(@"D:\NapCat\NapCatWinBootMain.exe", true)]
    [InlineData(@"D:\NapCat\napcat\launcher-user.bat", true)]
    [InlineData(@"C:\Program Files\Tencent\QQNT\QQ.exe", false)]
    [InlineData(@"D:\LiteLoader\LiteLoaderQQNT.exe", false)]
    [InlineData("", false)]
    public void AcceptsQuickLoginArgument_按入口名判定(string exePath, bool expected)
        => Assert.Equal(expected, NapCatRunnerService.AcceptsQuickLoginArgument(exePath));

    [Fact]
    public async Task 有头形态_启动器拉起后退出但QQ在运行_判为运行中()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 需要真实进程模拟「启动器退出、QQ 接管」
        }

        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "fake-boot.bat");
        // 模拟注入启动器：把 QQ 拉起后自身立即退出（有头形态的正常现象）
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\nexit /b 0\r\n");
        var runner = CreateRunner(
            new ConnectionSettings
            {
                NapCatExePath = scriptPath,
                NapCatRunMode = NapCatRunMode.Framework,
                NapCatReversePort = 0
            },
            isQqProcessRunning: () => true);
        try
        {
            await runner.StartNapCatAsync();
            await WaitForTerminalAsync(runner);

            Assert.Equal(NapCatRunnerState.Running, runner.Status.State);
            Assert.Contains("QQ 进程内", runner.Status.Detail);

            var line = await WaitForLogAsync(runner, "QQ 进程仍在运行");
            Assert.NotNull(line);
            Assert.Equal(NapCatLogStream.System, line!.Stream);
        }
        finally
        {
            await runner.StopNapCatAsync();
            runner.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    [Fact]
    public async Task 有头形态_停止_启动器已退出时结束QQ进程()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, "fake-boot.bat");
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\nexit /b 0\r\n");
        var qqRunning = true;
        var killed = 0;
        var runner = CreateRunner(
            new ConnectionSettings
            {
                NapCatExePath = scriptPath,
                NapCatRunMode = NapCatRunMode.Framework,
                NapCatReversePort = 0,
                // 有头形态下用户可能已开着 QQ 界面：本例不动启动前的 QQ，只在「停止」时结束它
                NapCatEndExistingQq = false
            },
            endExistingQqProcesses: () =>
            {
                killed++;
                qqRunning = false;
                return 1;
            },
            isQqProcessRunning: () => qqRunning);
        try
        {
            await runner.StartNapCatAsync();
            await WaitForTerminalAsync(runner);
            Assert.Equal(NapCatRunnerState.Running, runner.Status.State);

            await runner.StopNapCatAsync();

            // 有头形态：启动器早已退出，停止必须结束 QQ 进程（= 关闭 QQ 界面）
            Assert.Equal(1, killed);
            Assert.Equal(NapCatRunnerState.NotRunning, runner.Status.State);
            var line = await WaitForLogAsync(runner, "有头形态停止");
            Assert.NotNull(line);
        }
        finally
        {
            runner.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果
            }
        }
    }

    // ================= 测试辅助 =================

    /// <summary>
    /// 构造启动服务。测试环境一律注入「结束 QQ 进程」假委托（默认返回 0 且不计数），
    /// 避免测试真的去结束机器上正在运行的 QQ；需要断言调用时由用例显式覆盖。
    /// QQ 进程探测默认恒为「不在运行」，有头形态用例显式注入。
    /// </summary>
    private static NapCatRunnerService CreateRunner(ConnectionSettings settings,
        Func<int?>? selfPort = null,
        Func<ConnectionStatus>? connectionStatus = null,
        Func<int, bool>? probePort = null,
        Func<int>? endExistingQqProcesses = null,
        Func<bool>? isQqProcessRunning = null)
        => new(new IngestOptionsProvider
        {
            GetSettings = () => settings,
            DataDirectory = Path.GetTempPath()
        },
            null,
            selfPort,
            connectionStatus,
            probePort,
            endExistingQqProcesses ?? (() => 0),
            isQqProcessRunning ?? (() => false));

    private static async Task<NapCatRunnerStatus> WaitForTerminalAsync(NapCatRunnerService runner)
    {
        for (var i = 0; i < 50; i++)
        {
            var status = runner.Status;
            if (status.State is NapCatRunnerState.Failed or NapCatRunnerState.Running or NapCatRunnerState.Stopped)
            {
                return status;
            }

            await Task.Delay(100);
        }

        return runner.Status;
    }

    private static async Task<NapCatLogLine?> WaitForLogAsync(NapCatRunnerService runner, string textFragment, int attempts = 100)
    {
        for (var i = 0; i < attempts; i++)
        {
            var line = runner.LogBuffer.Snapshot().FirstOrDefault(l => l.Text.Contains(textFragment));
            if (line is not null)
            {
                return line;
            }

            await Task.Delay(100);
        }

        return null;
    }
}
