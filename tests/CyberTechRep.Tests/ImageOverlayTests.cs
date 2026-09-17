using System.Runtime.Versioning;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 需求 9：图片悬浮窗纯逻辑——可展示扩展名、说明文案、解码宽度上限、列表上限与去重、
/// 「当前图片」选择（视口中心最近）。
/// </summary>
public sealed class ImageOverlayLogicTests
{
    private static FileRecord MakeRecord(string fileName, DateTimeOffset? createdAt = null) => new()
    {
        MessageId = $"m-{fileName}",
        FileName = fileName,
        Status = FileStatus.Archived,
        CreatedAt = createdAt ?? new DateTimeOffset(2026, 9, 17, 21, 30, 0, TimeSpan.Zero)
    };

    [Theory]
    [InlineData("photo.png", true)]
    [InlineData("photo.PNG", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.JPEG", true)]
    [InlineData("photo.jfif", true)]
    [InlineData("photo.gif", true)]
    [InlineData("photo.BMP", true)]
    [InlineData("photo.webp", true)]
    [InlineData("archive.zip", false)]
    [InlineData("video.mp4", false)]
    [InlineData("document.pdf", false)]
    [InlineData("no-extension", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsImageFile_VariousExtensions_MatchesExpected(string? fileName, bool expected)
    {
        Assert.Equal(expected, ImageOverlayLogic.IsImageFile(fileName));
    }

    [Fact]
    public void FormatCaption_WithSubject_IncludesFileNameTimeAndSubject()
    {
        var record = MakeRecord("photo.png");

        var caption = ImageOverlayLogic.FormatCaption(record, " 数学 ");

        // 时间按本地时区格式化（断言用同一转换，保证跨时区稳定）
        var time = record.CreatedAt.LocalDateTime;
        Assert.Equal($"photo.png · {time:MM-dd HH:mm} · 数学", caption);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FormatCaption_WithoutSubject_OmitsSubjectSegment(string? subject)
    {
        var record = MakeRecord("photo.png");

        var caption = ImageOverlayLogic.FormatCaption(record, subject);

        var time = record.CreatedAt.LocalDateTime;
        Assert.Equal($"photo.png · {time:MM-dd HH:mm}", caption);
    }

    [Fact]
    public void FormatCaption_PrefersCompletedAt()
    {
        var record = new FileRecord
        {
            MessageId = "m1",
            FileName = "photo.png",
            CreatedAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 9, 17, 9, 15, 0, TimeSpan.Zero)
        };

        var caption = ImageOverlayLogic.FormatCaption(record, null);

        Assert.Contains(record.CompletedAt!.Value.LocalDateTime.ToString("MM-dd HH:mm"), caption);
    }

    [Theory]
    [InlineData(300, 1.0, 640)]    // 窄窗：clamp 到最小解码宽度
    [InlineData(1000, 1.0, 1000)]  // 区间内：按窗口宽度
    [InlineData(2000, 1.0, 1600)]  // 宽窗：clamp 到最大解码宽度（避免原图全尺寸解码）
    [InlineData(1000, 2.0, 1600)]  // 高 DPI 换算后 clamp
    [InlineData(500, 1.5, 750)]    // DIP × 缩放
    [InlineData(double.NaN, 1.0, 800)] // 非法宽度：兜底
    [InlineData(0, 1.0, 800)]
    [InlineData(-100, 1.0, 800)]
    [InlineData(1000, 0, 1000)]        // 非法缩放按 1.0
    [InlineData(1000, double.NaN, 1000)]
    public void ResolveDecodeWidth_ClampsToConfiguredRange(double windowWidth, double scaling, int expected)
    {
        Assert.Equal(expected, ImageOverlayLogic.ResolveDecodeWidth(windowWidth, scaling));
    }
}

/// <summary>需求 9：图片列表纯逻辑——顺序累积、同 Id 去重、超过上限丢弃最旧、当前图片选择。</summary>
public sealed class ImageOverlayListTests
{
    private static FileRecord MakeRecord(Guid id, string fileName = "a.png") => new()
    {
        Id = id,
        MessageId = $"m-{id:N}",
        FileName = fileName,
        Status = FileStatus.Archived
    };

    [Fact]
    public void Add_AppendsInArrivalOrder()
    {
        var list = new ImageOverlayList();
        var first = MakeRecord(Guid.NewGuid(), "1.png");
        var second = MakeRecord(Guid.NewGuid(), "2.png");

        Assert.True(list.Add(first).Added);
        Assert.True(list.Add(second).Added);

        Assert.Equal(2, list.Count);
        Assert.Equal([first, second], list.Records);
    }

    [Fact]
    public void Add_SameId_IsIgnored()
    {
        var list = new ImageOverlayList();
        var id = Guid.NewGuid();
        list.Add(MakeRecord(id, "1.png"));

        var result = list.Add(MakeRecord(id, "1-again.png"));

        Assert.False(result.Added);
        Assert.True(result.Duplicate);
        Assert.Equal(1, list.Count);
        Assert.Equal("1.png", list.Records[0].FileName); // 保留先到的那张
    }

    [Fact]
    public void Add_ExceedsCapacity_EvictsOldest()
    {
        var list = new ImageOverlayList();
        var records = Enumerable.Range(0, ImageOverlayLogic.MaxItems)
            .Select(i => MakeRecord(Guid.NewGuid(), $"{i}.png"))
            .ToList();
        foreach (var record in records)
        {
            list.Add(record);
        }

        Assert.Equal(ImageOverlayLogic.MaxItems, list.Count);

        var extra = MakeRecord(Guid.NewGuid(), "extra.png");
        var result = list.Add(extra);

        Assert.True(result.Added);
        Assert.NotNull(result.Evicted);
        Assert.Equal(records[0].Id, result.Evicted!.Id);       // 淘汰最旧一张
        Assert.Equal(ImageOverlayLogic.MaxItems, list.Count);   // 数量保持上限
        Assert.Equal(extra.Id, list.Records[^1].Id);            // 新图在末尾
        Assert.DoesNotContain(list.Records, r => r.Id == records[0].Id);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var list = new ImageOverlayList();
        list.Add(MakeRecord(Guid.NewGuid()));
        list.Clear();

        Assert.Equal(0, list.Count);
        // 清空后同一 Id 可再次加入（去重集合一并复位）
        var id = Guid.NewGuid();
        list.Add(MakeRecord(id));
        list.Clear();
        Assert.True(list.Add(MakeRecord(id)).Added);
    }

    [Fact]
    public void FindNearestIndex_PicksClosestToViewportCenter()
    {
        // 条目中心 50/200/600，视口中心 220 → 第 2 条（200）最近
        Assert.Equal(1, ImageOverlayList.FindNearestIndex([50, 200, 600], 220));
        Assert.Equal(2, ImageOverlayList.FindNearestIndex([50, 200, 600], 900));
        Assert.Equal(0, ImageOverlayList.FindNearestIndex([50, 200, 600], 0));
    }

    [Fact]
    public void FindNearestIndex_IgnoresUnknownCenters()
    {
        // NaN（容器未实现）跳过；全未知 → -1（调用方回落最新一张）
        Assert.Equal(2, ImageOverlayList.FindNearestIndex([double.NaN, double.NaN, 300], 0));
        Assert.Equal(-1, ImageOverlayList.FindNearestIndex([double.NaN, double.NaN], 100));
        Assert.Equal(-1, ImageOverlayList.FindNearestIndex([], 100));
    }
}

/// <summary>需求 9：图片悬浮窗（窗口级集成）——加入/去重/上限与「打开」按钮启用状态。</summary>
[SupportedOSPlatform("windows")]
public sealed class ImageSuspensionWindowTests
{
    private static FileRecord MakeRecord(string fileName)
    {
        var id = Guid.NewGuid();
        return new FileRecord
        {
            Id = id,
            MessageId = $"m-{id:N}",
            FileName = fileName,
            Status = FileStatus.Archived,
            ArchivedRelativePath = $"数学/2026-09-17/{fileName}",
            CreatedAt = new DateTimeOffset(2026, 9, 17, 21, 30, 0, TimeSpan.Zero)
        };
    }

    [Fact]
    public Task AddImage_AccumulatesInOrder_AndDeduplicatesSameId()
    {
        // 路径解析委托返回 null：不做真实解码（不依赖图片文件），只验证列表/去重/计数
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = new ImageSuspensionWindow(_ => null);
            var first = MakeRecord("1.png");
            var second = MakeRecord("2.png");

            window.AddImage(first);
            window.AddImage(first); // 同一 FileRecord.Id 重复到达 → 忽略
            window.AddImage(second);

            Assert.Equal(2, window.ImageCount);
            Assert.Equal(["1.png", "2.png"], window.Images.Select(r => r.FileName).ToList());
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task AddImage_ExceedsCapacity_DropsOldest()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var window = new ImageSuspensionWindow(_ => null);
            var records = Enumerable.Range(0, ImageOverlayLogic.MaxItems + 1)
                .Select(i => MakeRecord($"{i}.png"))
                .ToList();

            foreach (var record in records)
            {
                window.AddImage(record);
            }

            Assert.Equal(ImageOverlayLogic.MaxItems, window.ImageCount);
            Assert.DoesNotContain(window.Images, r => r.Id == records[0].Id);   // 最旧一张被丢弃
            Assert.Equal(records[^1].FileName, window.Images[^1].FileName);      // 新图在末尾
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AddImage_DecodesRealPngFile()
    {
        // 真实解码路径：临时写一张合法 PNG，验证按窗口宽度解码成功（headless + Skia）
        var dir = Path.Combine(Path.GetTempPath(), "classing-tests", "image-decode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "real.png");
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(8, 8))
            {
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            await AvaloniaTestSetup.Session.Dispatch(() =>
            {
                var window = new ImageSuspensionWindow(_ => path);
                window.AddImage(MakeRecord("real.png"));

                Assert.Equal(1, window.ImageCount);
                Assert.True(window.ItemViews[0].HasImage); // 解码成功（文件存在且为合法 PNG）

                // 同一 Id 再次到达不会被解码/重复加入
                var again = window.Images[0];
                window.AddImage(again);
                Assert.Equal(1, window.ImageCount);
                return Task.CompletedTask;
            }, CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }
}

/// <summary>需求 9：SuspensionWindowController 对 image 这个新 overlayKey 的设置持久化与默认值。</summary>
[SupportedOSPlatform("windows")]
public sealed class ImageOverlayKeysControllerTests : IDisposable
{
    private readonly string _dir;

    public ImageOverlayKeysControllerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "image-overlays", Guid.NewGuid().ToString("N"));
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
    public void Defaults_ImageHidden420x560_AutoShowOnReceiveEnabled()
    {
        var settings = new OverlaySettings();

        Assert.False(settings.Image.Visible); // 默认不随宿主显示
        Assert.Equal(420, settings.Image.Width);
        Assert.Equal(560, settings.Image.Height);
        Assert.True(settings.ImageAutoShowOnReceive); // 需求 9：收到图片自动展示默认开启
        Assert.Equal(0, settings.SubjectCircle.AutoOpenDelaySeconds); // 需求 10：默认准点
    }

    [Fact]
    public async Task ApplySettings_ImageKey_PersistsAcrossInstances()
    {
        var controller = new SuspensionWindowController(_dir);

        await controller.ApplySettingsAsync(SuspensionWindowController.ImageKey,
            new OverlayWindowSettings { X = 123, Y = 456, Width = 500, Height = 700, Visible = false });

        var reloaded = new SuspensionWindowController(_dir);
        Assert.Equal(123, reloaded.Settings.Image.X);
        Assert.Equal(456, reloaded.Settings.Image.Y);
        Assert.Equal(500, reloaded.Settings.Image.Width);
        Assert.Equal(700, reloaded.Settings.Image.Height);
        Assert.False(reloaded.Settings.Image.Visible);
        // 其余窗不受影响
        Assert.Equal(new OverlaySettings().Notice.X, reloaded.Settings.Notice.X);
    }

    [Fact]
    public async Task Image_IsKnownKey_ResetAndOnScreenWork()
    {
        var controller = new SuspensionWindowController(_dir);
        await controller.ApplySettingsAsync(SuspensionWindowController.ImageKey,
            new OverlayWindowSettings { X = 3000, Y = 2000 });

        await controller.ResetPositionAsync(SuspensionWindowController.ImageKey);

        var reloaded = new SuspensionWindowController(_dir);
        Assert.Equal(SuspensionWindowController.DefaultX, reloaded.Settings.Image.X);
        Assert.Equal(SuspensionWindowController.DefaultY, reloaded.Settings.Image.Y);
        Assert.True(controller.IsOnScreen(SuspensionWindowController.ImageKey));
    }

    [Fact]
    public void Settings_ReturnsDefensiveCopy_IncludingImage()
    {
        var controller = new SuspensionWindowController(_dir);

        var copy = controller.Settings;
        copy.Image.X = 999;

        Assert.NotEqual(999, controller.Settings.Image.X);
    }

    [Fact]
    public void Settings_DeepClone_CarriesImageWindowAndAutoOpenDelay()
    {
        // 单一来源模式：Settings 深拷贝必须带上 Image 窗口设置与 SubjectCircle.AutoOpenDelaySeconds，
        // 否则拖拽回写/导入回放会丢这些新字段
        var settings = new SettingsService(_dir);
        settings.Current.Overlays.Image.Width = 512;
        settings.Current.Overlays.SubjectCircle.AutoOpenDelaySeconds = -17;
        var controller = new SuspensionWindowController(_dir, settingsService: settings);

        Assert.Equal(512, controller.Settings.Image.Width);
        Assert.Equal(-17, controller.Settings.SubjectCircle.AutoOpenDelaySeconds);
    }

    [Fact]
    public async Task LegacyOverlaysJson_WithoutImageFields_ImportsWithoutLosingDefaults()
    {
        // 旧 overlays.json（2.0 及更早）：没有 image / imageAutoShowOnReceive 字段——
        // 反序列化得到默认值，导入不抛异常、不丢既有窗配置（camelCase 属性名与 JsonStoreFile 一致）
        var legacyPath = Path.Combine(_dir, SuspensionWindowController.LegacyFileName);
        await File.WriteAllTextAsync(legacyPath, """
            {
              "notice": { "x": 123, "y": 456, "width": 500, "height": 400, "opacity": 1,
                          "fontSize": 14, "topmost": true, "pinned": false,
                          "clickThrough": false, "visible": true },
              "launchWithHost": false
            }
            """);

        var settings = new SettingsService(_dir);
        var controller = new SuspensionWindowController(_dir, settingsService: settings);

        // 旧字段照常导入
        Assert.Equal(123, controller.Settings.Notice.X);
        Assert.False(controller.Settings.LaunchWithHost);
        // 新字段取默认值（Hidden/420x560；需求 10 延时默认准点）
        Assert.False(controller.Settings.Image.Visible);
        Assert.Equal(420, controller.Settings.Image.Width);
        Assert.Equal(560, controller.Settings.Image.Height);
        Assert.Equal(0, controller.Settings.SubjectCircle.AutoOpenDelaySeconds);
    }
}
