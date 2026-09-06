using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassIng.Plugin.Utils;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Maintenance;

/// <summary>
/// 模块 7：五组设置（连接/分类/悬浮窗/文件/维护）的加载、保存、导入导出与恢复默认。
/// <para>
/// 持久化文件：&lt;数据目录&gt;/settings.json（原子写入：临时文件 + File.Move 覆盖）。
/// 任何保存/导入/恢复默认操作完成后广播 <see cref="SettingsChanged"/>，订阅方（各模块
/// <c>ReloadRules</c>/<c>UpdateSettings</c> 接线器）据此即时生效。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsService : ISettingsService
{
    /// <summary>当前支持的配置结构版本；导入时强校验，不匹配即拒绝。</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _filePath;
    private readonly ILogger _logger;

    public AppSettings Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <param name="dataDirectory">插件数据目录（settings.json 存放处）。</param>
    /// <param name="logger">可选结构化日志。</param>
    public SettingsService(string dataDirectory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _logger = logger ?? NullLogger.Instance;
        _filePath = Path.Combine(dataDirectory, "settings.json");
        Current = LoadOrDefault();
    }

    /// <summary>settings.json 完整路径（诊断用）。</summary>
    public string ConfigFilePath => _filePath;

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken ct = default)
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = Current;
        }

        await WriteAtomicallyAsync(snapshot, ct).ConfigureAwait(false);
        _logger.LogInformation("设置已保存：{Path}", _filePath);
        RaiseChanged(snapshot);
    }

    /// <inheritdoc />
    public Task ImportAsync(string json, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        AppSettings imported;
        try
        {
            imported = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                       ?? throw new FormatException("导入内容反序列化结果为空");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"导入内容不是合法的 CyberTechRep 设置 JSON：{ex.Message}", ex);
        }

        if (imported.SchemaVersion != SupportedSchemaVersion)
        {
            throw new FormatException(
                $"SchemaVersion 不兼容：期望 {SupportedSchemaVersion}，实际 {imported.SchemaVersion}");
        }

        AppSettings merged;
        lock (_lock)
        {
            // 需求 5：AI 字段旧位置（Classification）→ 新位置（Ai）兼容迁移。
            // 必须先于「本地密文回填」执行：迁移按导入 JSON 的值判断，
            // 否则回填进 Classification 的本地密文会被迁移当作旧值搬走并清空（导入丢失本地密钥）。
            MigrateLegacyAiFields(imported);
            // 导入安全策略：Secret 字段保留本地加密值，防止恶意/误导入的密文或空值覆盖本机凭据。
            imported.Connection.AppSecretProtected = Current.Connection.AppSecretProtected;
            imported.Classification.CloudApiKeyProtected = Current.Classification.CloudApiKeyProtected;
            imported.Ai.CloudApiKeyProtected = Current.Ai.CloudApiKeyProtected;
            merged = imported;
            Current = merged;
        }

        // 独立保存任务：让导入（含写盘与广播）不阻塞调用方同步语义
        return SaveAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        AppSettings sanitized;
        lock (_lock)
        {
            // 导出脱敏：敏感字段置空（不导出密文，也不导出可反推明文的内容）。
            sanitized = DeepClone(Current);
            sanitized.Connection.AppSecretProtected = "";
            sanitized.Classification.CloudApiKeyProtected = "";
            sanitized.Ai.CloudApiKeyProtected = "";
        }

        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, sanitized, JsonOptions, ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc />
    public async Task ResetToDefaultsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            Current = new AppSettings();
        }

        await SaveAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("设置已恢复默认值");
    }

    /// <inheritdoc />
    public string Protect(string plainValue) => SecretProtector.Protect(plainValue);

    /// <inheritdoc />
    public string Unprotect(string protectedValue) => SecretProtector.Unprotect(protectedValue);

    private AppSettings LoadOrDefault()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogInformation("设置文件不存在，使用默认设置：{Path}", _filePath);
                return new AppSettings();
            }

            using var stream = File.OpenRead(_filePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            if (loaded is null)
            {
                _logger.LogWarning("设置文件为空，使用默认设置：{Path}", _filePath);
                return new AppSettings();
            }

            if (loaded.SchemaVersion != SupportedSchemaVersion)
            {
                _logger.LogWarning(
                    "设置文件 SchemaVersion {Actual} 与支持版本 {Expected} 不符，使用默认设置",
                    loaded.SchemaVersion, SupportedSchemaVersion);
                return new AppSettings();
            }

            // 需求 5：AI 字段旧位置（Classification）→ 新位置（Ai）一次性迁移
            MigrateLegacyAiFields(loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取设置文件失败，使用默认设置：{Path}", _filePath);
            return new AppSettings();
        }
    }

    private async Task WriteAtomicallyAsync(AppSettings settings, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        // 原子写入：同目录临时文件 → 写入 + 落盘 → File.Move(overwrite)。
        // SaveAsync 的 _lock 只保护快照读取，写盘可能被多路并发触发
        //（悬浮窗拖拽回写 / 设置页保存 / 启动应用），必须串行化避免 .tmp 冲突。
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tempPath = _filePath + ".tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RaiseChanged(AppSettings snapshot)
    {
        try
        {
            SettingsChanged?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            // 广播订阅方异常不阻断设置服务本身
            _logger.LogError(ex, "SettingsChanged 订阅方处理异常");
        }
    }

    /// <summary>
    /// AI 设置一次性迁移（需求 5）：ClassificationSettings 中的旧 AI 字段 → AiSettings。
    /// 旧位置有值而新位置为默认值时复制过去；迁移完成后旧字段复位为默认值，
    /// 下次保存即从 settings.json 移除（避免「用户清空新位置 → 重启被旧值复活」的回写问题）。
    /// 已配置密钥的用户升级后无需重新填写。
    /// </summary>
    internal static void MigrateLegacyAiFields(AppSettings settings)
    {
        var legacy = settings.Classification;
        var ai = settings.Ai;

        // AI 总开关：旧值 false（关闭）迁移；默认 true 无需迁移
        if (!legacy.AiEnabled && ai.AiEnabled)
        {
            ai.AiEnabled = false;
            legacy.AiEnabled = true;
        }

        if (!string.IsNullOrWhiteSpace(legacy.CloudEndpoint) && string.IsNullOrWhiteSpace(ai.CloudEndpoint))
        {
            ai.CloudEndpoint = legacy.CloudEndpoint;
            legacy.CloudEndpoint = "";
        }

        if (!string.IsNullOrWhiteSpace(legacy.CloudApiKeyProtected) && string.IsNullOrWhiteSpace(ai.CloudApiKeyProtected))
        {
            ai.CloudApiKeyProtected = legacy.CloudApiKeyProtected;
            legacy.CloudApiKeyProtected = "";
        }

        if (!string.IsNullOrWhiteSpace(legacy.CloudModelName) && string.IsNullOrWhiteSpace(ai.CloudModelName))
        {
            ai.CloudModelName = legacy.CloudModelName;
            legacy.CloudModelName = "";
        }

        if (legacy.CloudDailyCallLimit != 200 && ai.CloudDailyCallLimit == 200)
        {
            ai.CloudDailyCallLimit = legacy.CloudDailyCallLimit;
            legacy.CloudDailyCallLimit = 200;
        }

        if (Math.Abs(legacy.ConfidenceThreshold - 0.7) > 0.0001 && Math.Abs(ai.ConfidenceThreshold - 0.7) < 0.0001)
        {
            ai.ConfidenceThreshold = legacy.ConfidenceThreshold;
            legacy.ConfidenceThreshold = 0.7;
        }

        if (!legacy.PreferLocalModel && ai.PreferLocalModel)
        {
            ai.PreferLocalModel = false;
            legacy.PreferLocalModel = true;
        }

        if (legacy.LocalModelPath != "models/subject-classifier.onnx"
            && ai.LocalModelPath == "models/subject-classifier.onnx")
        {
            ai.LocalModelPath = legacy.LocalModelPath;
            legacy.LocalModelPath = "models/subject-classifier.onnx";
        }
    }

    internal static AppSettings DeepClone(AppSettings source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;
    }
}
