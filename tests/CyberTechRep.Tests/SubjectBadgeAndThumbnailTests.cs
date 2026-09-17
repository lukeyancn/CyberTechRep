using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CyberTechRep.Plugin.Services.Files;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>需求 6：圆圈栏「有新文件」未读标记器（纯逻辑、线程安全、会话内有效、不持久化）。</summary>
public sealed class SubjectFileBadgeTrackerTests
{
    [Fact]
    public void MarkUnseen_ThenIsUnseen_True_UntilCleared()
    {
        var tracker = new SubjectFileBadgeTracker();

        Assert.False(tracker.IsUnseen("数学"));

        tracker.MarkUnseen("数学");
        Assert.True(tracker.IsUnseen("数学"));

        tracker.Clear("数学");
        Assert.False(tracker.IsUnseen("数学"));
    }

    [Fact]
    public void MarkUnseen_Repeated_IsIdempotent()
    {
        var tracker = new SubjectFileBadgeTracker();

        tracker.MarkUnseen("数学");
        tracker.MarkUnseen("数学");
        tracker.MarkUnseen("数学");

        Assert.Single(tracker.UnseenSubjects);
        Assert.True(tracker.IsUnseen("数学"));

        // 重复标记后再清除一次即干净
        tracker.Clear("数学");
        Assert.False(tracker.IsUnseen("数学"));
    }

    [Fact]
    public void SubjectComparison_IsCaseInsensitive_AndTrimsWhitespace()
    {
        var tracker = new SubjectFileBadgeTracker();

        tracker.MarkUnseen("Math");

        Assert.True(tracker.IsUnseen("math"));
        Assert.True(tracker.IsUnseen("MATH"));
        Assert.True(tracker.IsUnseen(" Math "));

        tracker.Clear("mAtH");
        Assert.False(tracker.IsUnseen("Math"));
    }

    [Fact]
    public void MultipleSubjects_AreIndependent()
    {
        var tracker = new SubjectFileBadgeTracker();

        tracker.MarkUnseen("数学");
        tracker.MarkUnseen("语文");
        tracker.MarkUnseen("英语");

        tracker.Clear("语文");

        Assert.True(tracker.IsUnseen("数学"));
        Assert.False(tracker.IsUnseen("语文"));
        Assert.True(tracker.IsUnseen("英语"));
        Assert.Equal(2, tracker.UnseenSubjects.Count);
    }

    [Fact]
    public void Clear_OrQuery_UnknownSubject_IsNoOp()
    {
        var tracker = new SubjectFileBadgeTracker();

        tracker.Clear("不存在");
        Assert.False(tracker.IsUnseen("不存在"));
        Assert.Empty(tracker.UnseenSubjects);
    }

    [Fact]
    public void UnseenSubjects_ReturnsSnapshot_NotLiveView()
    {
        var tracker = new SubjectFileBadgeTracker();
        tracker.MarkUnseen("数学");

        var snapshot = tracker.UnseenSubjects;
        tracker.MarkUnseen("语文");

        Assert.Single(snapshot);
        Assert.Equal(2, tracker.UnseenSubjects.Count);
    }

    [Fact]
    public void EmptySubject_TreatedAsOrdinaryKey_NoSpecialSkip()
    {
        // 空/未分类学科不做特殊跳过：标记与查询照常工作（键归一化为空串），
        // 只是圆圈栏没有对应圆圈项可显示、用户点不到 → 该标记不会被清除（见实现说明）。
        var tracker = new SubjectFileBadgeTracker();

        Assert.False(tracker.IsUnseen(""));

        tracker.MarkUnseen(null);
        Assert.True(tracker.IsUnseen(""));
        Assert.True(tracker.IsUnseen("   "));
        Assert.Equal("", Assert.Single(tracker.UnseenSubjects));

        tracker.Clear(null);
        Assert.False(tracker.IsUnseen(""));
    }

