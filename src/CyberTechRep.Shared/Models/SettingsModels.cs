namespace CyberTechRep.Shared.Models;

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

    /// <summary>CyberTechRep AI：四用途识别模式与 AI 采样参数。</summary>
    public AiSettings Ai { get; set; } = new();

    public OverlaySettings Overlays { get; set; } = new();

    /// <summary>学科识别模式（需求 4）：纯关键词 / 成员绑定优先；含未绑定学科选择悬浮窗开关。</summary>
    public SubjectRecognitionSettings SubjectRecognition { get; set; } = new();

    public FileSettings Files { get; set; } = new();

    public MaintenanceSettings Maintenance { get; set; } = new();

    /// <summary>常态化作业（按学科配置的固定作业项，整理并发送确认窗口可勾选落档）。</summary>
    public StandingHomeworkSettings StandingHomework { get; set; } = new();
}

/// <summary>常态化作业设置（需求 3）：按学科维护的固定作业项，设置页可增删改。</summary>
public sealed class StandingHomeworkSettings
{
    /// <summary>常态化作业项（顺序即确认窗口与落档顺序）。</summary>
    public IReadOnlyList<StandingHomeworkItem> Items { get; set; } = [];
}

/// <summary>
/// 单条常态化作业（如数学「校本往后做一课」）：
/// 在「整理并发送·确认」窗口以可勾选形式列出，勾选后写入该学科作业文档末尾，
/// <b>点「发送」才落档</b>（勾选只影响预览，取消勾选即从预览移除，不写存档）。
/// </summary>
public sealed class StandingHomeworkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>学科（写入目标文档的学科名；空学科不参与确认窗口列表）。</summary>
    public string Subject { get; set; } = "";

    /// <summary>作业内容（追加到该学科文档末尾的一行/一段文本）。</summary>
    public string Content { get; set; } = "";

    /// <summary>启用状态（false = 确认窗口不列出，存档保留）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 消息接入协议模式：QQ 官方机器人开放平台（默认，现状行为）/
/// NapCat（OneBot 11，可选替代）。默认 Official 保证零回归。
/// </summary>
public enum MessageConnectionMode
{
    /// <summary>QQ 官方机器人开放平台 WebSocket 网关（默认 = 迁移前行为不变）。</summary>
    Official = 0,

    /// <summary>NapCat（OneBot 11）：正向 WS 连接 NapCat 服务端，或反向 WS 监听 NapCat 接入。</summary>
    NapCat = 1
}

/// <summary>NapCat 登录方式（启动参数决定）：扫码 = 不传 QQ 号，二维码经 WebUI/控制台展示；
/// 快速登录 = 每次启动把 QQ 号作为启动参数传入，跳过扫码。</summary>
public enum NapCatLoginMode
{
    /// <summary>扫码登录（默认；新电脑首次登录用，成功后 NapCat 自身会记住会话）。</summary>
    ScanQrCode = 0,

    /// <summary>QQ 号快速登录（每次启动默认使用该 QQ 号）。</summary>
    QuickLoginQQ = 1
}

/// <summary>连接设置（QQ 官方机器人开放平台）。</summary>
public sealed class ConnectionSettings
{
    /// <summary>
    /// 接入模式：Official（默认，QQ 官方平台，现状行为完全不变）/ NapCat（OneBot 11，可选替代）。
    /// 模式切换后需经排错面板手动重连或重启插件生效（ApplySettings 不自动重建连接）。
    /// </summary>
    public MessageConnectionMode Mode { get; set; } = MessageConnectionMode.Official;

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

    /// <summary>
    /// 作业清单「整理并发送」目标群（群 OpenID）：发送目标独立列表，与 <see cref="GroupWhitelist"/>
    /// （消息接管白名单）相互独立，白名单变化不影响发送目标。
    /// 默认空列表 = 未配置目标群（不迁移旧白名单数据，需用户显式填写）；空时发送明确报错回 UI（不静默）。
    /// </summary>
    public IReadOnlyList<string> TargetGroupOpenIds { get; set; } = [];

