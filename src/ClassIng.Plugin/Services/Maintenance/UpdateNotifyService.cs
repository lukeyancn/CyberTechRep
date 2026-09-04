using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ClassIng.Shared.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Maintenance;

/// <summary>更新检测选项（可测试注入）。</summary>
public sealed class UpdateNotifyOptions
{
    /// <summary>
    /// GitHub 仓库（owner/repo 或完整 releases API URL）。可配置，默认占位仓库，
    /// 正式发布前在插件配置中替换。
    /// </summary>
    public string Repository { get; set; } = "lukeyancn/ClassIng";

    /// <summary>当前插件版本（用于比较；默认取入口程序集版本）。</summary>
    public string? CurrentVersion { get; set; }

    /// <summary>检查超时（秒）。</summary>
    public int TimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// 模块 8：更新检测与提示。
/// <para>
/// 通过 GitHub Releases API（api.github.com）检查最新版本；检测到新版本时触发
/// <see cref="UpdateDetected"/> 事件并记日志。悬浮窗接入（事件 → 通知条目展示）属模块 5/6，
/// 本模块只发事件。结果同时落结构化日志，供排错。
/// </para>
/// </summary>
public sealed class UpdateNotifyService : IUpdateNotifyService
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient _httpClient;
    private readonly UpdateNotifyOptions _options;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    /// <inheritdoc />
    public event EventHandler<UpdateInfo>? UpdateDetected;

    public UpdateNotifyService(UpdateNotifyOptions? options = null, ILogger? logger = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? new UpdateNotifyOptions();
        _logger = logger ?? NullLogger.Instance;
        _options.CurrentVersion ??= typeof(UpdateNotifyService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ClassIng-Plugin/1.0");
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public async Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var url = BuildReleasesUrl(_options.Repository);
            var release = await _httpClient
                .GetFromJsonAsync<GitHubRelease>(url, JsonOptions, timeoutCts.Token)
                .ConfigureAwait(false);

            if (release?.TagName is not { Length: > 0 } latestTag)
            {
                _logger.LogInformation("更新检查：远端无版本信息（{Repository}）", _options.Repository);
                return;
            }

            var latest = latestTag.TrimStart('v', 'V');
            var current = _options.CurrentVersion ?? "0.0.0";
            if (!IsNewerVersion(latest, current))
            {
                _logger.LogInformation("更新检查：已是最新版本（Current={Current}, Latest={Latest}）",
                    current, latest);
                return;
            }

            var info = new UpdateInfo(
                latest,
                current,
                release.HtmlUrl ?? $"https://github.com/{_options.Repository}/releases",
                release.Body ?? "");
            _logger.LogInformation(
                "检测到新版本：{Latest}（当前 {Current}），下载：{Url}",
                info.LatestVersion, info.CurrentVersion, info.DownloadUrl);

            try
            {
                UpdateDetected?.Invoke(this, info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateDetected 订阅方处理异常");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("更新检查超时（{Repository}）", _options.Repository);
        }
        catch (Exception ex)
        {
            // 更新检查失败不干扰主流程，仅记日志
            _logger.LogError(ex, "更新检查失败（{Repository}）", _options.Repository);
        }
    }

    /// <summary>
    /// 版本比较（纯函数，供单元测试）：latest 是否比 current 更新。
    /// 逐段比较数字段（1.2.10 > 1.2.9）；段数不足按 0 补齐；非数字段按字符串比较。
    /// </summary>
    internal static bool IsNewerVersion(string latest, string current)
    {
        if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        var l = ParseParts(latest.TrimStart('v', 'V'));
        var c = ParseParts(current.TrimStart('v', 'V'));
        for (var i = 0; i < Math.Max(l.Length, c.Length); i++)
        {
            var lp = i < l.Length ? l[i] : "0";
            var cp = i < c.Length ? c[i] : "0";
            var cmp = ComparePart(lp, cp);
            if (cmp != 0)
            {
                return cmp > 0;
            }
        }

        return false;
    }

    private static string[] ParseParts(string version) => version.Split('.');

    private static int ComparePart(string left, string right)
    {
        if (int.TryParse(left, out var li) && int.TryParse(right, out var ri))
        {
            return li.CompareTo(ri);
        }

        return string.CompareOrdinal(left, right);
    }

    private static string BuildReleasesUrl(string repository)
    {
        if (repository.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            repository.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return repository;
        }

        return $"https://api.github.com/repos/{repository.TrimEnd('/')}/releases/latest";
    }

    /// <summary>GitHub Releases API 响应（仅取所需字段）。</summary>
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}

/// <summary>
/// 模块 8：宿主启动后异步执行一次更新检查（fire-and-forget，不阻塞启动；
/// 检查失败仅记日志）。
/// </summary>
public sealed class UpdateCheckStartupService : IHostedService
{
    private readonly IUpdateNotifyService _updateNotify;
    private readonly ILogger _logger;

    public UpdateCheckStartupService(IUpdateNotifyService updateNotify, ILogger? logger = null)
    {
        _updateNotify = updateNotify;
        _logger = logger ?? NullLogger.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _updateNotify.CheckAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动更新检查异常");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
