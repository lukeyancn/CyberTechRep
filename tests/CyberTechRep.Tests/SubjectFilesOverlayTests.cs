using System.Runtime.Versioning;
using System.Text.Json;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>模块 10：学科圆圈/文件悬浮窗设置默认值与旧配置兼容加载。</summary>
public sealed class SubjectCircleSettingsTests
{
    [Fact]
    public void Defaults_FilesHidden_CircleShown_VerticalOrder_IconsView()
    {
        var settings = new OverlaySettings();

        // 第三悬浮窗默认不随宿主显示；圆圈启动器默认显示
        Assert.False(settings.Files.Visible);
        Assert.True(settings.Circle.Visible);
        Assert.Equal(SubjectCircleBarOptions.OrientationVertical, settings.SubjectCircle.Orientation);
        Assert.Equal(SubjectCircleBarOptions.ViewModeIcons, settings.SubjectCircle.ViewMode);
        Assert.False(settings.SubjectCircle.AutoOpenWithClass);
        Assert.Empty(settings.SubjectCircle.Order);
    }

    [Fact]
    public void LegacyJsonWithoutNewFields_LoadsWithDefaults()
    {
        // 旧 settings.json（缺 Files/Circle/SubjectCircle 字段）反序列化后取默认值，不抛异常
        const string legacy = """
            {
              "SchemaVersion": 1,
              "Overlays": {
                "Notice": { "X": 100 },
                "Homework": { "Visible": true },
                "LaunchWithHost": true
              }
            }
            """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(legacy);

        Assert.NotNull(loaded);
        Assert.Equal(100, loaded!.Overlays.Notice.X);
        Assert.False(loaded.Overlays.Files.Visible);
        Assert.True(loaded.Overlays.Circle.Visible);
        Assert.NotNull(loaded.Overlays.SubjectCircle);
        Assert.Equal(SubjectCircleBarOptions.ViewModeIcons, loaded.Overlays.SubjectCircle.ViewMode);
    }

    [Fact]
    public void ViewModeAndOrientation_NonCanonicalValuesFallBackSafe()
    {
        // 未知值向前兼容：视图模式非 Icons 按详细列表、方向非 Horizontal 按纵向
        Assert.True(SubjectCircleOrder.IsDetailsView("Details"));
        Assert.True(SubjectCircleOrder.IsDetailsView("whatever"));
        Assert.False(SubjectCircleOrder.IsDetailsView("icons"));
        Assert.True(SubjectCircleOrder.IsVertical("Vertical"));
        Assert.False(SubjectCircleOrder.IsVertical("Horizontal"));
    }
}

/// <summary>模块 10：学科文件过滤/日期分组纯逻辑。</summary>
public sealed class SubjectFilesQueryTests
{
    private static FileRecord Record(string subject, string date, string name, DateTimeOffset? createdAt = null)
    {
        var created = createdAt ?? DateTimeOffset.Parse($"{date}T08:00:00");
        return new FileRecord
        {
            MessageId = Guid.NewGuid().ToString("N"),
            FileName = name,
            Status = FileStatus.Archived,
            ArchivedRelativePath = $"{subject}/{date}/{name}",
            CreatedAt = created
        };
    }

    [Fact]
    public void FilterBySubject_OnlyArchivedOfThatSubject_NewestFirst()
    {
        var records = new[]
        {
            Record("数学", "2026-09-01", "a.pdf", DateTimeOffset.Parse("2026-09-01T08:00:00")),
            Record("数学", "2026-09-03", "b.pdf", DateTimeOffset.Parse("2026-09-03T08:00:00")),
            Record("语文", "2026-09-02", "c.pdf"),
            new FileRecord { MessageId = "m", FileName = "d.pdf", Status = FileStatus.Failed,
                ArchivedRelativePath = "数学/2026-09-04/d.pdf", CreatedAt = DateTimeOffset.Parse("2026-09-04T08:00:00") }
        };

        var result = SubjectFilesQuery.FilterBySubject(records, "数学");

        Assert.Equal(2, result.Count);
        Assert.Equal("b.pdf", result[0].FileName);
        Assert.Equal("a.pdf", result[1].FileName);
    }

    [Fact]
    public void FilterBySubject_BackslashPathSeparator_AlsoMatches()
    {
        var record = new FileRecord
        {
            MessageId = "m",
            FileName = "a.pdf",
            Status = FileStatus.Archived,
            ArchivedRelativePath = @"数学\2026-09-01\a.pdf",
            CreatedAt = DateTimeOffset.Parse("2026-09-01T08:00:00")
        };

        Assert.Single(SubjectFilesQuery.FilterBySubject([record], "数学"));
        Assert.Empty(SubjectFilesQuery.FilterBySubject([record], "语文"));
    }

    [Fact]
    public void GroupByDate_GroupsDescByDate_KeepsItemOrder()
    {
        var records = new[]
        {
            Record("数学", "2026-09-01", "old.pdf"),
            Record("数学", "2026-09-03", "new1.pdf"),
            Record("数学", "2026-09-03", "new2.pdf")
        };

        var groups = SubjectFilesQuery.GroupByDate(records);

        Assert.Equal(2, groups.Count);
        Assert.Equal("2026-09-03", groups[0].Date);
        Assert.Equal(2, groups[0].Items.Count);
        Assert.Equal("2026-09-01", groups[1].Date);
    }

