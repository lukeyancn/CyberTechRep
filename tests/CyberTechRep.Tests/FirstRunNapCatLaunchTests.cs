using System.Reflection;
using System.Text.Json;
using CyberTechRep.Plugin.Services.FirstRun;
using CyberTechRep.Plugin.Services.MessageAccess;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 引导里的「启动 NapCat 并打开 WebUI」与收尾处理测试：
/// ① 收尾会把「启动 NapCat 后自动打开 WebUI」关掉，且不动其它设置；
/// ② 完成页 / 跳过整份引导 / 点「×」关闭三条收尾路都走同一段纯逻辑（窗口只调用，不自己改设置）；
/// ③ 第 5 步的十个窗口开关名字与对应设置项都不变（排版改成卡片后不回归）；
/// ④ 引导里不静默启动 NapCat（全窗口只有按钮点击那一处启动调用）。
/// </summary>
public class FirstRunNapCatLaunchTests
{
    /// <summary>第 5 步的十个窗口开关（XAML 里的控件名，去掉结尾的 Box 就是设置项名字）。</summary>
    private static readonly string[] OverlaySwitchNames =
    [
        "CircleVisibleBox", "CircleTopmostBox",
        "NoticeVisibleBox", "NoticeTopmostBox",
        "HomeworkVisibleBox", "HomeworkTopmostBox",
        "FilesVisibleBox", "FilesTopmostBox",
        "ImageVisibleBox", "ImageTopmostBox"
    ];

    // ==================== ① 收尾默认值 ====================

    [Fact]
    public void ApplyCompletionDefaults_只关掉WebUI自启动_其它设置保持原样()
    {
        var settings = BuildFullyCustomizedSettings();
        settings.Connection.NapCatOpenWebUiOnStart = true;
        var before = JsonSerializer.Serialize(settings);

        FirstRunWizardFlow.ApplyCompletionDefaults(settings);

        var after = JsonSerializer.Serialize(settings);
        Assert.NotEqual(before, after);
        Assert.Contains("\"NapCatOpenWebUiOnStart\":true", before);
        Assert.Contains("\"NapCatOpenWebUiOnStart\":false", after);
        // 把这一项换回原值后两段必须完全一致：说明收尾没有顺手动别的设置
        Assert.Equal(before, after.Replace("\"NapCatOpenWebUiOnStart\":false", "\"NapCatOpenWebUiOnStart\":true"));
    }

    [Fact]
    public void ApplyCompletionDefaults_默认设置上调用后WebUI自启动为关()
    {
        var settings = new AppSettings();

        FirstRunWizardFlow.ApplyCompletionDefaults(settings);

        Assert.False(settings.Connection.NapCatOpenWebUiOnStart);
        Assert.False(settings.FirstRunCompleted); // 完成标记由引导服务写，收尾不越权
    }

    [Fact]
    public void ApplyCompletionDefaults_空设置对象_直接报错而不是静默跳过()
    {
        Assert.Throws<ArgumentNullException>(() => FirstRunWizardFlow.ApplyCompletionDefaults(null!));
    }

    // ==================== ② 三条收尾路都调用收尾处理 ====================

    [Fact]
    public void 引导收尾_完成页与跳过引导都调用同一段收尾处理()
    {
        var source = File.ReadAllText(WizardWindowCodePath());

        // 窗口自己不直接改设置项，而是调用引导流程里的纯逻辑（可单测的那一份）
        Assert.Contains("FirstRunWizardFlow.ApplyCompletionDefaults(", source);
        // 完成页、跳过整份引导、点「×」关闭三条路各调用一次
        Assert.True(CountOccurrences(source, "await ApplyCompletionDefaultsAsync()") >= 3,
            "完成页 / 跳过引导 / 关闭三条收尾路都要写收尾默认值");
    }

    [Fact]
    public void 完成页提醒_写明WebUI自启动已关闭_且给出重新打开的位置()
    {
        var summary = FirstRunWizardFlow.BuildSummary(new FirstRunWizardInput());

        Assert.Contains(summary.Notes, note => note.Contains("已关闭「启动 NapCat 后自动打开 WebUI」"));
        Assert.Contains("「CyberTechRep 连接」页点「打开 WebUI」", FirstRunWizardText.WebUiAutoOpenClosedOnFinish);
        Assert.Contains("提醒：", summary.ToPlainText());
        Assert.Contains("已关闭「启动 NapCat 后自动打开 WebUI」", summary.ToPlainText());
    }

