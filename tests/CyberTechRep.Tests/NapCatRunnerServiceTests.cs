using System.Text;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace CyberTechRep.Tests;

/// <summary>
/// NapCat 一键启动辅助逻辑测试：登录方式启动参数组装（-q 快速登录/扫码不传参）、
/// webui.json 解析（端口/token → WebUI 地址）、webui.json 目录查找。
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
    public void 快速登录_传入q参数()
    {
        var args = NapCatRunnerService.BuildArguments(NapCatLoginMode.QuickLoginQQ, " 123456789 ", out var error);

        Assert.Null(error);
        Assert.Equal("-q 123456789", args);
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
            @"C:\napcat\NapCatWinBootMain.exe", @"C:\napcat", "-q 123456789");

        Assert.Equal(@"C:\napcat\NapCatWinBootMain.exe", startInfo.FileName);
        Assert.Equal("-q 123456789", startInfo.Arguments);
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
        var startInfo = NapCatRunnerService.BuildProcessStartInfo(scriptPath, null, "-q 123456789");

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/d /s /c ", startInfo.Arguments);
        Assert.Contains($"\"{scriptPath}\"", startInfo.Arguments);
        Assert.Contains("-q 123456789", startInfo.Arguments);
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

    // ================= 测试辅助 =================

    private static NapCatRunnerService CreateRunner(ConnectionSettings settings)
        => new(new IngestOptionsProvider
        {
            GetSettings = () => settings,
            DataDirectory = Path.GetTempPath()
        });

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

    private static async Task<NapCatLogLine?> WaitForLogAsync(NapCatRunnerService runner, string textFragment)
    {
        for (var i = 0; i < 100; i++)
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