    [Fact]
    public void ExtractSubject_FirstSegmentIsSubject_DateSegmentSkippedForUngrouped()
    {
        Assert.Equal("数学", SubjectFilesQuery.ExtractSubject("数学/2026-09-01/a.pdf"));
        Assert.Equal("数学", SubjectFilesQuery.ExtractSubject(@"数学\2026-09-01\a.pdf"));
        // 未按学科分组归档（首段是日期）→ 不可归属学科
        Assert.Null(SubjectFilesQuery.ExtractSubject("2026-09-01/a.pdf"));
        Assert.Null(SubjectFilesQuery.ExtractSubject(null));
        Assert.Null(SubjectFilesQuery.ExtractSubject("只有一个段"));
    }
}

/// <summary>模块 10：圆圈顺序解析与上移/下移重排。</summary>
public sealed class SubjectCircleOrderTests
{
    [Fact]
    public void ResolveOrder_OrderFirst_ThenFixed_ThenDiscovered()
    {
        // 配置顺序打乱固定学科 + files.json 出现过「信息技术」
        IReadOnlyList<string> order = ["英语", "数学"];
        var result = SubjectCircleOrder.ResolveOrder(order, ["信息技术", "数学"]);

        // ①配置顺序 ②未列出的固定学科（固定序） ③其余发现学科（名称序）
        Assert.Equal(
            ["英语", "数学", "语文", "物理", "化学", "生物", "其他", "信息技术"],
            result);
    }

    [Fact]
    public void ResolveOrder_NullOrEmptyOrder_FixedOrderOnly()
    {
        Assert.Equal(SubjectCircleOrder.BaseSubjects, SubjectCircleOrder.ResolveOrder(null, []));
        Assert.Equal(SubjectCircleOrder.BaseSubjects, SubjectCircleOrder.ResolveOrder([], ["语文"]));
    }

    [Fact]
    public void ResolveOrder_UnknownEntriesAndDuplicates_Ignored()
    {
        IReadOnlyList<string> order = ["体育", "数学", "数学", ""];
        var result = SubjectCircleOrder.ResolveOrder(order, ["语文"]);

        Assert.Equal("数学", result[0]);
        Assert.Equal("语文", result[1]);
        Assert.DoesNotContain("体育", result);
        // 数学（配置序）+ 固定七学科（语文替换掉重复的固定项）= 7
        Assert.Equal(7, result.Count);
    }

    [Fact]
    public void Move_SwapsAndClamps()
    {
        IReadOnlyList<string> subjects = ["语文", "数学", "英语"];

        // 正常相邻交换
        Assert.Equal(["数学", "语文", "英语"], SubjectCircleOrder.Move(subjects, "数学", -1));
        Assert.Equal(["语文", "英语", "数学"], SubjectCircleOrder.Move(subjects, "数学", 1));
        // 越界收敛到端点（语文已在最上，delta=5 与末位交换）
        Assert.Equal(subjects, SubjectCircleOrder.Move(subjects, "语文", -1));
        Assert.Equal(["英语", "数学", "语文"], SubjectCircleOrder.Move(subjects, "语文", 5));
        // 不存在/无位移 → 原样
        Assert.Equal(subjects, SubjectCircleOrder.Move(subjects, "不存在", 1));
        Assert.Equal(subjects, SubjectCircleOrder.Move(subjects, "数学", 0));
    }
}

/// <summary>模块 10：SuspensionWindowController 对 files/circle 两个新 overlayKey 的设置持久化。</summary>
[SupportedOSPlatform("windows")]
public sealed class SubjectOverlayKeysControllerTests : IDisposable
{
    private readonly string _dir;

    public SubjectOverlayKeysControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "subject-overlays", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task ApplySettings_FilesAndCircle_PersistAcrossInstances()
    {
        var controller = new SuspensionWindowController(_dir);

        await controller.ApplySettingsAsync(SuspensionWindowController.FilesKey,
            new OverlayWindowSettings { X = 111, Y = 222, Visible = false });
        await controller.ApplySettingsAsync(SuspensionWindowController.CircleKey,
            new OverlayWindowSettings { X = 333, Y = 444, Opacity = 0.7 });

        var reloaded = new SuspensionWindowController(_dir);
        Assert.Equal(111, reloaded.Settings.Files.X);
        Assert.Equal(222, reloaded.Settings.Files.Y);
        Assert.Equal(333, reloaded.Settings.Circle.X);
        Assert.Equal(0.7, reloaded.Settings.Circle.Opacity);
        // 原有两窗不受影响
        Assert.Equal(new OverlayWindowSettings().X, reloaded.Settings.Notice.X);
        Assert.Equal(new OverlayWindowSettings().X, reloaded.Settings.Homework.X);
    }

    [Fact]
    public async Task FilesAndCircle_AreKnownKeys_ResetAndOnScreenWork()
    {
        var controller = new SuspensionWindowController(_dir);
        await controller.ApplySettingsAsync(SuspensionWindowController.CircleKey,
            new OverlayWindowSettings { X = 3000, Y = 2000 });

        await controller.ResetPositionAsync(SuspensionWindowController.CircleKey);

        var reloaded = new SuspensionWindowController(_dir);
        Assert.Equal(SuspensionWindowController.DefaultX, reloaded.Settings.Circle.X);
        Assert.True(controller.IsOnScreen(SuspensionWindowController.FilesKey));
        Assert.False(controller.IsOnScreen("bogus"));
    }

    [Fact]
    public void Settings_ReturnsDefensiveCopy_IncludingNewGroups()
    {
        var controller = new SuspensionWindowController(_dir);

        var copy = controller.Settings;
        copy.Files.X = 999;
        copy.Circle.X = 999;
        copy.SubjectCircle.Order = ["x"];

        Assert.NotEqual(999, controller.Settings.Files.X);
        Assert.NotEqual(999, controller.Settings.Circle.X);
        Assert.Empty(controller.Settings.SubjectCircle.Order);
    }
}