    /// <summary>断线重连：初始延迟（秒）/倍增系数/上限（秒）。</summary>
    public int ReconnectInitialDelaySec { get; set; } = 2;

    public double ReconnectBackoffFactor { get; set; } = 2.0;

    public int ReconnectMaxDelaySec { get; set; } = 300;

    /// <summary>历史消息回溯天数。官方平台无历史补拉 API，当前恒为 0（保留字段兼容未来）。</summary>
    public int HistoryBackfillDays { get; set; } = 0;

    // ---- NapCat（OneBot 11）模式设置：仅 Mode == NapCat 时使用，默认值不影响 Official 行为 ----

    /// <summary>
    /// NapCat 正向 WS 服务端地址（NapCat「WebSocket 服务器」配置项，如 ws://127.0.0.1:3001）。
    /// 非空时优先按正向 WS 连接 NapCat；为空且反向端口 &gt; 0 时启用反向 WS 监听。
    /// </summary>
    public string NapCatWsUrl { get; set; } = "";

    /// <summary>
    /// NapCat 反向 WS 监听端口（NapCat「WebSocket 客户端」配置项指向 ws://127.0.0.1:此端口）。
    /// 0 = 不启用反向监听；NapCat 模式需至少配置正向地址与反向端口其一。
    /// </summary>
    public int NapCatReversePort { get; set; } = 3001;

    /// <summary>NapCat access token（DPAPI 加密存储，UI 脱敏显示，绝不入日志）。</summary>
    public string NapCatAccessTokenProtected { get; set; } = "";

    /// <summary>NapCat 可执行文件路径（一键启动 NapCat 用；插件不内置 NapCat 本体）。</summary>
    public string NapCatExePath { get; set; } = "";

    /// <summary>NapCat 工作目录（可选；为空时使用可执行文件所在目录）。</summary>
    public string NapCatWorkDirectory { get; set; } = "";

    /// <summary>
    /// NapCat 登录方式：扫码（默认；新电脑首次登录推荐，二维码在 NapCat WebUI 展示）/
    /// QQ 号快速登录（每次启动把该 QQ 号作为启动参数传入，跳过扫码）。
    /// </summary>
    public NapCatLoginMode NapCatLoginMode { get; set; } = NapCatLoginMode.ScanQrCode;

    /// <summary>快速登录 QQ 号（NapCatLoginMode = QuickLoginQQ 时每次启动作为参数传入启动器）。</summary>
    public string NapCatQuickLoginQQ { get; set; } = "";

    /// <summary>ClassIsland 启动后自动后台拉起 NapCat（无窗口；仅 NapCat 模式且已配置可执行文件时生效）。</summary>
    public bool NapCatAutoStart { get; set; }

    /// <summary>NapCat 启动成功后自动用系统默认浏览器打开 WebUI（登录/管理页面）。</summary>
    public bool NapCatOpenWebUiOnStart { get; set; }

    /// <summary>
    /// 排错面板 NapCat 日志环形缓冲行数上限（默认 2000；超出丢弃最旧行，内存占用有界）。
    /// 进程 stdout/stderr 经异步重定向接入，UI 侧 300ms 批量合并渲染。
    /// </summary>
    public int NapCatLogBufferLines { get; set; } = 2000;

    /// <summary>
    /// 断点续传：连接建立后自动拉取历史消息补齐缺口（NapCat 模式，get_group_msg_history）。
    /// 默认开启；游标续传为主、本项为启动核对兜底。
    /// </summary>
    public bool NapCatBackfillOnConnect { get; set; } = true;

    /// <summary>启动核对单群最多回溯条数（默认 100；过大将拖慢首次同步）。</summary>
    public int NapCatBackfillCount { get; set; } = 100;

    /// <summary>
    /// 撤回联动：收到协议端撤回事件（notice.group_recall / friend_recall）时，
    /// 自动从通知/作业存档与学科文档中删除对应消息。默认开启。
    /// </summary>
    public bool RecallSyncEnabled { get; set; } = true;

