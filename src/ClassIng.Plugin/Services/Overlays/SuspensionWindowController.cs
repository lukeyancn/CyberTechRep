using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServicesStores = ClassIng.Plugin.Services.Stores;

namespace ClassIng.Plugin.Services.Overlays;

/// <summary>
/// 悬浮窗控制器（通知/作业两窗共用，Avalonia 实现 <see cref="ISuspensionWindowController"/>）：
/// 按配置创建/显示/隐藏、位置大小透明度字号即时应用、一键复位、多屏边界检测。
/// <para>
/// 设置单一来源：<see cref="ISettingsService.Current.Overlays"/>（settings.json）。
/// 构造时若存在旧版独立 overlays.json（模块 5/6 遗留），先导入其中值覆盖后归档为
/// overlays.json.migrated（一次性迁移，之后以 <see cref="ISettingsService"/> 为唯一读写源，
/// 经 <c>SaveAsync</c> 持久化并广播 <c>SettingsChanged</c> 热生效）。
/// 窗口实例由工厂委托创建（Plugin.cs 注册时注入 <see cref="Views.NoticeSuspensionWindow"/>/
/// <see cref="Views.HomeworkSuspensionWindow"/>），控制器自身不做 XAML 依赖，逻辑可脱离 UI 测试。
/// </para>
/// </summary>
public sealed class SuspensionWindowController : ISuspensionWindowController
{
    /// <summary>overlayKey：通知悬浮窗。</summary>
    public const string NoticeKey = "notice";

    /// <summary>overlayKey：作业悬浮窗。</summary>
    public const string HomeworkKey = "homework";

    /// <summary>旧版独立悬浮窗设置文件（迁移后归档为 &lt;name&gt;.migrated）。</summary>
    internal const string LegacyFileName = "overlays.json";
    internal const string MigratedSuffix = ".migrated";

    /// <summary>复位默认逻辑坐标（与 OverlayWindowSettings 默认值一致）。</summary>
    internal const double DefaultX = 40;

    internal const double DefaultY = 40;

    private static readonly string[] KnownKeys = [NoticeKey, HomeworkKey];