    // ==================== ③ 第 5 步的十个窗口开关 ====================

    [Fact]
    public void 第5步_十个窗口开关名字不变_且都能在引导输入里找到对应项()
    {
        var xaml = File.ReadAllText(WizardXamlPath());

        foreach (var name in OverlaySwitchNames)
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);

            // 控件名去掉结尾的 Box 就是引导输入里的设置项（例如 CircleVisibleBox → CircleVisible）
            var propertyName = name[..^"Box".Length];
            Assert.NotNull(typeof(FirstRunWizardInput).GetProperty(propertyName));
        }
    }

    [Fact]
    public void 第5步_每个开关都自带文字_不用抬头看表头()
    {
        var xaml = File.ReadAllText(WizardXamlPath());

        Assert.Equal(5, CountOccurrences(xaml, "Content=\"显示\""));
        Assert.Equal(5, CountOccurrences(xaml, "Content=\"置顶\""));
        // 五个窗口各一张卡片（卡片底色一致），并且卡片组下方点明「未绑定学科选择窗」不用在这里开
        Assert.True(CountOccurrences(xaml, "Background=\"#0F000000\"") >= 6);
        Assert.Contains("未绑定学科选择窗", xaml);
        Assert.Contains("不需要在这里开", xaml);
    }

    // ==================== ④ 启动 NapCat 并打开 WebUI ====================

    [Fact]
    public void 第1步_启动卡片与按钮都在XAML里()
    {
        var xaml = File.ReadAllText(WizardXamlPath());

        Assert.Contains("x:Name=\"NapCatLaunchButton\"", xaml);
        Assert.Contains("Click=\"OnNapCatLaunchClick\"", xaml);
        Assert.Contains("Content=\"启动 NapCat 并打开 WebUI\"", xaml);
        Assert.Contains("x:Name=\"NapCatOpenWebUiButton\"", xaml);
        Assert.Contains("Click=\"OnNapCatOpenWebUiClick\"", xaml);
        Assert.Contains("x:Name=\"NapCatLaunchStatus\"", xaml);

        // 次按钮与状态文字默认都藏起来，等启动后才出现
        Assert.Contains("IsVisible=\"False\"", ElementTag(xaml, "x:Name=\"NapCatOpenWebUiButton\""));
        Assert.Contains("IsVisible=\"False\"", ElementTag(xaml, "x:Name=\"NapCatLaunchStatus\""));
        Assert.Contains("TextWrapping=\"Wrap\"", ElementTag(xaml, "x:Name=\"NapCatLaunchStatus\""));
        Assert.Contains("FontSize=\"12\"", ElementTag(xaml, "x:Name=\"NapCatLaunchStatus\""));
    }

    [Fact]
    public void 启动流程_复用一键启动服务_等WebUI就绪后打开_并在超时给下一步()
    {
        var source = File.ReadAllText(WizardWindowCodePath());

        Assert.Contains("await runner.StartNapCatAsync()", source);   // 与「连接」页同一份启动实现
        Assert.Contains("runner.WebUiReady", source);                 // 就绪判断
        Assert.Contains("runner.OpenWebUi();", source);               // 就绪后打开登录页面
        Assert.Contains("new DispatcherTimer", source);               // 每秒轮询（UI 线程）
        Assert.Contains("NapCatWebUiWaitSeconds = 60", source);       // 轮询上限约 60 秒
        Assert.Contains("还没等到 WebUI", source);                    // 超时提示
        Assert.Contains("已打开 WebUI，请在浏览器里扫码登录", source);
        Assert.Contains("NapCatRunnerState.Failed", source);          // 启动失败按失败原因停下，不空等
        Assert.Contains("StopNapCatPolling", source);                 // 离开该步 / 关窗要停表
        Assert.Contains("Closed += (_, _) => StopNapCatPolling();", source);
        Assert.Contains("WebUI 还没启动：先点上面的「启动 NapCat 并打开 WebUI」", source);
        Assert.Contains("还没启动 NapCat", source);                    // 没点启动就下一步的非阻塞提示
        // 先保存本步填的路径 / 端口，再启动（启动读的是保存后的设置）
        Assert.Contains("ApplyAndSaveStepAsync(FirstRunWizardStep.Connection)", source);
    }

    [Fact]
    public void 引导不自动启动NapCat_全窗口只有按钮点击那一处启动调用()
    {
        var source = File.ReadAllText(WizardWindowCodePath());

        // 「离开该步 / 进完成页 / 跳过」都不得顺手拉起第三方程序：StartNapCatAsync 只能出现在启动按钮处理里
        var startCalls = CountOccurrences(source, "StartNapCatAsync(");
        Assert.Equal(1, startCalls);
        Assert.Contains("private async void OnNapCatLaunchClick", source);
    }

    // ==================== 构造：旧的两参数调用仍然可用 ====================

    [Fact]
    public void 引导窗口构造_保留两个参数的旧调用_并新增可选的NapCat启动服务()
    {
        var constructors = typeof(FirstRunWizardWindow).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // 预览用的无参构造仍在
        Assert.Contains(constructors, c => c.GetParameters().Length == 0);

        // 运行时构造：设置服务 + 引导服务 + 可选的 NapCat 启动服务（不传第三项时旧写法照样能用）
        var main = constructors.SingleOrDefault(c => c.GetParameters() is
            [{ ParameterType: var first }, { ParameterType: var second }, { ParameterType: var third }]
            && first == typeof(CyberTechRep.Shared.Abstractions.ISettingsService)
            && second == typeof(IFirstRunService)
            && third == typeof(NapCatRunnerService));
        Assert.NotNull(main);
        Assert.True(main!.GetParameters()[2].IsOptional); // 不传也能构造（测试与预览不受影响）
        Assert.Null(main.GetParameters()[2].DefaultValue);
    }

    // ==================== 辅助 ====================

    /// <summary>一份「到处都不是默认值」的设置：用来证明收尾只动 WebUI 自启动这一项。</summary>
    private static AppSettings BuildFullyCustomizedSettings()
    {
        var settings = new AppSettings
        {
            FirstRunCompleted = false
        };
        settings.Connection.Mode = MessageConnectionMode.NapCat;
        settings.Connection.NapCatExePath = @"D:\NapCat\napcat\launcher-user.bat";
        settings.Connection.NapCatWorkDirectory = @"D:\NapCat";
        settings.Connection.NapCatReversePort = 4001;
        settings.Connection.NapCatAccessTokenProtected = "enc-token";
        settings.Connection.NapCatRunMode = NapCatRunMode.Framework;
        settings.Connection.NapCatLoginMode = NapCatLoginMode.QuickLoginQQ;
        settings.Connection.NapCatQuickLoginQQ = "123456789";
        settings.Connection.NapCatAutoStart = true;
        settings.Connection.NapCatEndExistingQq = false;
        settings.Connection.GroupWhitelist = ["111", "222"];
        settings.Connection.TargetGroupOpenIds = ["333"];
        settings.Connection.HomeworkSendEnabled = false;
        settings.Classification.NoticeSubjectPrefix = false;
        settings.SubjectRecognition.Mode = SubjectRecognitionMode.Keyword;
        settings.SubjectRecognition.SelectionWindowEnabled = false;
        settings.Overlays.Notice.Visible = false;
        settings.Overlays.Image.Visible = true;
        settings.Overlays.SubjectCircle.AutoOpenWithClass = true;
        settings.Overlays.SubjectCircle.AutoOpenDelaySeconds = -30;
        settings.Files.DownloadRoot = @"D:\班级文件";
        settings.Files.CleanupPolicy = 1;
        settings.Maintenance.HomeworkRetentionDays = 7;
        return settings;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>取出某个元素从 <paramref name="marker"/> 开始到标签结束（"&gt;" 或 "/&gt;"）的那一段文本。</summary>
    private static string ElementTag(string xaml, string marker)
    {
        var start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"XAML 里找不到 {marker}");
        var end = xaml.IndexOf('>', start);
        Assert.True(end > start, $"{marker} 所在标签没有结束");
        return xaml[start..end];
    }

    private static string WizardXamlPath() => FindWizardFile("FirstRunWizardWindow.axaml");

    private static string WizardWindowCodePath() => FindWizardFile("FirstRunWizardWindow.axaml.cs");

    /// <summary>从测试输出目录向上找到引导窗口的 XAML / 代码文件（Debug / Release 布局都适用）。</summary>
    private static string FindWizardFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CyberTechRep.Plugin", "Views", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"未找到 {fileName}（本测试需在仓库内运行）");
    }
}