    /// <summary>
    /// 撤回核对兜底（默认关闭）：定期用 get_msg 探测当日已归档消息是否仍存在，
    /// 不存在则视为已撤回并删除。用于 NapCat 不上报非 API 撤回（issue #1171）的场景；
    /// 因探测存在误判风险（消息超期后 get_msg 也会失败），默认关闭需用户显式开启。
    /// </summary>
    public bool RecallReconcileEnabled { get; set; }

    /// <summary>
    /// 作业清单「整理并发送」总开关（作业悬浮窗「整理并发送」入口与实际发送行为；
    /// 默认 true = 现状开启）。关闭后悬浮窗隐藏发送入口，且发送服务拒绝发送。
    /// </summary>
    public bool HomeworkSendEnabled { get; set; } = true;
}

/// <summary>分类设置。</summary>
public sealed class ClassificationSettings
{
    public IReadOnlyList<string> NoticeKeywords { get; set; } = ["通知", "注意", "提醒", "广播"];

    /// <summary>
    /// 通知学科前缀：已绑定「发送者→学科」映射的发送者发出通知时，
    /// 通知内容前附加「学科名称：」前缀（NoticeStore 写入时生效，默认开）。
    /// </summary>
    public bool NoticeSubjectPrefix { get; set; } = true;

    public IReadOnlyList<string> HomeworkKeywords { get; set; } = ["作业", "练习", "提交", "完成"];

    /// <summary>学科关键词表外置文件相对路径（subjects.json）。</summary>
    public string SubjectRulesPath { get; set; } = "subjects.json";

    // --- AI 识别（旧字段，兼容保留） ---
    // 需求 5：AI 相关设置已整体迁移到 AiSettings（设置 UI 集中在「CyberTechRep AI」页）。
    // 以下旧字段仅为 settings.json 旧结构的一次性迁移保留：加载/导入时若旧位置有值而
    // 新位置为默认值，由 SettingsService 迁移到 AiSettings 后将旧字段复位为默认值。
    // 运行时代码一律读取 AiSettings，不再读取此处。

    /// <summary>旧字段（迁移用）：AI 总开关，现读 <see cref="AiSettings.AiEnabled"/>。</summary>
    public bool AiEnabled { get; set; } = true;

    /// <summary>旧字段（迁移用）：本地 ONNX 优先，现读 <see cref="AiSettings.PreferLocalModel"/>。</summary>
    public bool PreferLocalModel { get; set; } = true;

    /// <summary>旧字段（迁移用）：本地模型路径，现读 <see cref="AiSettings.LocalModelPath"/>。</summary>
    public string LocalModelPath { get; set; } = "models/subject-classifier.onnx";

    /// <summary>旧字段（迁移用）：云端端点，现读 <see cref="AiSettings.CloudEndpoint"/>。</summary>
    public string CloudEndpoint { get; set; } = "";

    /// <summary>旧字段（迁移用）：云端 API Key 密文，现读 <see cref="AiSettings.CloudApiKeyProtected"/>。</summary>
    public string CloudApiKeyProtected { get; set; } = "";

    /// <summary>旧字段（迁移用）：云端模型名，现读 <see cref="AiSettings.CloudModelName"/>。</summary>
    public string CloudModelName { get; set; } = "";

    /// <summary>旧字段（迁移用）：云端每日上限，现读 <see cref="AiSettings.CloudDailyCallLimit"/>。</summary>
    public int CloudDailyCallLimit { get; set; } = 200;

    /// <summary>旧字段（迁移用）：置信度阈值，现读 <see cref="AiSettings.ConfidenceThreshold"/>。</summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    public bool ManualConfirmQueueEnabled { get; set; } = true;
}

/// <summary>CyberTechRep AI 用途模式：AI 在某一识别用途中的参与方式。</summary>
public enum AiUsageMode
{
    /// <summary>关闭：AI 不参与该用途（保持无 AI 的现状行为）。</summary>
    Off = 0,

    /// <summary>
    /// 唯一识别：跳过/替代前面级别（关键词等），AI 为该用途的唯一识别方式；
    /// AI 失败或不可用时自动降级回原有链路，不阻塞主流程。
    /// 注意：二分类等逐条消息场景下此模式会对每条消息调用 AI，产生持续 API 费用。
    /// </summary>
    Primary = 1,