    private readonly string _filePath;
    private readonly ISettingsService? _settingsService;
    private readonly ILogger _logger;
    private readonly Func<string, Window?>? _windowFactory;
    private readonly object _lock = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);
    private OverlaySettings _settings;

    /// <summary>
    /// <paramref name="settingsService"/> 提供时以其为设置单一来源（推荐）；
    /// 为 null 时保持旧行为：独立 overlays.json 读写（旧测试/独立场景兼容）。
    /// </summary>
    public SuspensionWindowController(
        string dataDirectory, ILogger? logger = null, Func<string, Window?>? windowFactory = null,
        ISettingsService? settingsService = null)
    {
        _filePath = Path.Combine(dataDirectory, LegacyFileName);
        _settingsService = settingsService;
        _logger = logger ?? NullLogger.Instance;
        _windowFactory = windowFactory;
        _settings = settingsService is not null
            ? LoadFromSettingsService(dataDirectory, settingsService)
            : LoadSettings();
    }

    /// <summary>当前悬浮窗设置（副本；模块 7 设置页读写入口）。</summary>
    public OverlaySettings Settings
    {
        get
        {
            lock (_lock)
            {
                return JsonStoreFileDeepClone(_settings);
            }
        }
    }

    /// <summary>设置被控制器改写（复位/窗口拖拽回写）后广播；模块 7 设置页可订阅同步 UI。</summary>
    public event EventHandler<OverlaySettings>? SettingsPersisted;

    /// <inheritdoc />
    public Task ShowAsync(string overlayKey, CancellationToken ct = default)
    {
        if (!IsKnownKey(overlayKey))
        {
            _logger.LogWarning("ShowAsync 未知 overlayKey={Key}", overlayKey);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = GetOrCreateWindow(overlayKey);
            if (window is null)
            {
                return;
            }

            ApplyToWindow(window, GetWindowSettings(overlayKey));
            window.Show();
            _logger.LogInformation("悬浮窗已显示 Key={Key}", overlayKey);
        }).GetTask();
    }

    /// <inheritdoc />
    public Task HideAsync(string overlayKey, CancellationToken ct = default)
    {
        if (!IsKnownKey(overlayKey))
        {
            _logger.LogWarning("HideAsync 未知 overlayKey={Key}", overlayKey);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_windows.TryGetValue(overlayKey, out var window))
            {
                window.Hide();
                _logger.LogInformation("悬浮窗已隐藏 Key={Key}", overlayKey);
            }
        }).GetTask();
    }

    /// <inheritdoc />
    public Task ResetPositionAsync(string overlayKey, CancellationToken ct = default)
    {
        if (!IsKnownKey(overlayKey))
        {
            _logger.LogWarning("ResetPositionAsync 未知 overlayKey={Key}", overlayKey);
            return Task.CompletedTask;
        }

        var settings = CloneWindowSettings(GetWindowSettings(overlayKey));
        settings.X = DefaultX;
        settings.Y = DefaultY;
        _logger.LogInformation("悬浮窗位置已复位 Key={Key}, X={X}, Y={Y}", overlayKey, settings.X, settings.Y);
        return ApplySettingsCoreAsync(overlayKey, settings, persist: true);
    }

    /// <inheritdoc />
    public Task ApplySettingsAsync(string overlayKey, OverlayWindowSettings settings, CancellationToken ct = default)
    {
        if (!IsKnownKey(overlayKey) || settings is null)
        {
            _logger.LogWarning("ApplySettingsAsync 参数无效 Key={Key}", overlayKey);
            return Task.CompletedTask;
        }

        return ApplySettingsCoreAsync(overlayKey, settings, persist: true);
    }

    /// <inheritdoc />
    public bool IsOnScreen(string overlayKey)
    {
        if (!IsKnownKey(overlayKey))
        {
            return false;
        }

        var settings = GetWindowSettings(overlayKey);
        Window? window;
        lock (_lock)
        {
            _windows.TryGetValue(overlayKey, out window);
        }

        if (window?.Screens is { } screens && screens.All.Count > 0)
        {
            // 已创建窗口：以窗口实际像素位置 + 全部屏幕（换算为逻辑坐标）判定
            var scaling = window.RenderScaling;
            var windowRect = new Rect(
                window.Position.X / scaling, window.Position.Y / scaling,
                window.Bounds.Width, window.Bounds.Height);
            var screensDip = screens.All
                .Select(s => OverlayGeometry.FromPixelRect(s.Bounds, s.Scaling))
                .ToList();
            return OverlayGeometry.IsOnScreen(windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height, screensDip);
        }

        // 窗口未创建（无平台信息）：按持久化设置判定；无屏幕信息时视为在屏
        return OverlayGeometry.IsOnScreen(settings.X, settings.Y, settings.Width, settings.Height, []);
    }

    private Task ApplySettingsCoreAsync(string overlayKey, OverlayWindowSettings settings, bool persist)
    {
        // 单一来源模式下：settings 实例若本就来自 ISettingsService.Current.Overlays
        //（SettingsChanged → SettingsChangeApplier → 本方法回环），不再回写保存，避免广播死循环
        var cameFromSettingsService = _settingsService is not null &&
            ReferenceEquals(settings, GetLiveContainerSettings(overlayKey));

        lock (_lock)
        {
            if (overlayKey == NoticeKey)
            {
                _settings.Notice = settings;
            }
            else
            {
                _settings.Homework = settings;
            }

            if (persist && !cameFromSettingsService)
            {
                SaveSettings();
            }
        }

        if (persist)
        {
            RaiseSettingsPersisted();
        }

        Window? window;
        lock (_lock)
        {
            _windows.TryGetValue(overlayKey, out window);
        }

        if (window is not null)
        {
            return Dispatcher.UIThread.InvokeAsync(() => ApplyToWindow(window, settings)).GetTask();
        }

        return Task.CompletedTask;
    }

    /// <summary>把一组 OverlayWindowSettings 即时应用到窗口（透明度/字号/位置/大小/置顶）。</summary>
    private void ApplyToWindow(Window window, OverlayWindowSettings settings)
    {
        window.Topmost = settings.Topmost;
        window.Opacity = Math.Clamp(settings.Opacity, 0.1, 1.0);
        window.FontSize = settings.FontSize;
        window.Width = settings.Width;
        window.Height = settings.Height;

        // DPI 适配：持久化的是逻辑坐标（DIP），落地时按窗口缩放系数换算为像素
        var scaling = window.RenderScaling;
        window.Position = new PixelPoint(
            (int)Math.Round(settings.X * scaling),
            (int)Math.Round(settings.Y * scaling));
    }

    private Window? GetOrCreateWindow(string overlayKey)
    {
        lock (_lock)
        {
            if (_windows.TryGetValue(overlayKey, out var existing))
            {
                return existing;
            }
        }

        var window = _windowFactory?.Invoke(overlayKey);
        if (window is null)
        {
            _logger.LogWarning("窗口工厂未提供或返回 null，悬浮窗不可用 Key={Key}", overlayKey);
            return null;
        }

        lock (_lock)
        {
            _windows[overlayKey] = window;
        }

        AttachPersistenceHooks(overlayKey, window);
        return window;
    }

    /// <summary>窗口拖拽/缩放结束后把实际位置/大小回写到设置并持久化（逻辑坐标 DIP）。</summary>
    private void AttachPersistenceHooks(string overlayKey, Window window)
    {
        window.PositionChanged += (_, _) => CaptureBounds(overlayKey, window);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty)
            {
                CaptureBounds(overlayKey, window);
            }
        };
    }

    private void CaptureBounds(string overlayKey, Window window)
    {
        var scaling = window.RenderScaling;
        var settings = GetWindowSettings(overlayKey);
        settings.X = window.Position.X / scaling;
        settings.Y = window.Position.Y / scaling;
        settings.Width = window.Bounds.Width;
        settings.Height = window.Bounds.Height;
        lock (_lock)
        {
            if (overlayKey == NoticeKey)
            {
                _settings.Notice = settings;
            }
            else
            {
                _settings.Homework = settings;
            }

            SaveSettings();
        }

        RaiseSettingsPersisted();
    }

    private OverlayWindowSettings GetWindowSettings(string overlayKey)
    {
        lock (_lock)
        {
            return overlayKey == NoticeKey ? _settings.Notice : _settings.Homework;
        }
    }

    /// <summary>
    /// 单一来源（ISettingsService）初始化：以 settings.json 的悬浮窗组为准；
    /// 首启检测到旧版独立 overlays.json 时导入其中值覆盖后归档（overlays.json.migrated），
    /// 保证悬浮窗设置只有 <see cref="ISettingsService"/> 一个读写源。
    /// </summary>
    private OverlaySettings LoadFromSettingsService(string dataDirectory, ISettingsService settingsService)
    {
        var container = settingsService.Current.Overlays;
        container.Notice ??= new OverlayWindowSettings();
        container.Homework ??= new OverlayWindowSettings();

        var legacyPath = Path.Combine(dataDirectory, LegacyFileName);
        if (File.Exists(legacyPath))
        {
            try
            {
                var legacy = ServicesStores.JsonStoreFile.LoadOrRestore<OverlaySettings>(legacyPath, _logger);
                if (legacy is not null)
                {
                    legacy.Notice ??= new OverlayWindowSettings();
                    legacy.Homework ??= new OverlayWindowSettings();
                    container.Notice = legacy.Notice;
                    container.Homework = legacy.Homework;
                    container.LaunchWithHost = legacy.LaunchWithHost;
                    _ = PersistViaSettingsServiceAsync();
                    _logger.LogInformation("已将旧 overlays.json 的悬浮窗设置导入 ISettingsService（settings.json），悬浮窗设置统一为单一来源");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "迁移旧 overlays.json 失败，继续使用 settings.json 中的悬浮窗设置");
            }
            finally
            {
                // 无论导入成功与否都归档旧文件，避免下次启动重复导入
                try
                {
                    File.Move(legacyPath, legacyPath + MigratedSuffix, overwrite: true);
                    _logger.LogInformation("旧悬浮窗设置文件已归档：{Path}", legacyPath + MigratedSuffix);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "归档旧 overlays.json 失败（不影响插件运行）");
                }
            }
        }

        return container;
    }

    private OverlaySettings LoadSettings()
    {
        var dto = ServicesStores.JsonStoreFile.LoadOrRestore<OverlaySettings>(_filePath, _logger);
        var settings = dto ?? new OverlaySettings();
        settings.Notice ??= new OverlayWindowSettings();
        settings.Homework ??= new OverlayWindowSettings();
        return settings;
    }

    private void SaveSettings()
    {
        if (_settingsService is not null)
        {
            // 单一来源：_settings 即 ISettingsService.Current.Overlays（同实例，内存中已生效），
            // 经 SaveAsync 持久化并广播 SettingsChanged 热生效（ApplySettingsCoreAsync 有回环防护）
            _ = PersistViaSettingsServiceAsync();
            return;
        }

        try
        {
            ServicesStores.JsonStoreFile.SaveWithBackup(_filePath, ServicesStores.JsonStoreFile.Serialize(_settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "悬浮窗设置持久化失败（内存中状态保留）");
        }
    }

    private async Task PersistViaSettingsServiceAsync()
    {
        try
        {
            await _settingsService!.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "悬浮窗设置写回 ISettingsService 失败（内存中状态保留）");
        }
    }

    private OverlayWindowSettings? GetLiveContainerSettings(string overlayKey)
    {
        var container = _settingsService!.Current.Overlays;
        return overlayKey == NoticeKey ? container.Notice : container.Homework;
    }

    private void RaiseSettingsPersisted()
    {
        try
        {
            SettingsPersisted?.Invoke(this, Settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SettingsPersisted 订阅者异常（已吞掉）");
        }
    }

    private static bool IsKnownKey(string overlayKey) =>
        KnownKeys.Contains(overlayKey, StringComparer.OrdinalIgnoreCase);

    private static OverlaySettings JsonStoreFileDeepClone(OverlaySettings source)
    {
        return new OverlaySettings
        {
            LaunchWithHost = source.LaunchWithHost,
            Notice = CloneWindowSettings(source.Notice),
            Homework = CloneWindowSettings(source.Homework)
        };
    }

    /// <summary>深拷贝单窗设置（局部克隆用，如复位时避免改动单一来源的活实例）。</summary>
    private static OverlayWindowSettings CloneWindowSettings(OverlayWindowSettings s) => new()
    {
        X = s.X,
        Y = s.Y,
        Width = s.Width,
        Height = s.Height,
        Opacity = s.Opacity,
        FontSize = s.FontSize,
        Topmost = s.Topmost,
        Visible = s.Visible
    };
}