    [Fact]
    public void ConcurrentMarkAndClear_DoesNotThrow_AndKeepsConsistentState()
    {
        var tracker = new SubjectFileBadgeTracker();

        Parallel.For(0, 400, i =>
        {
            var subject = (i % 4) switch { 0 => "数学", 1 => "语文", 2 => "英语", _ => "物理" };
            tracker.MarkUnseen(subject);
            if (i % 3 == 0)
            {
                tracker.Clear(subject);
            }

            // 所有线程都会标记的学科：并发结束后必然处于未读态
            tracker.MarkUnseen("并发");
        });

        // 线程安全：并发标记/清除不抛异常，状态可查询
        Assert.True(tracker.IsUnseen("并发"));
        Assert.InRange(tracker.UnseenSubjects.Count, 1, 5);
    }
}

/// <summary>需求 8：图片缩略图提供者（扩展名判定 + 失败不抛 + 结果自洽与缓存）。</summary>
public sealed class ImageThumbnailProviderTests
{
    [Theory]
    [InlineData("a.png")]
    [InlineData("photo.JPG")]
    [InlineData("photo.jpeg")]
    [InlineData("avatar.JFIF")]
    [InlineData("anim.gif")]
    [InlineData("shot.bmp")]
    [InlineData("web.webp")]
    [InlineData("scan.TIF")]
    [InlineData("scan.tiff")]
    [InlineData("icon.ico")]
    [InlineData(@"C:\归档\数学\2026-09-17\题目.png")]
    public void IsImageFileName_KnownImageExtensions_True(string fileName) =>
        Assert.True(ImageThumbnailProvider.IsImageFileName(fileName));

    [Theory]
    [InlineData("a.svg")]
    [InlineData("a.txt")]
    [InlineData("a.pdf")]
    [InlineData("README")]
    [InlineData("a.png.bak")]
    [InlineData("")]
    [InlineData(null)]
    public void IsImageFileName_NonImageOrNoExtension_False(string? fileName) =>
        Assert.False(ImageThumbnailProvider.IsImageFileName(fileName));

    [Fact]
    public void TryGetThumbnailPng_NonexistentFile_False()
    {
        var path = Path.Combine(Path.GetTempPath(), "classing-tests", $"{Guid.NewGuid():N}.png");

        Assert.False(ImageThumbnailProvider.TryGetThumbnailPng(path, 176, out var png));
        Assert.Null(png);
    }

    [Fact]
    public Task TryGetThumbnailPng_NonImageExtension_OrCorruptFile_False()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "classing-tests", "thumb-fail", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 非图片扩展名（文件真实存在）
                var textPath = Path.Combine(dir, "笔记.txt");
                File.WriteAllText(textPath, "不是图片");
                Assert.False(ImageThumbnailProvider.TryGetThumbnailPng(textPath, 176, out var pngFromText));
                Assert.Null(pngFromText);

