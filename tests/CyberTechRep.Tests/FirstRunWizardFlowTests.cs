using CyberTechRep.Plugin.Services.FirstRun;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 首次启动引导流程测试（不构造任何界面对象）：
/// 步骤推进 / 回退 / 跳过、每步必填校验（AppID 留空、NapCat 路径留空等）、
/// 写回设置的规则（密钥不丢、留空回落默认、列表去重）与完成页汇总（未配置项 + 去哪里补）。
/// </summary>
public class FirstRunWizardFlowTests
{
    /// <summary>面向普通使用者的文字里不允许出现的内部说法。</summary>
    private static readonly string[] ForbiddenWords =
    [
        "模块", "需求", "占位", "幂等", "投递重试队列", "热读取", "结构化日志",
        "Provider", "宿主", "协议端", "字段", "边界", "抽象", "实例"
    ];

    private static int IndexOf(FirstRunWizardStep step) => FirstRunWizardSteps.Order.ToList().IndexOf(step);

    // ==================== 步骤顺序与推进 ====================

    [Fact]
    public void Steps_StartWithWelcome_EndWithFinish_AndOrderMustConfigureFirst()
    {
        var order = FirstRunWizardSteps.Order;

        Assert.Equal(9, order.Count);
        Assert.Equal(FirstRunWizardStep.Welcome, order[0]);
        Assert.Equal(FirstRunWizardStep.Finish, order[^1]);
        // 必须先配 → 才能用 → 用起来怎么调：连接在最前，使用说明在配完之后
        Assert.True(IndexOf(FirstRunWizardStep.Connection) < IndexOf(FirstRunWizardStep.IngestScope));
        Assert.True(IndexOf(FirstRunWizardStep.IngestScope) < IndexOf(FirstRunWizardStep.SendTargets));
        Assert.True(IndexOf(FirstRunWizardStep.SendTargets) < IndexOf(FirstRunWizardStep.Classification));
        Assert.True(IndexOf(FirstRunWizardStep.Classification) < IndexOf(FirstRunWizardStep.Overlays));
        Assert.True(IndexOf(FirstRunWizardStep.Overlays) < IndexOf(FirstRunWizardStep.FilesRetention));
        Assert.True(IndexOf(FirstRunWizardStep.FilesRetention) < IndexOf(FirstRunWizardStep.Usage));
        Assert.True(IndexOf(FirstRunWizardStep.Usage) < IndexOf(FirstRunWizardStep.Finish));
    }

    [Fact]
    public void Flow_NextAndBack_MoveInOrder()
    {
        var flow = new FirstRunWizardFlow();

        Assert.Equal(FirstRunWizardStep.Welcome, flow.Current);
        Assert.Equal(1, flow.StepNumber);
        Assert.False(flow.CanGoBack);

        Assert.Equal(FirstRunWizardStep.Connection, flow.Next());
        Assert.Equal(2, flow.StepNumber);
        Assert.True(flow.CanGoBack);

        Assert.Equal(FirstRunWizardStep.Welcome, flow.Back());
        // 第 1 步再回退原地不动
        Assert.Equal(FirstRunWizardStep.Welcome, flow.Back());
        Assert.False(flow.CanGoBack);
    }

    [Fact]
    public void Flow_AllStepsReachable_AndFinishIsLast()
    {
        var flow = new FirstRunWizardFlow();
        for (var i = 0; i < flow.StepCount - 1; i++)
        {
            flow.Next();
        }

        Assert.Equal(FirstRunWizardStep.Finish, flow.Current);
        Assert.True(flow.AtFinish);
        Assert.Equal(flow.StepCount, flow.StepNumber);
        Assert.Equal(FirstRunWizardStep.Finish, flow.Next()); // 最后一步不再前进
    }

    [Fact]
    public void Flow_Skip_RecordsStepAndMovesOn()
    {
        var flow = new FirstRunWizardFlow();
        flow.Next();

        var skipped = flow.Current;
        var next = flow.SkipStep();

        Assert.Equal(FirstRunWizardStep.Connection, skipped);
        Assert.True(flow.IsSkipped(skipped));
        Assert.False(flow.IsSkipped(FirstRunWizardStep.IngestScope));
        Assert.Equal(FirstRunWizardStep.IngestScope, next);
    }