    /// <summary>后补识别：前面级别（关键词）不中或低置信时才轮到 AI。</summary>
    Backup = 2
}

/// <summary>云端 API 提供者类型（需求 5+6：OpenAI 兼容 / Anthropic Messages API）。</summary>
public enum CloudAiProvider
{
    /// <summary>OpenAI 兼容 chat/completions 端点（默认 = 迁移前行为不变）。</summary>
    OpenAiCompatible = 0,

    /// <summary>Anthropic Messages API（{base}/v1/messages，x-api-key + anthropic-version 头）。</summary>
    Anthropic = 1
}

/// <summary>
/// CyberTechRep AI 设置（需求 6）：AI 的四种用途各自独立选择识别模式。
/// <para>
/// 默认值原则：<strong>未配置 AI 时现状行为完全不变</strong>——
/// 学科分类默认 <see cref="AiUsageMode.Backup"/>（与既有「关键词不中 → 云端 LLM」链路语义一致，
/// 云端未配置时该级自然不可用，行为不变）；其余用途默认 <see cref="AiUsageMode.Off"/>。
/// </para>
/// <para>
/// 需求 5：云端凭据与 AI 相关字段已从 <see cref="ClassificationSettings"/> 整体迁移至此
/// （settings.json 旧位置字段由 SettingsService 一次性迁移，已配置密钥的用户无需重填）。
/// </para>
/// </summary>
public sealed class AiSettings
{
    // ---- AI 总开关与云端 API 接口配置（自 ClassificationSettings 迁入）----

    /// <summary>AI 识别总开关（本地 ONNX 与云端 LLM 共用；关闭后所有 AI 级自动跳过）。</summary>
    public bool AiEnabled { get; set; } = true;

    /// <summary>云端 API 提供者类型：OpenAI 兼容 / Anthropic（路由层按此无感切换）。</summary>
    public CloudAiProvider CloudProvider { get; set; } = CloudAiProvider.OpenAiCompatible;

    /// <summary>云端 API 端点（OpenAI 兼容 Base URL 或 Anthropic API Base；密钥加密存储）。</summary>
    public string CloudEndpoint { get; set; } = "";

    /// <summary>云端 API Key（DPAPI 加密存储，UI 脱敏显示，绝不入日志）。</summary>
    public string CloudApiKeyProtected { get; set; } = "";

    /// <summary>云端模型名称。</summary>
    public string CloudModelName { get; set; } = "";

    /// <summary>云端费用/频率保护：每日最大调用次数。</summary>
    public int CloudDailyCallLimit { get; set; } = 200;

    /// <summary>置信度阈值：低于此值进人工确认队列。</summary>
    public double ConfidenceThreshold { get; set; } = 0.7;

    /// <summary>true=本地 ONNX 优先；false=云端优先（本地 ONNX 为占位实现，设置 UI 不暴露）。</summary>
    public bool PreferLocalModel { get; set; } = true;

    /// <summary>本地 ONNX 模型路径（占位实现预留，设置 UI 不暴露）。</summary>
    public string LocalModelPath { get; set; } = "models/subject-classifier.onnx";

    // ---- 四用途识别模式 ----

    /// <summary>
    /// 用途① 通知作业学科分类：SubjectClassifierChain 的 AI 级路由模式。
    /// Backup（默认）= 关键词不中或低置信才 AI（现状链路）；Primary = 跳过关键词直接 AI；Off = 不用 AI。
    /// </summary>
    public AiUsageMode SubjectClassifyMode { get; set; } = AiUsageMode.Backup;

    /// <summary>
    /// 用途② 通知/作业二分类：关键词分类器 Unknown 时（Backup）或每条消息（Primary）询问 AI；
    /// 默认 Off = 现状（仅关键词，Unknown 消息直接忽略）。
    /// </summary>
    public AiUsageMode MessageClassifyMode { get; set; } = AiUsageMode.Off;

    /// <summary>
    /// 用途③ 无关键词消息兜底识别：文本中无任何通知/作业关键词（消息会被忽略）时，
    /// 兜底走学科识别链判断学科并按作业归档；默认 Off = 现状（直接忽略）。
    /// </summary>
    public AiUsageMode NoKeywordFallbackMode { get; set; } = AiUsageMode.Off;

