using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CyberTechRep.Plugin.Services.Overlays;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Views;

/// <summary>图片悬浮窗条目视图（记录 + 按窗口宽度自适应解码的位图）。
/// 解码失败（文件缺失/格式损坏）时 <see cref="Image"/> 为 null，条目仍保留（说明行与「打开」仍可用）。</summary>
public sealed class ImageOverlayItemView : IDisposable
{
    public required FileRecord Record { get; init; }

    /// <summary>解码后的位图（解码宽度已 clamp，见 <see cref="ImageOverlayLogic.ResolveDecodeWidth"/>）。</summary>
    public Bitmap? Image { get; init; }

    public bool HasImage => Image is not null;

    /// <summary>极简说明行：文件名 · 时间 · 学科（学科缺失时省略）。</summary>
    public string Caption { get; init; } = "";

    public void Dispose() => Image?.Dispose();
}

/// <summary>
/// 图片悬浮窗可抽出的纯逻辑（需求 9，可脱离 UI 单测）：
/// 支持的图片扩展名、列表上限与解码宽度上限、说明文案、解码宽度换算。
/// </summary>
public static class ImageOverlayLogic
{
    /// <summary>列表上限：超出后丢弃最旧一张（防止长时间运行内存持续增长；与需求 9 约定一致）。</summary>
    public const int MaxItems = 20;

    /// <summary>最小解码宽度（像素）：窗口很窄时也至少解到这个宽度，避免显示时放大发虚。</summary>
    public const int MinDecodeWidth = 640;

    /// <summary>最大解码宽度（像素）：超过按此宽度解码，避免原图全尺寸解码吃内存。</summary>
    public const int MaxDecodeWidth = 1600;

    /// <summary>兜底解码宽度（窗口尚未显示、拿不到实际宽度时）。</summary>
    public const int FallbackDecodeWidth = 800;

    /// <summary>展示的图片扩展名（至少覆盖需求约定的 png/jpg/jpeg/jfif/gif/bmp/webp；大小写不敏感）。</summary>
    private static readonly string[] SupportedExtensions =
        [".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp"];

    /// <summary>按扩展名判断是否为可展示图片。</summary>
    public static bool IsImageFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length > 0
               && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>极简说明文案：文件名 · MM-dd HH:mm · 学科（无学科段时省略学科）。</summary>
    public static string FormatCaption(FileRecord record, string? subject)
    {
        var time = (record.CompletedAt ?? record.CreatedAt).LocalDateTime;
        var text = $"{record.FileName} · {time:MM-dd HH:mm}";
        return string.IsNullOrWhiteSpace(subject) ? text : $"{text} · {subject.Trim()}";
    }

    /// <summary>
    /// 解码宽度（像素）= 窗口逻辑宽度 × 渲染缩放，并 clamp 到
    /// [<see cref="MinDecodeWidth"/>, <see cref="MaxDecodeWidth"/>]；
    /// 输入非法（NaN/0/负）时用 <see cref="FallbackDecodeWidth"/>。
    /// </summary>
    public static int ResolveDecodeWidth(double windowWidth, double renderScaling)
    {
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return FallbackDecodeWidth;
        }

        var scaling = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1.0;
        return (int)Math.Clamp(windowWidth * scaling, MinDecodeWidth, MaxDecodeWidth);
    }
}

/// <summary>图片入列结果：是否新增、是否因同一 <see cref="FileRecord.Id"/> 重复被忽略、因上限淘汰的最旧记录。</summary>
public readonly record struct ImageOverlayAddResult(bool Added, bool Duplicate, FileRecord? Evicted);

/// <summary>
/// 图片悬浮窗图片列表纯逻辑（需求 9）：按到达顺序累积（新图追加在末尾）、同一
/// <see cref="FileRecord.Id"/> 去重、超过 <see cref="ImageOverlayLogic.MaxItems"/> 丢弃最旧。
/// </summary>
public sealed class ImageOverlayList
{
    private readonly List<FileRecord> _records = [];
    private readonly HashSet<Guid> _ids = [];

    /// <summary>当前列表（旧 → 新；索引与窗口条目一一对应）。</summary>
    public IReadOnlyList<FileRecord> Records => _records;

    public int Count => _records.Count;

    /// <summary>
    /// 追加一张图片：同一 Id 重复到达时忽略（<see cref="ImageOverlayAddResult.Duplicate"/>）；
    /// 超过上限时丢弃最旧一张并经 <see cref="ImageOverlayAddResult.Evicted"/> 返回（供调用方释放位图）。
    /// </summary>
    public ImageOverlayAddResult Add(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_ids.Add(record.Id))
        {
            return new ImageOverlayAddResult(Added: false, Duplicate: true, Evicted: null);
        }

        _records.Add(record);
        if (_records.Count <= ImageOverlayLogic.MaxItems)
        {
            return new ImageOverlayAddResult(Added: true, Duplicate: false, Evicted: null);
        }