                // 图片扩展名但内容损坏 → 解码失败，不抛
                var corruptPath = Path.Combine(dir, "损坏.png");
                File.WriteAllBytes(corruptPath, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03]);
                Assert.False(ImageThumbnailProvider.TryGetThumbnailPng(corruptPath, 176, out var pngFromCorrupt));
                Assert.Null(pngFromCorrupt);

                // 空文件同样不抛
                var emptyPath = Path.Combine(dir, "空图.jpg");
                File.WriteAllBytes(emptyPath, []);
                Assert.False(ImageThumbnailProvider.TryGetThumbnailPng(emptyPath, 176, out var pngFromEmpty));
                Assert.Null(pngFromEmpty);
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

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task TryGetThumbnailPng_RealPng_ReturnsDecodableThumbnailWithinMaxWidth_AndCaches()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "classing-tests", "thumb-real", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 400×300 的 PNG（大于缩略宽度，走缩放解码路径）
                var path = Path.Combine(dir, "大图.png");
                using (var source = new WriteableBitmap(new PixelSize(400, 300), new Vector(96, 96),
                           PixelFormat.Bgra8888, AlphaFormat.Premul))
                using (var stream = File.Create(path))
                {
                    source.Save(stream);
                }

                Assert.True(ImageThumbnailProvider.TryGetThumbnailPng(path, 176, out var png));
                Assert.NotNull(png);
                Assert.NotEmpty(png!);

                // 结果自洽（不依赖具体像素内容）：可再次解码为位图，宽度受最大宽度约束
                using (var decoded = new Bitmap(new MemoryStream(png!)))
                {
                    Assert.True(decoded.PixelSize.Width > 0);
                    Assert.True(decoded.PixelSize.Height > 0);
                    Assert.True(decoded.PixelSize.Width <= 176);
                }

                // 第二次调用命中缓存：返回同一份字节（不重复解码）
                Assert.True(ImageThumbnailProvider.TryGetThumbnailPng(path, 176, out var cached));
                Assert.Same(png, cached);

                // 参数守卫：非正宽度 / 空路径 → false
                Assert.False(ImageThumbnailProvider.TryGetThumbnailPng(path, 0, out _));
                Assert.False(ImageThumbnailProvider.TryGetThumbnailPng(null, 176, out _));
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

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public Task TryGetThumbnailPng_SameImageDifferentWidths_AreCachedSeparately()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "classing-tests", "thumb-widths", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "图.png");
                using (var source = new WriteableBitmap(new PixelSize(120, 80), new Vector(96, 96),
                           PixelFormat.Bgra8888, AlphaFormat.Premul))
                using (var stream = File.Create(path))
                {
                    source.Save(stream);
                }

                Assert.True(ImageThumbnailProvider.TryGetThumbnailPng(path, 176, out var wide));
                Assert.True(ImageThumbnailProvider.TryGetThumbnailPng(path, 44, out var small));
                Assert.NotNull(wide);
                Assert.NotNull(small);

                // 缓存键含宽度：两份结果各自独立（不是同一数组实例）
                Assert.NotSame(wide, small);
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

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 需求 8：文件悬浮窗条目构建——图片记录优先取缩略图（且不再提取关联图标），
    /// 非图片/缩略图失败时保持原有图标/「📄」兜底（两者互斥显示）。
    /// </summary>
    [Fact]
    public Task FileItemView_ImageRecord_UsesThumbnail_NonImageKeepsIconFallback()
    {
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "classing-tests", "thumb-item", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var pngPath = Path.Combine(dir, "题目.png");
                using (var source = new WriteableBitmap(new PixelSize(200, 120), new Vector(96, 96),
                           PixelFormat.Bgra8888, AlphaFormat.Premul))
                using (var stream = File.Create(pngPath))
                {
                    source.Save(stream);
                }

                var pdfPath = Path.Combine(dir, "讲义.pdf");
                File.WriteAllText(pdfPath, "%PDF-1.4 测试用文件");

                var pipeline = new FilesWindowPipelineStub();
                pipeline.Records.Add(FileRecordFor("题目.png"));
                pipeline.Records.Add(FileRecordFor("讲义.pdf"));

                var window = new SubjectFilesSuspensionWindow(
                    pipeline,
                    () => new SubjectCircleBarSettings(),
                    relative => Path.Combine(dir, Path.GetFileName(relative)));
                try
                {
                    await window.RefreshAsync();

                    var items = ((System.Collections.IEnumerable)window.VisibleGroups!)
                        .Cast<SubjectFileGroupView>()
                        .SelectMany(group => group.Items)
                        .ToList();

                    var image = items.Single(item => item.FileName == "题目.png");
                    Assert.True(image.HasThumbnail);
                    Assert.NotNull(image.Thumbnail);
                    Assert.False(image.HasIcon);   // 取到缩略图则不再提图标（互斥）

                    var pdf = items.Single(item => item.FileName == "讲义.pdf");
                    Assert.False(pdf.HasThumbnail);
                    Assert.Null(pdf.Thumbnail);
                }
                finally
                {
                    TryCloseWindow(window);
                }
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
        }, CancellationToken.None);
    }

    private static FileRecord FileRecordFor(string fileName) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        FileName = fileName,
        Status = FileStatus.Archived,
        ArchivedRelativePath = $"未分类/2026-09-17/{fileName}",
        CreatedAt = DateTimeOffset.Parse("2026-09-17T08:00:00")
    };

    private static void TryCloseWindow(Window window)
    {
        try
        {
            if (window.IsVisible)
            {
                window.Content = null;
                window.Close();
            }

            Dispatcher.UIThread.RunJobs();
        }
        catch
        {
            // 测试清理失败不影响测试结论
        }
    }

    /// <summary>文件管道替身：只供文件悬浮窗读取记录（不写文件、不触发事件）。</summary>
    private sealed class FilesWindowPipelineStub : IFilePipelineService
    {
        public event EventHandler<FileRecord>? FileUpdated
        {
            add { }
            remove { }
        }

        public List<FileRecord> Records { get; } = [];

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FileRecord>>(Records.ToList());

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
            => throw new NotSupportedException("测试桩不写文件");

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

/// <summary>
/// 需求 6：圆圈栏红点数据流（归档事件标记 → 定项刷新；文件展示事件清除红点；新学科合并标记）。
/// 控件构造与事件触发须在 Avalonia UI 线程（Headless 会话调度）。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SubjectCircleBadgeFlowTests : IDisposable
{
    private readonly string _dir;

    public SubjectCircleBadgeFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "circle-badge", Guid.NewGuid().ToString("N"));
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
    public Task ArchivedFile_MarksBadgeInPlace_SubjectFilesShown_ClearsIt()
    {
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var (circle, controller, pipeline, filesWindow, overlays) = CreateSut();
            try
            {
                await circle.RefreshAsync();

                // 圆圈栏可见学科名保持公开可用（诊断 API），数据项已从字符串改为带红点状态的对象
                Assert.Contains("数学", circle.VisibleSubjects!);
                Assert.Contains("语文", circle.VisibleSubjects!);

                var math = FindCircleItem(circle, "数学");
                Assert.NotNull(math);
                Assert.False(math!.HasNewFile);

                // 归档完成 → 红点标记；列表已有该学科 → 定项刷新（实例不变，不整表重建）
                pipeline.Raise(Archived("数学", "2026-09-01", "a.png"));
                Assert.True(math.HasNewFile);
                Assert.Same(math, FindCircleItem(circle, "数学"));

                // 圆圈点击打开（toggle 显示成功）→ 控制器触发 SubjectFilesShown → 红点清除（仍是定项刷新）
                await controller.ToggleOrSwitchAsync("数学");
                Assert.True(overlays.FilesShown);
                Assert.False(math.HasNewFile);
                Assert.Same(math, FindCircleItem(circle, "数学"));

                // 再次归档 → 红点复现；同学科再次点击 = 隐藏（内容未展示）→ 红点保留
                pipeline.Raise(Archived("数学", "2026-09-01", "b.png"));
                Assert.True(math.HasNewFile);
                await controller.ToggleOrSwitchAsync("数学");
                Assert.False(overlays.FilesShown);
                Assert.True(math.HasNewFile);
            }
            finally
            {
                TryClose(filesWindow);
                TryClose(circle);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task FirstAppearanceOfSubject_TriggersRefresh_AndMergesBadgeFromTracker()
    {
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var (circle, controller, pipeline, filesWindow, _) = CreateSut();
            try
            {
                await circle.RefreshAsync();
                Assert.Null(FindCircleItem(circle, "地理"));

                // 模拟 FilePipelineService 语义：归档完成先落库（GetRecordsAsync 可见）再 Raise
                var record = Archived("地理", "2026-09-02", "d.png");
                pipeline.Records.Add(record);
                pipeline.Raise(record);

                // 列表中没有该学科 → 整表刷新发现新学科，并从 tracker 合并未读标记
                var geo = FindCircleItem(circle, "地理");
                Assert.NotNull(geo);
                Assert.True(geo!.HasNewFile);

                // 上课联动打开同样清除红点（控制器两条触发路径共用同一事件）
                await controller.OpenForClassAsync("地理");
                Assert.False(geo.HasNewFile);
            }
            finally
            {
                TryClose(filesWindow);
                TryClose(circle);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public Task NonArchivedOrUnclassifiableFile_DoesNotMarkBadge()
    {
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            var (circle, _, pipeline, filesWindow, _) = CreateSut();
            try
            {
                await circle.RefreshAsync();

                // 未归档完成（Failed）→ 不标记、不刷新：新学科不会出现，已有学科红点不变
                pipeline.Raise(new FileRecord
                {
                    MessageId = "m-failed",
                    FileName = "x.png",
                    Status = FileStatus.Failed,
                    ArchivedRelativePath = "地理/2026-09-02/x.png"
                });
                Assert.Null(FindCircleItem(circle, "地理"));
                Assert.False(FindCircleItem(circle, "数学")!.HasNewFile);

                // 首段是日期（未按学科分组归档）→ 提取不到学科，同样不标记、不刷新
                pipeline.Raise(Archived("2026-09-02", "2026-09-02", "y.png"));
                Assert.False(FindCircleItem(circle, "数学")!.HasNewFile);
            }
            finally
            {
                TryClose(filesWindow);
                TryClose(circle);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 需求 6：模板物化验证——圆圈文字绑定学科名；小红点 Border 叠在 48×48 圆圈框右上角、
    /// 随未读标记显隐，横向/纵向排列都正常显示。
    /// </summary>
    [Fact]
    public Task CircleItemTemplate_BindsSubjectText_AndBadgeAtTopRight_InBothOrientations()
    {
        return AvaloniaTestSetup.Session.Dispatch(async () =>
        {
            EnsureControlTheme();
            var settings = new SubjectCircleBarSettings();
            var (circle, _, pipeline, filesWindow, _) = CreateSut(() => settings);
            try
            {
                circle.Show();
                await circle.RefreshAsync();
                Dispatcher.UIThread.RunJobs();
                circle.UpdateLayout();

                var math = FindCircleItem(circle, "数学");
                Assert.NotNull(math);

                // 圆圈文字绑定 {Binding Subject}
                var text = ((Control)circle.Content!).GetVisualDescendants().OfType<TextBlock>()
                    .FirstOrDefault(block => ReferenceEquals(block.DataContext, math) && block.Text == "数学");
                Assert.NotNull(text);

                // 小红点：初始隐藏，位于 48×48 圆圈框的右上角
                var badge = FindBadge(circle, math!);
                Assert.NotNull(badge);
                Assert.False(badge!.IsVisible);
                AssertBadgeAtTopRight(badge);

                // 归档新文件 → 红点显示且仍在右上角
                pipeline.Raise(Archived("数学", "2026-09-01", "a.png"));
                Dispatcher.UIThread.RunJobs();
                Assert.True(badge.IsVisible);
                AssertBadgeAtTopRight(badge);

                // 横向排列：列表重建后红点仍合并显示、位置不变（横竖排列都正常）
                settings.Orientation = SubjectCircleBarOptions.OrientationHorizontal;
                await circle.RefreshAsync();
                Dispatcher.UIThread.RunJobs();
                circle.UpdateLayout();

                var mathHorizontal = FindCircleItem(circle, "数学");
                Assert.NotNull(mathHorizontal);
                var badgeHorizontal = FindBadge(circle, mathHorizontal!);
                Assert.NotNull(badgeHorizontal);
                Assert.True(badgeHorizontal!.IsVisible);
                AssertBadgeAtTopRight(badgeHorizontal);
            }
            finally
            {
                TryClose(filesWindow);
                TryClose(circle);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 组装被测系统：圆圈窗 + 控制器 + 文件悬浮窗 + 可控文件管道替身。
    /// 文件窗用 FakeOverlayController 真正 Show/Hide（toggle 的可见性判断依赖 IsVisible）。
    /// </summary>
    private (
        SubjectCircleBarWindow Circle,
        SubjectFilesController Controller,
        FakePipeline Pipeline,
        SubjectFilesSuspensionWindow FilesWindow,
        FakeOverlayController Overlays) CreateSut(Func<SubjectCircleBarSettings>? getCircleSettings = null)
    {
        getCircleSettings ??= () => new SubjectCircleBarSettings();
        var settings = new SettingsService(_dir);
        var overlays = new FakeOverlayController();
        var pipeline = new FakePipeline();
        pipeline.Records.Add(Archived("数学", "2026-09-01", "a.pdf"));

        var filesWindow = new SubjectFilesSuspensionWindow(
            pipeline,
            getCircleSettings,
            _ => null,
            overlays: overlays,
            settingsService: settings);
        overlays.FilesWindow = filesWindow;

        var controller = new SubjectFilesController(overlays, settings, () => filesWindow);
        var circle = new SubjectCircleBarWindow(controller, pipeline, getCircleSettings);
        return (circle, controller, pipeline, filesWindow, overlays);
    }

    private static FileRecord Archived(string subject, string date, string fileName) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        FileName = fileName,
        Status = FileStatus.Archived,
        ArchivedRelativePath = $"{subject}/{date}/{fileName}",
        CreatedAt = DateTimeOffset.Parse($"{date}T08:00:00")
    };

    /// <summary>取圆圈列表的数据项（数据源已从学科名字符串改为 <see cref="SubjectCircleItem"/>）。</summary>
    private static IReadOnlyList<SubjectCircleItem> GetCircleItems(SubjectCircleBarWindow circle)
    {
        var list = ((Control)circle.Content!).GetLogicalDescendants()
            .OfType<ItemsControl>()
            .FirstOrDefault(control => control.Name == "CircleList");
        Assert.NotNull(list);
        return Assert.IsAssignableFrom<IReadOnlyList<SubjectCircleItem>>(list!.ItemsSource);
    }

    private static SubjectCircleItem? FindCircleItem(SubjectCircleBarWindow circle, string subject) =>
        GetCircleItems(circle).FirstOrDefault(item => item.Subject == subject);

    /// <summary>按 DataContext 定位圆圈右上角的小红点（宽高 10 的 Border，与圆圈同一数据项）。</summary>
    private static Border? FindBadge(SubjectCircleBarWindow circle, SubjectCircleItem item) =>
        ((Control)circle.Content!).GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Width == 10 && border.Height == 10
                && ReferenceEquals(border.DataContext, item));

    /// <summary>红点叠在 48×48 圆圈框的右上角：Panel 内偏移 = 右边缘对齐、顶边对齐。</summary>
    private static void AssertBadgeAtTopRight(Border badge)
    {
        Assert.True(badge.Parent is Panel, "小红点应叠在圆圈所在的 48×48 Panel 上");
        var panel = (Panel)badge.Parent!;
        var offset = badge.TranslatePoint(new Point(0, 0), panel);
        Assert.NotNull(offset);
        Assert.Equal(48, panel.Bounds.Width);
        Assert.Equal(panel.Bounds.Width - badge.Bounds.Width, offset!.Value.X, 1);
        Assert.Equal(0, offset.Value.Y, 1);
    }

    /// <summary>为 ItemsControl 数据模板生成条目容器准备控件主题（与其他悬浮窗测试同一约定）。</summary>
    private static void EnsureControlTheme()
    {
        if (Application.Current is { } app && !app.Styles.OfType<SimpleTheme>().Any())
        {
            app.Styles.Add(new SimpleTheme());
        }
    }

    private static void TryClose(Window window)
    {
        try
        {
            if (window.IsVisible)
            {
                window.Content = null;
                window.Close();
            }

            Dispatcher.UIThread.RunJobs();
        }
        catch
        {
            // 测试清理失败不影响测试结论
        }
    }

    /// <summary>可控文件管道替身：GetRecordsAsync 立即返回（归档事件先落库再 Raise，与真实管道一致）。</summary>
    private sealed class FakePipeline : IFilePipelineService
    {
        public event EventHandler<FileRecord>? FileUpdated;

        public List<FileRecord> Records { get; } = [];

        public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FileRecord>>(Records.ToList());

        public Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
            string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
            => throw new NotSupportedException("测试桩不写文件");

        public Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
            => Task.CompletedTask;

        public void Raise(FileRecord record) => FileUpdated?.Invoke(this, record);
    }

    /// <summary>悬浮窗控制器替身：真实 Show/Hide 目标文件窗，便于验证 toggle 语义与展示事件。</summary>
    private sealed class FakeOverlayController : ISuspensionWindowController
    {
        public Window? FilesWindow { get; set; }

        /// <summary>最近一次在「真正展示」链路上被触发的标记（验证事件确实由控制器发出）。</summary>
        public bool FilesShown { get; private set; }

        public Task ShowAsync(string overlayKey, CancellationToken ct = default)
        {
            if (overlayKey == SuspensionWindowController.FilesKey)
            {
                FilesShown = true;
                FilesWindow?.Show();
            }

            return Task.CompletedTask;
        }

        public Task HideAsync(string overlayKey, CancellationToken ct = default)
        {
            if (overlayKey == SuspensionWindowController.FilesKey)
            {
                FilesShown = false;
                FilesWindow?.Hide();
            }

            return Task.CompletedTask;
        }

        public Task ResetPositionAsync(string overlayKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task ApplySettingsAsync(string overlayKey, OverlayWindowSettings settings, CancellationToken ct = default)
            => Task.CompletedTask;

        public bool IsOnScreen(string overlayKey) => true;

        public void NotifyHostStopping()
        {
        }

        public void SetOverlayEditing(string overlayKey, bool editing)
        {
        }
    }
}