    /// <summary>
    /// 用途④ 文件分类无独立模式：文件随其所在消息的学科归档，实际行为跟随
    /// <see cref="SubjectClassifyMode"/>（无独立字段，设置页仅做说明）。
    /// </summary>
    public const string FileClassifyNote =
        "文件本身不单独分类：文件随其所在消息的学科归档，实际行为跟随「通知作业学科分类」的模式。";

    /// <summary>云端 LLM 采样温度（0=最确定，多数分类任务建议 0）。</summary>
    public double CloudTemperature { get; set; }
}

/// <summary>学科识别模式（需求 4）：成员绑定的参与方式。</summary>
public enum SubjectRecognitionMode
{
    /// <summary>
    /// 纯关键词（现状）：完全不读取成员学科绑定、不触发选择悬浮窗，
    /// 识别链行为与历史版本完全一致（人工修正 &gt; 识别链 &gt; 发送者映射矩阵不变）。
    /// </summary>
    Keyword = 0,

    /// <summary>
    /// 成员绑定优先（默认）：优先使用成员 OpenID 的显式学科绑定（member-subject-bindings.json）；
    /// 未绑定时照常走关键词识别链；链仍未得出可信学科且成员未绑定时，
    /// 触发「未绑定学科选择悬浮窗」让用户点选学科完成绑定（主流程不阻塞，消息仍按现有降级语义处理）。
    /// </summary>
    MemberSelection = 1
}

/// <summary>
/// 学科识别模式设置（需求 4，独立分组，不与 AiSettings 混用）：
/// <see cref="Mode"/> 决定成员绑定在学科识别中的参与方式，
/// <see cref="SelectionWindowEnabled"/> 是「未绑定学科选择悬浮窗」的显示总开关（关闭后管道不再触发该窗）。
/// </summary>
public sealed class SubjectRecognitionSettings
{
    /// <summary>
    /// 学科识别模式：Keyword（纯关键词，现状行为）/ MemberSelection（默认，成员绑定优先）。
    /// 切换仅影响管道是否消费成员绑定与触发选择悬浮窗；Keyword 档不读取任何新存储，行为与现状逐字节一致。
    /// </summary>
    public SubjectRecognitionMode Mode { get; set; } = SubjectRecognitionMode.MemberSelection;

    /// <summary>
    /// 未绑定学科选择悬浮窗显示开关（需求 3）：开（默认）= 消息需学科分类但发送者未绑定学科时弹出选择窗；
    /// 关 = 完全不触发该窗（消息仍按现有降级语义处理）。
    /// </summary>
    public bool SelectionWindowEnabled { get; set; } = true;
}

/// <summary>悬浮窗设置（通知/作业/学科文件三个悬浮窗 + 学科圆圈启动器共用一组窗口参数结构）。</summary>
public sealed class OverlaySettings
{
    public OverlayWindowSettings Notice { get; set; } = new();

    public OverlayWindowSettings Homework { get; set; } = new();

    /// <summary>
    /// 学科文件悬浮窗（第三悬浮窗）：默认不随宿主显示（Visible=false），
    /// 由学科圆圈栏点击唤出或设置页手动开启。
    /// </summary>
    public OverlayWindowSettings Files { get; set; } = new()
    {
        Visible = false,
        Width = 360,
        Height = 520
    };

    /// <summary>学科圆圈启动器（小型常驻窗，点击圆圈打开/切换学科文件悬浮窗）。</summary>
    public OverlayWindowSettings Circle { get; set; } = new()
    {
        Visible = true,
        Width = 64,
        Height = 440,
        Opacity = 0.85
    };

    /// <summary>
    /// 未绑定学科选择悬浮窗（第五悬浮窗，需求 3）：消息需要学科分类但发送者未绑定学科时由管道触发弹出。
    /// 默认不随宿主显示（Visible=false），只在触发时机显示；位置大小透明度与其他悬浮窗同构并持久化。
    /// </summary>
    public OverlayWindowSettings Selection { get; set; } = new()
    {
        Visible = false,
        Width = 320,
        Height = 260
    };