    [Fact]
    public void Flow_SkipNotAllowedOnUsageAndFinish()
    {
        var flow = new FirstRunWizardFlow();
        while (flow.Current != FirstRunWizardStep.Usage)
        {
            flow.Next();
        }

        Assert.False(flow.CanSkip);
        Assert.Equal(FirstRunWizardStep.Usage, flow.SkipStep()); // 原地不动

        flow.Next();
        Assert.Equal(FirstRunWizardStep.Finish, flow.Current);
        Assert.False(flow.CanSkip);
        Assert.Equal(FirstRunWizardStep.Finish, flow.SkipStep());
    }

    [Fact]
    public void Buttons_WelcomeSkipSkipsWholeWizard_UsageFinishes()
    {
        Assert.Equal("跳过引导（稍后再配）", FirstRunWizardSteps.SkipButtonText(FirstRunWizardStep.Welcome));
        Assert.True(FirstRunWizardSteps.SkipSkipsWholeWizard(FirstRunWizardStep.Welcome));
        Assert.Equal("跳过这一步", FirstRunWizardSteps.SkipButtonText(FirstRunWizardStep.Connection));
        Assert.False(FirstRunWizardSteps.SkipSkipsWholeWizard(FirstRunWizardStep.Connection));

        Assert.Equal("下一步", FirstRunWizardSteps.NextButtonText(FirstRunWizardStep.Connection));
        Assert.Equal("完成", FirstRunWizardSteps.NextButtonText(FirstRunWizardStep.Usage));
        Assert.Equal("关闭", FirstRunWizardSteps.NextButtonText(FirstRunWizardStep.Finish));
    }

