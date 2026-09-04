namespace ClassIng.Shared.Models;

/// <summary>根配置对象（含 SchemaVersion，支持导入导出与恢复默认）。</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 首次启动引导是否已完成（模块 9）。
    /// 完成/跳过引导均置 true 并随 settings.json 持久化；删除 settings.json、恢复默认
    /// 或设置文件损坏回退默认值后会重新视为首次启动。导入的配置视为已完成引导。
    /// </summary>
    public bool FirstRunCompleted { get; set; }

    public ConnectionSettings Connection { get; set; } = new();

    public ClassificationSettings Classification { get; set; } = new();

    public OverlaySettings Overlays { get; set; } = new();

    public FileSettings Files { get; set; } = new();

    public MaintenanceSettings Maintenance { get; set; } = new();
}

/// <summary>连接设置（QQ 官方机器人开放平台）。</summary>
public sealed class ConnectionSettings
{
    /// <summary>机器人 AppID（q.qq.com 注册获得）。</summary>
    public string AppId { get; set; } = "";

    /// <summary>AppSecret（DPAPI 加密存储，UI 脱敏显示）。</summary>
    public string AppSecretProtected { get; set; } = "";

    /// <summary>开放平台 API 根地址（默认官方地址；沙箱环境可改）。</summary>
    public string ApiBase { get; set; } = "https://api.sgroup.qq.com";

    /// <summary>AccessToken 服务地址（默认官方地址）。</summary>
    public string TokenApiUrl { get; set; } = "https://bots.qq.com/app/getAppAccessToken";

    /// <summary>群白名单（群 OpenID；空 = 接收全部已接入群，仅建议调试期使用）。</summary>
    public IReadOnlyList<string> GroupWhitelist { get; set; } = [];

    /// <summary>断线重连：初始延迟（秒）/倍增系数/上限（秒）。</summary>
    public int ReconnectInitialDelaySec { get; set; } = 2;

    public double ReconnectBackoffFactor { get; set; } = 2.0;

    public int ReconnectMaxDelaySec { get; set; } = 300;

    /// <summary>历史消息回溯天数。官方平台无历史补拉 API，当前恒为 0（保留字段兼容未来）。</summary>
    public int HistoryBackfillDays { get; set; } = 0;
}

/// <summary>分类设置。</summary>
public sealed class ClassificationSettings
{
    public IReadOnlyList<string> NoticeKeywords { get; set; } = ["通知", "注意", "提醒", "广播"];

    public IReadOnlyList<string> HomeworkKeywords { get; set; } = ["作业", "练习", "提交", "完成"];

    /// <summary>学科关键词表外置文件相对路径（subjects.json）。</summary>
    public string SubjectRulesPath { get; set; } = "subjects.json";

    // --- AI 识别 ---

    public bool AiEnabled { get; set; } = true;

    /// <summary>true=本地 ONNX 优先；false=云端优先。</summary>
    public bool PreferLocalModel { get; set; } = true;

    public string LocalModelPath { get; set; } = "models/subject-classifier.onnx";

    /// <summary>云端 OpenAI 兼容端点与密钥（密钥加密存储）。</summary>
    public string CloudEndpoint { get; set; } = "";

    public string CloudApiKeyProtected { get; set; } = "";

    public string CloudModelName { get; set; } = "";

    /// <summary>云端费用/频率保护：每日最大调用次数。</summary>
    public int CloudDailyCallLimit { get; set; } = 200;

    /// <summary>置信度阈值：低于此值进人工确认队列。</summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    public bool ManualConfirmQueueEnabled { get; set; } = true;
}

/// <summary>悬浮窗设置（两个悬浮窗各自一组）。</summary>
public sealed class OverlaySettings
{
    public OverlayWindowSettings Notice { get; set; } = new();

    public OverlayWindowSettings Homework { get; set; } = new();

    /// <summary>随宿主 ClassIsland 启动（跟随宿主自启机制）。</summary>
    public bool LaunchWithHost { get; set; } = true;
}

/// <summary>单个悬浮窗的外观与位置。</summary>
public sealed class OverlayWindowSettings
{
    /// <summary>逻辑坐标（DIP，跨 DPI 一致）。</summary>
    public double X { get; set; } = 40;

    public double Y { get; set; } = 40;

    public double Width { get; set; } = 320;

    public double Height { get; set; } = 480;

    /// <summary>0.1~1.0。</summary>
    public double Opacity { get; set; } = 0.92;

    public double FontSize { get; set; } = 14;

    public bool Topmost { get; set; } = true;

    public bool Visible { get; set; } = true;
}

/// <summary>文件设置。</summary>
public sealed class FileSettings
{
    /// <summary>下载根目录（默认：插件数据目录/下载文件）。</summary>
    public string DownloadRoot { get; set; } = "下载文件";

    /// <summary>按学科建目录归档。</summary>
    public bool GroupBySubject { get; set; } = true;

    /// <summary>MD5 去重开关。</summary>
    public bool Md5DedupEnabled { get; set; } = true;

    /// <summary>磁盘占用上限（MB），0=不限。</summary>
    public long MaxDiskUsageMb { get; set; } = 2048;

    /// <summary>清理策略：0=停止接收新文件并告警；1=清理最旧文件。</summary>
    public int CleanupPolicy { get; set; }

    public int DownloadConcurrency { get; set; } = 2;

    /// <summary>限速 KB/s，0=不限。</summary>
    public int SpeedLimitKbps { get; set; }

    /// <summary>单文件大小上限（MB），超出拒绝并告警。</summary>
    public long MaxFileSizeMb { get; set; } = 512;

    /// <summary>文件名最大长度。</summary>
    public int MaxFileNameLength { get; set; } = 200;
}

/// <summary>维护设置。</summary>
public sealed class MaintenanceSettings
{
    /// <summary>Trace/Debug/Info/Warning/Error。</summary>
    public string LogLevel { get; set; } = "Info";

    public bool RetryQueueEnabled { get; set; } = true;

    /// <summary>重试上限次数（超出 GivenUp，需人工重放）。</summary>
    public int MaxRetryAttempts { get; set; } = 5;
}