    /// <summary>学科圆圈栏/学科文件悬浮窗联动设置（排列方向/顺序/视图模式等）。</summary>
    public SubjectCircleBarSettings SubjectCircle { get; set; } = new();

    /// <summary>
    /// 作业悬浮窗学科分组顺序：配置顺序优先，未配置的学科按字母序追加在后。
    /// 空列表（默认）= 保持现状（全部按字母序）。经 SettingsChanged 热生效。
    /// </summary>
    public IReadOnlyList<string> HomeworkGroupOrder { get; set; } = [];

    /// <summary>随宿主 ClassIsland 启动（跟随宿主自启机制）。</summary>
    public bool LaunchWithHost { get; set; } = true;
}

/// <summary>学科圆圈栏/学科文件悬浮窗联动设置。</summary>
public sealed class SubjectCircleBarSettings
{
    /// <summary>圆圈排列方向：Horizontal（横）/ Vertical（竖，默认）。</summary>
    public string Orientation { get; set; } = SubjectCircleBarOptions.OrientationVertical;

    /// <summary>学科顺序（圆圈栏展示与文件悬浮窗学科集合的排序依据；未列出的学科按固定顺序追加）。</summary>
    public IReadOnlyList<string> Order { get; set; } = [];

    /// <summary>文件悬浮窗视图模式：Icons（大图标，默认）/ Details（详细列表）。</summary>
    public string ViewMode { get; set; } = SubjectCircleBarOptions.ViewModeIcons;

    /// <summary>上课联动：进入上课（CurrentState==OnClass）时自动弹出/切换该学科已归档文件的悬浮窗，下课/放学自动收起联动打开的窗。</summary>
    public bool AutoOpenWithClass { get; set; }
}

/// <summary>学科圆圈栏设置的合法取值常量（与 <see cref="SubjectCircleBarSettings"/> 配套）。</summary>
public static class SubjectCircleBarOptions
{
    /// <summary>圆圈横向排列。</summary>
    public const string OrientationHorizontal = "Horizontal";

    /// <summary>圆圈纵向排列（默认）。</summary>
    public const string OrientationVertical = "Vertical";

    /// <summary>大图标视图（图标大、居中，文件名在图标下方，流式排列）。</summary>
    public const string ViewModeIcons = "Icons";

    /// <summary>详细列表视图（小图标在左、完整文件名在右的行列表）。</summary>
    public const string ViewModeDetails = "Details";
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

    /// <summary>
    /// 置顶模式：开 → 悬浮窗浮在所有窗口之上；关（默认）→ 悬浮窗钉在桌面层最底
    /// （桌面之上、其他所有窗口之下），抗「显示桌面」（Win+D）两种模式下均生效。
    /// </summary>
    public bool Topmost { get; set; }

    /// <summary>
    /// 固定模式：开 → 禁用拖拽与缩放（位置大小只能经设置页调整）；关（默认）→
    /// 标题栏可拖动、角部可缩放。不抢焦点与层级无关，由钉底器恒定生效。
    /// 注意：必须保持默认 <c>false</c>——悬浮窗此前（0ac4052 之前）默认可拖拽，
    /// 该默认值曾是拖拽失效回归的根因（0.2.0 曾误设为 true）。
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>鼠标穿透：仅固定模式下生效，鼠标点击直接穿过悬浮窗落到下方窗口。</summary>
    public bool ClickThrough { get; set; }

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

    /// <summary>
    /// 通知保留天数（存储层按 CreatedAt 本地日期分桶，启动/跨天清理）：
    /// 0=永久（默认，不清理）；&gt;0 时已读通知仅保留当天，未读通知不受保留期限制全部保留。
    /// </summary>
    public int NoticesRetentionDays { get; set; }

    /// <summary>
    /// 作业保留天数（存储层按 CreatedAt 本地日期分桶，启动/跨天清理）：
    /// 0=永久（默认，不清理）；&gt;0 保留最近 N 天（含当天）。
    /// </summary>
    public int HomeworkRetentionDays { get; set; }
}