        var evicted = _records[0];
        _records.RemoveAt(0);
        _ids.Remove(evicted.Id);
        return new ImageOverlayAddResult(Added: true, Duplicate: false, Evicted: evicted);
    }

    public void Clear()
    {
        _records.Clear();
        _ids.Clear();
    }

    /// <summary>
    /// 「当前正在看的条目」纯计算：返回条目中心最接近视口中心的索引；
    /// 中心值非法（NaN/无穷，容器未实现）的条目跳过；全部无法判定时返回 -1（调用方回落最新一张）。
    /// </summary>
    public static int FindNearestIndex(IReadOnlyList<double> itemCenters, double viewportCenter)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < itemCenters.Count; i++)
        {
            var center = itemCenters[i];
            if (!double.IsFinite(center))
            {
                continue;
            }

            var distance = Math.Abs(center - viewportCenter);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }
}

/// <summary>
/// 图片悬浮窗（第六悬浮窗，需求 9：收到图片自动展示，图片查看器形态）：
/// <see cref="Services.Overlays.ImageOverlayService"/> 订阅文件管道，归档完成的图片经
/// <see cref="Dispatcher.UIThread"/> 调度后调用 <see cref="AddImage"/> 加入本窗；
/// 是否自动弹出由设置项 <c>Overlays.ImageAutoShowOnReceive</c> 控制（本窗自身显示状态与
/// 其他悬浮窗一致，由 <c>Overlays.Image.Visible</c> 与控制器管理）。
/// 标题栏「打开」用系统默认图片查看器打开「当前正在看的图片」（视口中心最近的条目；
/// 判定不出来时回落最新一张）；「⋯」为与其他悬浮窗共用的快捷设置菜单；「×」隐藏窗口。
/// 位置/大小/层级由 <see cref="SuspensionWindowController"/> 持久化与应用。
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ImageSuspensionWindow : Window
{
    /// <summary>归档相对路径 → 绝对路径解析委托（注入；与文件管道 GetDownloadRoot 解析规则一致）。</summary>
    private readonly Func<string, string?> _resolveAbsolutePath = null!;

    private readonly ILogger _logger = null!;
    private readonly OverlayQuickMenu? _quickMenu;

    /// <summary>图片列表纯逻辑（顺序/去重/上限）。</summary>
    private readonly ImageOverlayList _list = new();

    /// <summary>窗口条目视图（与 <see cref="_list"/> 索引一一对应）。</summary>
    private readonly ObservableCollection<ImageOverlayItemView> _views = [];

    public ImageSuspensionWindow()
    {
        // 设计时/XAML 预览用；运行时走含依赖构造
        InitializeComponent();
    }

    public ImageSuspensionWindow(
        Func<string, string?> resolveAbsolutePath,
        ILogger? logger = null,
        ISuspensionWindowController? overlays = null,
        ISettingsService? settingsService = null)
    {
        _resolveAbsolutePath = resolveAbsolutePath ?? throw new ArgumentNullException(nameof(resolveAbsolutePath));
        _logger = logger ?? NullLogger.Instance;
        InitializeComponent();
        ImageItems.ItemsSource = _views;

        // 右上角「⋯」快捷菜单（与其余悬浮窗共用 OverlayQuickMenu：置顶/固定/穿透，即时生效并回写设置）
        if (settingsService is not null)
        {
            _quickMenu = new OverlayQuickMenu(
                settingsService, overlays, SuspensionWindowController.ImageKey,
                () => settingsService.Current.Overlays.Image, this);
            QuickMenuButton.Flyout = _quickMenu.Flyout;
        }

        UpdateEmptyState();
    }

    /// <summary>当前图片数量（测试用）。</summary>
    internal int ImageCount => _list.Count;

    /// <summary>当前图片记录（旧 → 新；测试用）。</summary>
    internal IReadOnlyList<FileRecord> Images => _list.Records;

    /// <summary>当前条目视图（与 <see cref="Images"/> 同序；测试用）。</summary>
    internal IReadOnlyList<ImageOverlayItemView> ItemViews => _views;

    /// <summary>
    /// 加入一张图片（必须由 UI 线程调用：由 <see cref="Services.Overlays.ImageOverlayService"/>
    /// 经 <see cref="Dispatcher.UIThread"/> 调度）。同一 <see cref="FileRecord.Id"/> 重复到达时忽略；
    /// 超过上限丢弃最旧一张并释放其位图；新图追加在末尾并自动滚动到可见位置。
    /// 本方法只改内容、不弹窗（是否显示由调用方经控制器决定）。
    /// </summary>
    public void AddImage(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            var result = _list.Add(record);
            if (result.Duplicate)
            {
                _logger.LogInformation("图片悬浮窗：同一图片记录重复到达，忽略（FileId={FileId}）", record.Id);
                return;
            }

            if (result.Evicted is { } evicted)
            {
                var removed = _views.FirstOrDefault(v => v.Record.Id == evicted.Id);
                if (removed is not null)
                {
                    _views.Remove(removed);
                    removed.Dispose();
                }
            }

            _views.Add(CreateItemView(record));
            UpdateEmptyState();
            ScrollToNewest();
            _logger.LogInformation("图片悬浮窗已加入图片 {FileName}（当前 {Count}/{Max} 张）",
                record.FileName, _list.Count, ImageOverlayLogic.MaxItems);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图片悬浮窗加入图片失败（已吞掉）：{FileName}", record.FileName);
        }
    }

    /// <summary>解码 + 组装条目视图（解码失败时位图为 null，条目仍保留）。</summary>
    private ImageOverlayItemView CreateItemView(FileRecord record)
    {
        Bitmap? bitmap = null;
        var path = record.ArchivedRelativePath is { } relative ? _resolveAbsolutePath(relative) : null;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                // 解码宽度自适应窗口宽度并 clamp（见 ImageOverlayLogic.ResolveDecodeWidth），
                // 避免原图全尺寸解码吃内存；窗口未显示时用设置宽度/兜底宽度。
                var width = double.IsFinite(Bounds.Width) && Bounds.Width > 0
                    ? Bounds.Width
                    : double.IsFinite(Width) ? Width : ImageOverlayLogic.FallbackDecodeWidth;
                var decodeWidth = ImageOverlayLogic.ResolveDecodeWidth(width, RenderScaling);
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, decodeWidth);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "图片解码失败（条目保留，可用系统查看器打开）：{Path}", path);
            }
        }
        else
        {
            _logger.LogWarning("图片文件不存在或路径解析失败（条目保留，仅说明行）：{Relative}",
                record.ArchivedRelativePath);
        }

        return new ImageOverlayItemView
        {
            Record = record,
            Image = bitmap,
            Caption = ImageOverlayLogic.FormatCaption(record, SubjectFilesQuery.ExtractSubject(record.ArchivedRelativePath))
        };
    }

    /// <summary>
    /// 取「当前正在看的图片」：视口中心最近的条目；判定不出来（布局未完成/容器未实现）时回落最新一张。
    /// </summary>
    internal ImageOverlayItemView? FindCurrentItem()
    {
        if (_views.Count == 0)
        {
            return null;
        }

        var centers = new List<double>(_views.Count);
        for (var i = 0; i < _views.Count; i++)
        {
            var container = ImageItems.ContainerFromIndex(i) as Control;
            centers.Add(container is null ? double.NaN : container.Bounds.Y + container.Bounds.Height / 2);
        }

        var viewportCenter = ImageScroll.Offset.Y + ImageScroll.Viewport.Height / 2;
        var index = ImageOverlayList.FindNearestIndex(centers, viewportCenter);
        return index >= 0 && index < _views.Count ? _views[index] : _views[^1];
    }

    private void UpdateEmptyState()
    {
        var hasAny = _views.Count > 0;
        EmptyText.IsVisible = !hasAny;
        OpenButton.IsEnabled = hasAny;
    }

    /// <summary>滚动到最新图片（追加在末尾）。Background 优先级保证排在一次布局之后，避免 offset 被 clamp。</summary>
    private void ScrollToNewest()
    {
        try
        {
            ImageScroll.ScrollToEnd();
            Dispatcher.UIThread.Post(() => ImageScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图片悬浮窗滚动到最新图片失败（已吞掉）");
        }
    }

    /// <summary>「打开」：用系统默认图片查看器打开当前图片；失败只记日志不抛。</summary>
    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var item = FindCurrentItem();
        if (item?.Record.ArchivedRelativePath is not { } relative)
        {
            _logger.LogWarning("图片悬浮窗「打开」：没有可打开的图片");
            return;
        }

        var path = _resolveAbsolutePath(relative);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _logger.LogWarning("图片悬浮窗「打开」：文件不存在或路径解析失败：{Relative}", relative);
            return;
        }

        // 仅 Windows：系统默认程序打开（Win10+ 图片默认走「照片」查看器）；失败吞异常记日志
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _logger.LogInformation("图片悬浮窗：已用系统默认查看器打开 {FileName}", item.Record.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图片悬浮窗用系统默认查看器打开失败：{Path}", path);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 固定模式下禁用拖拽（位置只能经设置页调整）
        if (OverlayBehaviors.GetFixed(this))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnHideClick(object? sender, RoutedEventArgs e) => Hide();

    private void OnResizeDragDelta(object? sender, VectorEventArgs e)
    {
        // 固定模式下禁用缩放
        if (OverlayBehaviors.GetFixed(this))
        {
            return;
        }

        Width = Math.Max(MinWidth, Width + e.Vector.X);
        Height = Math.Max(MinHeight, Height + e.Vector.Y);
    }
}