    [Fact]
    public void NeedsSave_OnlyForStepsWithInput()
    {
        Assert.False(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.Welcome));
        Assert.True(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.Connection));
        Assert.True(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.IngestScope));
        Assert.True(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.SendTargets));
        Assert.True(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.Classification));
        Assert.True(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.Overlays));
        Assert.True(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.FilesRetention));
        Assert.False(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.Usage));
        Assert.False(FirstRunWizardSteps.NeedsSave(FirstRunWizardStep.Finish));
    }

    [Fact]
    public void SavedMessages_TellUserHowToVerify()
    {
        foreach (var step in FirstRunWizardSteps.Order.Where(FirstRunWizardSteps.NeedsSave))
        {
            Assert.Contains("怎么验证", FirstRunWizardSteps.SavedMessage(step));
        }
    }

    [Fact]
    public void EverySkippableStep_HasMakeUpHint()
    {
        foreach (var step in FirstRunWizardSteps.Order.Where(FirstRunWizardSteps.CanSkip))
        {
            Assert.False(string.IsNullOrWhiteSpace(FirstRunWizardSteps.MakeUpHint(step)));
        }
    }

    // ==================== 第 2 步：连接方式的必填校验 ====================

    [Fact]
    public void Validate_OfficialMode_EmptyAppIdAndSecret_BlocksWithActionableText()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "",
            AppSecretPlain = ""
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("AppID"));
        Assert.Contains(result.Errors, e => e.Contains("q.qq.com"));      // 去哪拿
        Assert.Contains(result.Errors, e => e.Contains("AppSecret"));
        Assert.Contains(result.Errors, e => e.Contains("跳过这一步"));     // 跳过是明确出路
    }

    [Fact]
    public void Validate_OfficialMode_AppIdAndSecretFilled_Passes()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "app-9527",
            AppSecretPlain = "s3cret",
            ApiBase = "",
            TokenApiUrl = ""
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input);

        Assert.True(result.IsValid);
        Assert.Contains(result.Notes, n => n.Contains("默认地址"));       // 留空 = 默认地址
        Assert.Contains(result.Notes, n => n.Contains("重启"));           // 换方式后需重启
    }

    [Fact]
    public void Validate_OfficialMode_AlreadySavedSecret_DoesNotAskAgain()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "app-9527",
            AppSecretPlain = "",
            HasSavedAppSecret = true
        };

        Assert.True(FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input).IsValid);
    }

    [Fact]
    public void Validate_NapCatMode_EmptyExePath_Blocks()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = "",
            NapCatReversePort = 3001
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("可执行文件路径"));
        Assert.Contains(result.Errors, e => e.Contains("跳过这一步"));
    }

    [Fact]
    public void Validate_NapCatMode_WithoutWsUrlAndPort_Blocks()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = @"D:\NapCat\napcat\launcher-user.bat",
            NapCatWsUrl = "",
            NapCatReversePort = 0
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("正向 WS 地址"));
    }

    [Fact]
    public void Validate_NapCatMode_QuickLoginNeedsNumericQq()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = "NapCat.exe",
            NapCatReversePort = 3001,
            NapCatLoginMode = NapCatLoginMode.QuickLoginQQ,
            NapCatQuickLoginQQ = "机器人"
        };

        var invalid = FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, e => e.Contains("纯数字"));

        input.NapCatQuickLoginQQ = "123456789";
        Assert.True(FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input).IsValid);
    }

    [Fact]
    public void Validate_NapCatMode_Valid_PointsToNapCatDownloadAndRestart()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = "NapCat.exe",
            NapCatReversePort = 3001
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.Connection, input);

        Assert.True(result.IsValid);
        Assert.Contains(result.Notes, n => n.Contains("自己下载安装"));
        Assert.Contains(result.Notes, n => n.Contains("重启"));
    }

    // ==================== 第 3 步：接管范围（留空的真实含义） ====================

    [Fact]
    public void Validate_IngestScope_EmptyWhitelist_MeansUnlimited_AndIsNotBlocking()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            GroupWhitelist = []
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.IngestScope, input);

        Assert.True(result.IsValid); // 留空是合法选择，只提示不阻止
        Assert.Contains("留空 = 不限制", FirstRunWizardText.WhitelistEmptyMeaning);
        Assert.Contains(result.Notes, n => n.Contains("留空 = 不限制"));
        Assert.Contains(result.Notes, n => n.Contains("每个群"));
        Assert.DoesNotContain(result.Notes, n => n.Contains("不接管"));
        Assert.Contains(result.Notes, n => n.Contains("群 OpenID")); // 官方模式怎么拿群标识
    }

    [Fact]
    public void Validate_IngestScope_OfficiaMode_DoesNotRequireAnything()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            GroupWhitelist = ["8F3A2B4C"]
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.IngestScope, input);

        Assert.True(result.IsValid);
        Assert.Contains(result.Notes, n => n.Contains("只接管 1 个群"));
    }

    [Fact]
    public void Validate_IngestScope_NapCatMode_NonNumericGroup_Blocks()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            GroupWhitelist = ["123456789", "8F3A2B4C"]
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.IngestScope, input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("8F3A2B4C"));
        Assert.Contains(result.Errors, e => e.Contains("纯数字"));
    }

    // ==================== 第 4 步：发送目标群 ====================

    [Fact]
    public void Validate_SendTargets_EnabledButEmpty_BlocksWithSkipHint()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            HomeworkSendEnabled = true,
            TargetGroupOpenIds = []
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.SendTargets, input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("目标群"));
        Assert.Contains(result.Errors, e => e.Contains("跳过这一步"));
    }

    [Fact]
    public void Validate_SendTargets_Disabled_IsAllowedAndExplainsIndependence()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            HomeworkSendEnabled = false,
            TargetGroupOpenIds = []
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.SendTargets, input);

        Assert.True(result.IsValid);
        Assert.Contains(result.Notes, n => n.Contains("两件独立的事"));
    }

    [Fact]
    public void Validate_SendTargets_NapCatMode_NonNumericTarget_Blocks()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            HomeworkSendEnabled = true,
            TargetGroupOpenIds = ["8F3A2B4C"]
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.SendTargets, input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("群号"));
    }

    // ==================== 第 5 / 6 / 7 步 ====================

    [Fact]
    public void Validate_Classification_UsesDefaultKeywordsAsExamples()
    {
        var input = FirstRunWizardFlow.FromSettings(new AppSettings());

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.Classification, input);

        Assert.True(result.IsValid);
        Assert.Equal(new[] { "通知", "注意", "提醒", "广播" }, FirstRunWizardFlow.DefaultNoticeKeywords);
        Assert.Equal(new[] { "作业", "练习", "提交", "完成" }, FirstRunWizardFlow.DefaultHomeworkKeywords);
        Assert.Contains(result.Notes, n => n.Contains("哪边命中的关键词多"));
        Assert.Contains(result.Notes, n => n.Contains("词表编辑"));
    }

    [Fact]
    public void Validate_Overlays_AllHidden_WarnsWithoutBlocking()
    {
        var input = new FirstRunWizardInput
        {
            CircleVisible = false,
            NoticeVisible = false,
            HomeworkVisible = false,
            FilesVisible = false,
            ImageVisible = false
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.Overlays, input);

        Assert.True(result.IsValid);
        Assert.Contains(result.Notes, n => n.Contains("全部关掉"));
        Assert.Contains(result.Notes, n => n.Contains("桌面最底层"));
        Assert.False(FirstRunWizardFlow.AnyOverlayVisible(input));
    }

    [Fact]
    public void Validate_Overlays_VisibleListIsStableAndOrdered()
    {
        var input = new FirstRunWizardInput
        {
            CircleVisible = true,
            NoticeVisible = false,
            HomeworkVisible = true,
            FilesVisible = false,
            ImageVisible = true
        };

        Assert.True(FirstRunWizardFlow.AnyOverlayVisible(input));
        Assert.Equal(new[] { "圆圈栏", "作业", "图片" }, FirstRunWizardFlow.VisibleOverlayNames(input));
    }

    [Fact]
    public void Validate_FilesRetention_BadValues_Block()
    {
        var input = new FirstRunWizardInput
        {
            DownloadRoot = "   ",
            MaxDiskUsageMb = -1,
            MaxFileSizeMb = 0,
            NoticesRetentionDays = -1,
            HomeworkRetentionDays = -1
        };

        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.FilesRetention, input);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("下载目录"));
        Assert.Contains(result.Errors, e => e.Contains("单文件"));
        Assert.Contains(result.Errors, e => e.Contains("磁盘占用上限"));
        Assert.Contains(result.Errors, e => e.Contains("保留天数"));
    }

    [Fact]
    public void Validate_FilesRetention_Defaults_PassAndExplainDefaultFolder()
    {
        var result = FirstRunWizardFlow.Validate(FirstRunWizardStep.FilesRetention, new FirstRunWizardInput());

        Assert.True(result.IsValid);
        Assert.Contains(result.Notes, n => n.Contains("数据目录"));
    }

    // ==================== 写回设置 ====================

    [Fact]
    public void ApplyTo_Official_WritesFieldsAndProtectsSecret()
    {
        var settings = new AppSettings();
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "  app-9527  ",
            AppSecretPlain = "s3cret",
            ApiBase = "",
            TokenApiUrl = "   "
        };

        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.Connection, input, settings, plain => "enc:" + plain);

        var connection = settings.Connection;
        Assert.Equal(MessageConnectionMode.Official, connection.Mode);
        Assert.Equal("app-9527", connection.AppId);
        Assert.Equal("enc:s3cret", connection.AppSecretProtected);
        Assert.Equal(new ConnectionSettings().ApiBase, connection.ApiBase);            // 留空回落默认
        Assert.Equal(new ConnectionSettings().TokenApiUrl, connection.TokenApiUrl);
    }

    [Fact]
    public void ApplyTo_Official_EmptySecretInput_KeepsSavedSecret()
    {
        var settings = new AppSettings();
        settings.Connection.AppSecretProtected = "enc:old";
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "app-9527",
            AppSecretPlain = ""
        };

        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.Connection, input, settings, plain => "enc:" + plain);

        Assert.Equal("enc:old", settings.Connection.AppSecretProtected);
    }

    [Fact]
    public void ApplyTo_NapCat_WritesNapCatFieldsAndKeepsOfficialOnes()
    {
        var settings = new AppSettings();
        settings.Connection.AppId = "keep-me";
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = @"  D:\NapCat\napcat\launcher-user.bat  ",
            NapCatWsUrl = "ws://127.0.0.1:3001",
            NapCatReversePort = 99999,
            NapCatAccessTokenPlain = "tok",
            NapCatRunMode = NapCatRunMode.Framework,
            NapCatLoginMode = NapCatLoginMode.QuickLoginQQ,
            NapCatQuickLoginQQ = "123456789",
            NapCatAutoStart = true
        };

        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.Connection, input, settings, plain => "enc:" + plain);

        var connection = settings.Connection;
        Assert.Equal(MessageConnectionMode.NapCat, connection.Mode);
        Assert.Equal(@"D:\NapCat\napcat\launcher-user.bat", connection.NapCatExePath);
        Assert.Equal(65535, connection.NapCatReversePort);   // 越界夹紧
        Assert.Equal("enc:tok", connection.NapCatAccessTokenProtected);
        Assert.Equal(NapCatRunMode.Framework, connection.NapCatRunMode);
        Assert.Equal(NapCatLoginMode.QuickLoginQQ, connection.NapCatLoginMode);
        Assert.Equal("123456789", connection.NapCatQuickLoginQQ);
        Assert.True(connection.NapCatAutoStart);
        Assert.Equal("keep-me", connection.AppId);           // NapCat 步骤不动官方字段
    }

    [Fact]
    public void ApplyTo_GroupLists_TrimDedupeAndKeepOrder()
    {
        var settings = new AppSettings();
        var input = new FirstRunWizardInput
        {
            GroupWhitelist = [" 111 ", "", "111", "222"],
            TargetGroupOpenIds = ["333", " 333 "],
            HomeworkSendEnabled = false
        };

        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.IngestScope, input, settings);
        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.SendTargets, input, settings);

        Assert.Equal(new[] { "111", "222" }, settings.Connection.GroupWhitelist);
        Assert.Equal(new[] { "333" }, settings.Connection.TargetGroupOpenIds);
        Assert.False(settings.Connection.HomeworkSendEnabled);
    }

    [Fact]
    public void ApplyTo_ClassificationAndOverlays_WriteSelectedValues()
    {
        var settings = new AppSettings();
        var input = new FirstRunWizardInput
        {
            SubjectRecognitionMode = SubjectRecognitionMode.Keyword,
            SelectionWindowEnabled = false,
            NoticeSubjectPrefix = false,
            CircleVisible = true,
            CircleTopmost = true,
            NoticeVisible = false,
            HomeworkVisible = true,
            FilesVisible = true,
            ImageVisible = false,
            ImageTopmost = true,
            AutoOpenWithClass = true,
            AutoOpenDelaySeconds = -30
        };

        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.Classification, input, settings);
        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.Overlays, input, settings);

        Assert.Equal(SubjectRecognitionMode.Keyword, settings.SubjectRecognition.Mode);
        Assert.False(settings.SubjectRecognition.SelectionWindowEnabled);
        Assert.False(settings.Classification.NoticeSubjectPrefix);
        Assert.True(settings.Overlays.Circle.Visible);
        Assert.True(settings.Overlays.Circle.Topmost);
        Assert.False(settings.Overlays.Notice.Visible);
        Assert.True(settings.Overlays.Homework.Visible);
        Assert.True(settings.Overlays.Files.Visible);
        Assert.False(settings.Overlays.Image.Visible);
        Assert.True(settings.Overlays.Image.Topmost);
        Assert.True(settings.Overlays.SubjectCircle.AutoOpenWithClass);
        Assert.Equal(-30, settings.Overlays.SubjectCircle.AutoOpenDelaySeconds);
    }

    [Fact]
    public void ApplyTo_FilesRetention_DefaultsAndClamps()
    {
        var settings = new AppSettings();
        var input = new FirstRunWizardInput
        {
            DownloadRoot = "  ",
            GroupBySubject = false,
            MaxDiskUsageMb = -5,
            CleanupOldestWhenFull = true,
            MaxFileSizeMb = 0,
            NoticesRetentionDays = -1,
            HomeworkRetentionDays = 30
        };

        FirstRunWizardFlow.ApplyTo(FirstRunWizardStep.FilesRetention, input, settings);

        Assert.Equal("下载文件", settings.Files.DownloadRoot);
        Assert.False(settings.Files.GroupBySubject);
        Assert.Equal(0, settings.Files.MaxDiskUsageMb);
        Assert.Equal(1, settings.Files.CleanupPolicy);
        Assert.Equal(1, settings.Files.MaxFileSizeMb);
        Assert.Equal(0, settings.Maintenance.NoticesRetentionDays);
        Assert.Equal(30, settings.Maintenance.HomeworkRetentionDays);
    }

    [Fact]
    public void FromSettings_RoundTripsCurrentValues()
    {
        var settings = new AppSettings();
        settings.Connection.Mode = MessageConnectionMode.NapCat;
        settings.Connection.NapCatExePath = @"D:\NapCat\napcat\launcher-user.bat";
        settings.Connection.NapCatReversePort = 4001;
        settings.Connection.NapCatAccessTokenProtected = "enc-token";
        settings.Connection.GroupWhitelist = ["111"];
        settings.Connection.TargetGroupOpenIds = ["222"];
        settings.Connection.HomeworkSendEnabled = false;
        settings.Classification.NoticeSubjectPrefix = false;
        settings.SubjectRecognition.Mode = SubjectRecognitionMode.Keyword;
        settings.SubjectRecognition.SelectionWindowEnabled = false;
        settings.Overlays.Notice.Visible = false;
        settings.Overlays.Image.Visible = true;
        settings.Overlays.SubjectCircle.AutoOpenWithClass = true;
        settings.Overlays.SubjectCircle.AutoOpenDelaySeconds = 10;
        settings.Files.DownloadRoot = @"D:\班级文件";
        settings.Files.CleanupPolicy = 1;
        settings.Maintenance.HomeworkRetentionDays = 7;

        var input = FirstRunWizardFlow.FromSettings(settings, "plain-secret", "plain-token");

        Assert.Equal(MessageConnectionMode.NapCat, input.Mode);
        Assert.Equal(@"D:\NapCat\napcat\launcher-user.bat", input.NapCatExePath);
        Assert.Equal(4001, input.NapCatReversePort);
        Assert.Equal("plain-secret", input.AppSecretPlain);
        Assert.Equal("plain-token", input.NapCatAccessTokenPlain);
        Assert.False(input.HasSavedAppSecret);
        Assert.True(input.HasSavedNapCatAccessToken);
        Assert.Equal(new[] { "111" }, input.GroupWhitelist);
        Assert.Equal(new[] { "222" }, input.TargetGroupOpenIds);
        Assert.False(input.HomeworkSendEnabled);
        Assert.False(input.NoticeSubjectPrefix);
        Assert.Equal(SubjectRecognitionMode.Keyword, input.SubjectRecognitionMode);
        Assert.False(input.SelectionWindowEnabled);
        Assert.False(input.NoticeVisible);
        Assert.True(input.ImageVisible);
        Assert.True(input.AutoOpenWithClass);
        Assert.Equal(10, input.AutoOpenDelaySeconds);
        Assert.Equal(@"D:\班级文件", input.DownloadRoot);
        Assert.True(input.CleanupOldestWhenFull);
        Assert.Equal(7, input.HomeworkRetentionDays);
    }

    [Fact]
    public void IsConnectionConfigured_RequiresRealConnectionOptions()
    {
        Assert.False(FirstRunWizardFlow.IsConnectionConfigured(new FirstRunWizardInput()));
        Assert.True(FirstRunWizardFlow.IsConnectionConfigured(new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "app-1",
            AppSecretPlain = "s"
        }));
        Assert.True(FirstRunWizardFlow.IsConnectionConfigured(new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "app-1",
            AppSecretPlain = "",
            HasSavedAppSecret = true
        }));

        // NapCat：光有可执行文件路径不算配好，还得有一种连接方式
        Assert.False(FirstRunWizardFlow.IsConnectionConfigured(new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = "NapCat.exe",
            NapCatReversePort = 0,
            NapCatWsUrl = ""
        }));
        Assert.True(FirstRunWizardFlow.IsConnectionConfigured(new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = "NapCat.exe",
            NapCatReversePort = 0,
            NapCatWsUrl = "ws://127.0.0.1:3001"
        }));
    }

    // ==================== 完成页汇总 ====================

    [Fact]
    public void Summary_SkippedAndUnconfiguredSteps_AreListedWithMakeUpPath()
    {
        var flow = new FirstRunWizardFlow();
        flow.Next();      // 连接方式
        flow.SkipStep();  // 跳过连接
        flow.SkipStep();  // 跳过接管范围
        flow.SkipStep();  // 跳过发送目标
        Assert.Equal(FirstRunWizardStep.Classification, flow.Current);

        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "",
            AppSecretPlain = "",
            GroupWhitelist = [],
            HomeworkSendEnabled = true,
            TargetGroupOpenIds = [],
            CircleVisible = false,
            NoticeVisible = false,
            HomeworkVisible = false,
            FilesVisible = false,
            ImageVisible = false
        };

        var summary = FirstRunWizardFlow.BuildSummary(input, flow.SkippedSteps);

        Assert.True(summary.HasMissing);
        // 连接方式：跳过了 + 没配 → 必须列出并写明到哪里补
        var connection = Assert.Single(summary.Missing.Where(line => line.Text.Contains("连接方式")));
        Assert.Contains("跳过", connection.Text);
        Assert.Contains("连接", connection.MakeUpHint);
        // 发送目标群：开关开着却没填
        Assert.Contains(summary.Missing, line => line.Text.Contains("目标群") && line.MakeUpHint.Contains("目标群"));
        // 悬浮窗全关
        Assert.Contains(summary.Missing, line => line.Text.Contains("悬浮窗"));
        // 跳过的步骤在提醒里被点名，白名单留空按"不限制"解释
        Assert.Contains(summary.Notes, n => n.Contains("被跳过的步骤") && n.Contains("连接方式"));
        Assert.Contains(summary.Notes, n => n.Contains("不限制"));

        var text = summary.ToPlainText();
        Assert.Contains("已配置：", text);
        Assert.Contains("还没配好", text);
        Assert.Contains("提醒：", text);
        Assert.Contains("到「CyberTechRep 连接」设置页补", text);
    }

    [Fact]
    public void Summary_FullyConfigured_ReportsNothingMissing()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.Official,
            AppId = "app-9527",
            AppSecretPlain = "s3cret",
            GroupWhitelist = ["111111", "222222"],
            TargetGroupOpenIds = ["333333"],
            HomeworkSendEnabled = true,
            SubjectRecognitionMode = SubjectRecognitionMode.MemberSelection,
            SelectionWindowEnabled = true,
            NoticeSubjectPrefix = true,
            CircleVisible = true,
            NoticeVisible = true,
            HomeworkVisible = true,
            DownloadRoot = "下载文件",
            MaxDiskUsageMb = 2048,
            MaxFileSizeMb = 512
        };

        var summary = FirstRunWizardFlow.BuildSummary(input);

        Assert.False(summary.HasMissing);
        Assert.Empty(summary.Missing);
        Assert.Contains(summary.Configured, line => line.Text.Contains("QQ 官方机器人"));
        Assert.Contains(summary.Configured, line => line.Text.Contains("2 个群"));
        Assert.Contains(summary.Configured, line => line.Text.Contains("1 个目标群"));
        Assert.Contains(summary.Configured, line => line.Text.Contains("按发送者绑定优先"));
        Assert.Contains(summary.Configured, line => line.Text.Contains("圆圈栏"));
        Assert.Contains(summary.Notes, n => n.Contains("重启一次 ClassIsland"));
        Assert.Contains(summary.Notes, n => n.Contains("重新打开引导"));
        Assert.Contains(summary.Notes, n => n.Contains("词表编辑"));
        Assert.Contains("还没配好：无，全部配好了。", summary.ToPlainText());
    }

    [Fact]
    public void Summary_NapCatConnection_DescribesRunAndLoginMode()
    {
        var input = new FirstRunWizardInput
        {
            Mode = MessageConnectionMode.NapCat,
            NapCatExePath = @"D:\NapCat\napcat\launcher-user.bat",
            NapCatReversePort = 3001,
            NapCatRunMode = NapCatRunMode.Framework,
            NapCatLoginMode = NapCatLoginMode.QuickLoginQQ,
            NapCatQuickLoginQQ = "123456789",
            TargetGroupOpenIds = ["123456789"]
        };

        var summary = FirstRunWizardFlow.BuildSummary(input);

        Assert.Contains(summary.Configured, line => line.Text.Contains("NapCat") && line.Text.Contains("有头"));
        Assert.Contains(summary.Configured, line => line.Text.Contains("QQ 号快速登录"));
    }

    [Fact]
    public void Summary_HiddenOverlays_AreReportedMissingWithOverlayPage()
    {
        var input = new FirstRunWizardInput
        {
            AppId = "app-1",
            AppSecretPlain = "s",
            TargetGroupOpenIds = ["1"],
            CircleVisible = false,
            NoticeVisible = false,
            HomeworkVisible = false,
            FilesVisible = false,
            ImageVisible = false
        };

        var summary = FirstRunWizardFlow.BuildSummary(input);

        var overlay = Assert.Single(summary.Missing.Where(line => line.Text.Contains("悬浮窗")));
        Assert.Contains("悬浮窗", overlay.MakeUpHint);
    }

    // ==================== 文案纪律 ====================

    [Fact]
    public void WizardCopy_DoesNotLeakInternalJargon()
    {
        var samples = new List<string>();
        foreach (var step in FirstRunWizardSteps.Order)
        {
            samples.Add(FirstRunWizardSteps.Title(step));
            samples.Add(FirstRunWizardSteps.MakeUpHint(step));
            samples.Add(FirstRunWizardSteps.NextButtonText(step));
            samples.Add(FirstRunWizardSteps.SkipButtonText(step));
            samples.Add(FirstRunWizardSteps.SavedMessage(step));
        }

        samples.AddRange(
        [
            FirstRunWizardText.WhitelistEmptyMeaning,
            FirstRunWizardText.TargetGroupsIndependent,
            FirstRunWizardText.OverlayLayering,
            FirstRunWizardText.RestartAfterModeChange,
            FirstRunWizardText.ReopenWizardHint,
            FirstRunWizardText.KeywordEditHint,
            FirstRunWizardText.OfficialGroupIdSource,
            FirstRunWizardText.NapCatGroupIdSource,
            FirstRunWizardText.WebUiAutoOpenClosedOnFinish
        ]);

        // 每步的提示与错误文案（空着填 / 填满两种状态都覆盖）
        var empty = new FirstRunWizardInput();
        var filled = new FirstRunWizardInput
        {
            AppId = "a",
            AppSecretPlain = "s",
            GroupWhitelist = ["1"],
            TargetGroupOpenIds = ["1"],
            CircleVisible = true,
            NoticeVisible = true,
            HomeworkVisible = true,
            DownloadRoot = "下载文件"
        };
        foreach (var step in FirstRunWizardSteps.Order)
        {
            samples.AddRange(FirstRunWizardFlow.Validate(step, empty).Errors);
            samples.AddRange(FirstRunWizardFlow.Validate(step, empty).Notes);
            samples.AddRange(FirstRunWizardFlow.Validate(step, filled).Errors);
            samples.AddRange(FirstRunWizardFlow.Validate(step, filled).Notes);
        }

        samples.Add(FirstRunWizardFlow.BuildSummary(empty, FirstRunWizardSteps.Order).ToPlainText());
        samples.Add(FirstRunWizardFlow.BuildSummary(filled).ToPlainText());

        foreach (var sample in samples.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            foreach (var word in ForbiddenWords)
            {
                Assert.DoesNotContain(word, sample);
            }
        }
    }

    [Fact]
    public void WizardWindowXamlCopy_DoesNotLeakInternalJargon_AndKeepsKeyGuidance()
    {
        var xamlPath = FindWizardXaml();
        Assert.True(xamlPath is not null, "未找到 FirstRunWizardWindow.axaml（本测试需在仓库内运行）");
        var xaml = File.ReadAllText(xamlPath!);

        foreach (var word in ForbiddenWords)
        {
            Assert.DoesNotContain(word, xaml);
        }

        // 关键的"会困惑 / 必须说清"的说明不能写丢：
        Assert.Contains("留空 = 不限制", xaml);   // 群白名单留空的真实含义
        Assert.Contains("两件独立", xaml);         // 白名单 ≠ 发送目标群
        Assert.Contains("桌面最底层", xaml);       // 默认层级 vs 置顶
        Assert.Contains("怎么验证", xaml);         // 每步都有验证动作
        Assert.Contains("整理并发送", xaml);
        Assert.Contains("已读", xaml);
        Assert.Contains("转为作业", xaml);
        Assert.Contains("重新打开引导", xaml);     // 重看引导的位置
        Assert.Contains("一键启动", xaml);         // NapCat 指向连接设置页
    }

    /// <summary>从测试输出目录向上找到引导窗口 XAML（Debug / Release 布局都适用）。</summary>
    private static string? FindWizardXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CyberTechRep.Plugin", "Views", "FirstRunWizardWindow.axaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
