using System.Runtime.Versioning;
using Avalonia.Controls;
using ClassIng.Plugin.Services.Classification;
using ClassIng.Plugin.Services.MessageAccess;
using ClassIng.Plugin.Services.Maintenance;
using ClassIng.Plugin.Services.Files;
using ClassIng.Plugin.Services.Overlays;
using ClassIng.Plugin.Services.Stores;
using ClassIng.Plugin.Services.SubjectChain;
using ClassIng.Plugin.Views;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIng.Plugin;

/// <summary>
/// ClassIng 插件入口：向 ClassIsland 宿主 DI 容器注册服务。
/// 已注册：模块 1 消息接入、模块 2 消息分类、模块 3 三级学科识别链；
/// 设置页/悬浮窗/文件管道由后续模块追加。
/// </summary>
[SupportedOSPlatform("windows")]
[PluginEntrance]
public class ClassIngPlugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 数据目录：模块 7 将替换为宿主插件配置目录（PluginConfigFolder）；当前用用户数据目录兜底
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassIsland", "Plugins", "classisland.classing", "data");

        // ---- 模块 7：设置服务提前构造（下方各 OptionsProvider 的 GetSettings 热读取 Current）----
        PluginRuntime.DataDirectory = dataDir;
        var settingsService = new Services.Maintenance.SettingsService(dataDir);

        services.AddSingleton(_ => new IngestOptionsProvider
        {
            // 模块 7：接入 ISettingsService，热读取连接设置（含配置热更新）
            GetSettings = () => settingsService.Current.Connection,
            SecretUnprotector = Utils.SecretProtector.Unprotect,
            DataDirectory = dataDir
        });
        services.AddSingleton(sp => new MessageIngestService(
            sp.GetRequiredService<IngestOptionsProvider>(),
            sp.GetService<ILogger<MessageIngestService>>()));
        services.AddSingleton<IMessageIngestService>(sp => sp.GetRequiredService<MessageIngestService>());

        // 模块 2：通知/作业关键词分类器（词表外置 JSON，ReloadRules 热生效）
        services.AddSingleton(_ => new ClassifierOptionsProvider
        {
            GetSettings = () => settingsService.Current.Classification,
            DataDirectory = dataDir
        });
        services.AddSingleton<KeywordMessageClassifier>(sp =>
            new KeywordMessageClassifier(
                sp.GetRequiredService<ClassifierOptionsProvider>(),
                sp.GetService<ILogger<KeywordMessageClassifier>>()));
        services.AddSingleton<IMessageClassifier>(sp => sp.GetRequiredService<KeywordMessageClassifier>());

        // 模块 3：三级学科识别链（①关键词 → ②AI 本地优先/云端可配 → ③人工确认队列）
        services.AddSingleton(_ => new SubjectChainOptionsProvider
        {
            // 模块 7：接入 ISettingsService，热读取分类设置（含配置热更新）
            GetSettings = () => settingsService.Current.Classification,
            // 需求 6：CyberTechRep AI 四用途识别模式（AiSettings）热读取
            GetAiSettings = () => settingsService.Current.Ai,
            DataDirectory = dataDir,
            SecretUnprotector = Utils.SecretProtector.Unprotect
        });
        services.AddSingleton<KeywordSubjectClassifier>(sp =>
            new KeywordSubjectClassifier(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<KeywordSubjectClassifier>>()));
        services.AddSingleton<ISubjectClassifier>(sp => sp.GetRequiredService<KeywordSubjectClassifier>());
        services.AddSingleton<LocalOnnxAiProvider>(sp =>
            new LocalOnnxAiProvider(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<LocalOnnxAiProvider>>()));
        services.AddSingleton<IAiProvider>(sp => sp.GetRequiredService<LocalOnnxAiProvider>());
        services.AddSingleton<CloudOpenAiProvider>(sp =>
            new CloudOpenAiProvider(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                logger: sp.GetService<ILogger<CloudOpenAiProvider>>()));
        services.AddSingleton<IAiProvider>(sp => sp.GetRequiredService<CloudOpenAiProvider>());
        // 需求 5+6：Anthropic Messages API 云端提供者（AiSettings.CloudProvider 切换；
        // 两个云端提供者各自按 Provider 类型门控 IsAvailable，链组合器自动跳过不可用者）
        services.AddSingleton<AnthropicCloudProvider>(sp =>
            new AnthropicCloudProvider(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                logger: sp.GetService<ILogger<AnthropicCloudProvider>>()));
        services.AddSingleton<IAiProvider>(sp => sp.GetRequiredService<AnthropicCloudProvider>());
        services.AddSingleton<JsonPendingConfirmStore>(sp =>
            new JsonPendingConfirmStore(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<JsonPendingConfirmStore>>()));
        services.AddSingleton<IPendingConfirmStore>(sp => sp.GetRequiredService<JsonPendingConfirmStore>());
        // 人工确认写回：由 Services/Pipeline/MessageDispatchService 在启动时挂接 store.Resolved 回调
        // 需求 6：学科链注册为具体单例（NoKeywordFallbackClassifier 依赖 ISubjectChainModeRouter），
        // ISubjectClassifierChain 与 ISubjectChainModeRouter 共享同一实例。
        services.AddSingleton(sp =>
        {
            var chain = new SubjectClassifierChain(
                sp.GetRequiredService<ISubjectClassifier>(),
                sp.GetServices<IAiProvider>(),
                sp.GetRequiredService<IPendingConfirmStore>(),
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<SubjectClassifierChain>>());
            return chain;
        });
        services.AddSingleton<ISubjectClassifierChain>(sp => sp.GetRequiredService<SubjectClassifierChain>());
        services.AddSingleton<ISubjectChainModeRouter>(sp => sp.GetRequiredService<SubjectClassifierChain>());

        // 需求 6 用途③：无关键词消息兜底识别（AiSettings.NoKeywordFallbackMode，默认 Off = 现状忽略）
        services.AddSingleton<INoKeywordFallbackClassifier>(sp =>
            new NoKeywordFallbackClassifier(
                sp.GetRequiredService<ISubjectChainModeRouter>(),
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<NoKeywordFallbackClassifier>>()));

        // 需求 6 用途②：通知/作业二分类 AI 路由（AiMessageKindRouter 包装关键词分类器；
        // AiSettings.MessageClassifyMode 默认 Off 时行为与纯关键词分类完全一致）。
        // 需求 5+6：二分类云端提供者按 AiSettings.CloudProvider 在 OpenAI 兼容 / Anthropic 之间选择
        //（CompositeMessageKindProvider 依赖各自 IsAvailable 门控，路由层 AiMessageKindRouter 无感切换）。
        services.AddSingleton(sp =>
            new CloudMessageKindProvider(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                logger: sp.GetService<ILogger<CloudMessageKindProvider>>()));
        services.AddSingleton(sp =>
            new AnthropicMessageKindProvider(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                logger: sp.GetService<ILogger<AnthropicMessageKindProvider>>()));
        services.AddSingleton<IMessageKindAiProvider>(sp =>
            new CompositeMessageKindProvider(
                sp.GetRequiredService<CloudMessageKindProvider>(),
                sp.GetRequiredService<AnthropicMessageKindProvider>()));
        services.AddSingleton<IMessageClassifier>(sp =>
            new AiMessageKindRouter(
                sp.GetRequiredService<KeywordMessageClassifier>(),
                sp.GetService<IMessageKindAiProvider>(),
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<AiMessageKindRouter>>()));

        // ==== 模块 DI 注册区 ====
        // 模块 4：文件处理管道（原子下载 / MD5 去重 / 路径安全 / 磁盘上限）
        services.AddSingleton(_ => new FilePipelineOptionsProvider
        {
            // 模块 7：接入 ISettingsService，热读取文件设置（含配置热更新）
            GetSettings = () => settingsService.Current.Files,
            DataDirectory = dataDir
        });
        // 显式工厂注入 ILogger<FilePipelineService>：构造函数 logger 参数为非泛型 ILogger，
        // 裸 AddSingleton<T>() 下 DI 解析不到非泛型 ILogger 会回落 NullLogger，
        // 启动自检日志「文件管道就绪」会被静默吞掉。
        services.AddSingleton(sp => new FilePipelineService(
            sp.GetRequiredService<FilePipelineOptionsProvider>(),
            logger: sp.GetService<ILogger<FilePipelineService>>()));
        services.AddSingleton<IFilePipelineService>(sp => sp.GetRequiredService<FilePipelineService>());

        // ---- 模块 5：持久化层（通知/作业存储，JSON 原子写入 + .bak 损坏恢复）----
        // UserSubjectRuleStore 共享单例：HomeworkStore（学科优先级矩阵）与
        // NoticeStore（通知学科前缀）共用同一份「发送者→学科」映射。
        services.AddSingleton(sp => new UserSubjectRuleStore(
            dataDir, sp.GetService<ILogger<UserSubjectRuleStore>>()));
        // 需求 2：成员学科显式绑定存储（member-subject-bindings.json）：消息管道（MemberSelection 模式）
        // 与词表编辑页管理 UI、未绑定学科选择悬浮窗共用同一单例（进程内缓存即时生效）。
        services.AddSingleton(sp => new MemberSubjectBindingStore(
            dataDir, sp.GetService<ILogger<MemberSubjectBindingStore>>()));
        services.AddSingleton(sp => new NoticeStore(
            dataDir,
            userRules: sp.GetRequiredService<UserSubjectRuleStore>(),
            noticePrefixEnabled: () => settingsService.Current.Classification.NoticeSubjectPrefix,
            noticesRetentionDays: () => settingsService.Current.Maintenance.NoticesRetentionDays,
            logger: sp.GetService<ILogger<NoticeStore>>()));
        services.AddSingleton<INoticeStore>(sp => sp.GetRequiredService<NoticeStore>());
        services.AddSingleton(sp => new HomeworkStore(
            dataDir,
            sp.GetService<ILogger<HomeworkStore>>(),
            homeworkRetentionDays: () => settingsService.Current.Maintenance.HomeworkRetentionDays,
            userRules: sp.GetRequiredService<UserSubjectRuleStore>()));
        services.AddSingleton<IHomeworkStore>(sp => sp.GetRequiredService<HomeworkStore>());

        // ---- 需求 2：作业清单「整理并发送」（QQ 官方机器人开放平台群消息 REST 发送）----
        // 目标群 = 连接设置群白名单（GroupWhitelist）；发送开关 = 连接设置 HomeworkSendEnabled（默认 true，热生效）
        services.AddSingleton(_ => new HomeworkSendOptionsProvider
        {
            GetSettings = () => settingsService.Current.Connection,
            SecretUnprotector = Utils.SecretProtector.Unprotect,
            GetEnabled = () => settingsService.Current.Connection.HomeworkSendEnabled
        });
        services.AddSingleton(sp => new HomeworkSendService(
            sp.GetRequiredService<HomeworkSendOptionsProvider>(),
            sp.GetService<ILogger<HomeworkSendService>>()));
        services.AddSingleton<IHomeworkSendService>(sp => sp.GetRequiredService<HomeworkSendService>());

        // ---- 模块 6：悬浮窗（Avalonia 无边框置顶窗 + 共享控制器）----
        // 设置单一来源：ISettingsService.Current.Overlays（settings.json）；
        // 首启检测到旧 overlays.json 时由控制器导入后归档（集成收口：双源合一）。
        // 第三悬浮窗（学科文件）与学科圆圈启动器复用同一 ApplyToWindow/钉底器路径。
        // 学科文件悬浮窗注册为显式单例：控制器窗口工厂与 SubjectFilesController 共享同实例，
        // 圆圈 toggle/切换才能原地改内容（不闪关）。
        services.AddSingleton(sp => new SubjectFilesSuspensionWindow(
            sp.GetRequiredService<IFilePipelineService>(),
            () => settingsService.Current.Overlays.SubjectCircle,
            relative => ResolveArchivePath(settingsService.Current.Files, dataDir, relative),
            sp.GetService<ILogger<SubjectFilesSuspensionWindow>>(),
            // 注入控制器与设置服务：右上角「⋯」快捷菜单（置顶/固定/穿透）与通知窗共用 OverlayQuickMenu
            overlays: sp.GetRequiredService<ISuspensionWindowController>(),
            settingsService: settingsService));
        services.AddSingleton(sp => new SubjectFilesController(
            sp.GetRequiredService<ISuspensionWindowController>(),
            sp.GetRequiredService<ISettingsService>(),
            () => sp.GetRequiredService<SubjectFilesSuspensionWindow>(),
            sp.GetService<ILogger<SubjectFilesController>>()));
        services.AddSingleton(sp => new SubjectCircleBarWindow(
            sp.GetRequiredService<SubjectFilesController>(),
            sp.GetRequiredService<IFilePipelineService>(),
            () => settingsService.Current.Overlays.SubjectCircle,
            sp.GetService<ILogger<SubjectCircleBarWindow>>(),
            settingsService,
            // 注入控制器：底部「⋯」快捷菜单（置顶/固定/穿透）与其他悬浮窗共用 OverlayQuickMenu
            overlays: sp.GetRequiredService<ISuspensionWindowController>()));
        services.AddSingleton(sp =>
            new SuspensionWindowController(
                dataDir,
                sp.GetService<ILogger<SuspensionWindowController>>(),
                overlayKey => overlayKey switch
                {
                    // 通知悬浮窗注入控制器与设置服务：右上角快捷菜单（置顶/固定/穿透）
                    // 经控制器 ApplySettingsAsync 路径即时生效并回写 ISettingsService
                    SuspensionWindowController.NoticeKey => new NoticeSuspensionWindow(
                        sp.GetRequiredService<INoticeStore>(),
                        sp.GetRequiredService<ISuspensionWindowController>(),
                        sp.GetRequiredService<ISettingsService>()),
                    SuspensionWindowController.HomeworkKey => new HomeworkSuspensionWindow(
                        sp.GetRequiredService<IHomeworkStore>(),
                        () => settingsService.Current.Overlays.HomeworkGroupOrder,
                        sp.GetRequiredService<ISettingsService>(),
                        sp.GetRequiredService<IHomeworkSendService>(),
                        sp.GetRequiredService<ISuspensionWindowController>()),
                    SuspensionWindowController.FilesKey => sp.GetRequiredService<SubjectFilesSuspensionWindow>(),
                    SuspensionWindowController.CircleKey => (Window?)sp.GetRequiredService<SubjectCircleBarWindow>(),
                    // 第五悬浮窗（未绑定学科选择，需求 3）：单例窗，协调器触发时装载请求并经控制器显示
                    SuspensionWindowController.SubjectSelectionKey => (Window?)sp.GetRequiredService<SubjectSelectionSuspensionWindow>(),
                    _ => (Window?)null
                },
                sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<ISuspensionWindowController>(sp => sp.GetRequiredService<SuspensionWindowController>());

        // ---- 模块 7：设置服务（五组设置加载/保存/导入导出/恢复默认/DPAPI 脱敏；settings.json 原子写入）----
        services.AddSingleton(settingsService);
        services.AddSingleton<ISettingsService>(settingsService);

        // ---- 模块 8：失败重试队列（JSON 持久化 + 指数退避 + 手动重放）----
        services.AddSingleton(_ => new Services.Maintenance.RetryQueueOptions
        {
            GetSettings = () => settingsService.Current.Maintenance,
            DataDirectory = dataDir
        });
        services.AddSingleton(sp => new Services.Maintenance.RetryQueueService(
            sp.GetRequiredService<Services.Maintenance.RetryQueueOptions>(),
            sp.GetService<ILogger<Services.Maintenance.RetryQueueService>>()));
        services.AddSingleton<IRetryQueueService>(sp => sp.GetRequiredService<Services.Maintenance.RetryQueueService>());

        // ---- 模块 8：更新检测（GitHub Releases；默认占位仓库，只发事件 + 记日志）----
        services.AddSingleton(_ => new Services.Maintenance.UpdateNotifyOptions());
        services.AddSingleton(sp => new Services.Maintenance.UpdateNotifyService(
            sp.GetRequiredService<Services.Maintenance.UpdateNotifyOptions>(),
            sp.GetService<ILogger<Services.Maintenance.UpdateNotifyService>>()));
        services.AddSingleton<IUpdateNotifyService>(sp => sp.GetRequiredService<Services.Maintenance.UpdateNotifyService>());

        // ---- 模块 8：环境探测（磁盘空间 / 协议端离线时长，告警事件）----
        services.AddSingleton(sp => new Services.Maintenance.EnvironmentMonitorService(
            new Services.Maintenance.EnvironmentMonitorOptions { MonitoredDirectory = dataDir },
            sp.GetService<IMessageIngestService>(),
            sp.GetService<ILogger<Services.Maintenance.EnvironmentMonitorService>>()));
        services.AddSingleton<IEnvironmentMonitorService>(sp => sp.GetRequiredService<Services.Maintenance.EnvironmentMonitorService>());

        // ---- 模块 8：排错数据聚合出口（连接状态 / 最近消息快照 / 重试队列 + 重放与手动重连入口）----
        services.AddSingleton(sp => new Services.Maintenance.DiagnosticsService(
            sp.GetService<IMessageIngestService>(),
            sp.GetService<IEnvironmentMonitorService>(),
            sp.GetService<IRetryQueueService>(),
            sp.GetService<ILogger<Services.Maintenance.DiagnosticsService>>()));
        services.AddSingleton<IDiagnosticsService>(sp => sp.GetRequiredService<Services.Maintenance.DiagnosticsService>());

        // ---- 消息日志 dump 导出（维护页入口）：订阅 MessageReceived 维护环形缓冲，
        // 导出 JSONL（每行一条全字段结构化数据，按 MessageId 关联分类/学科/文件结果）----
        services.AddSingleton(sp => new Services.Maintenance.MessageDumpService(
            sp.GetService<IMessageIngestService>(),
            sp.GetService<IHomeworkStore>(),
            sp.GetService<INoticeStore>(),
            sp.GetService<IFilePipelineService>(),
            sp.GetService<ILogger<Services.Maintenance.MessageDumpService>>()));
        services.AddSingleton<IMessageDumpService>(sp => sp.GetRequiredService<Services.Maintenance.MessageDumpService>());

        // ---- 模块 7：设置变更热生效接线（ReloadRules / 悬浮窗 ApplySettingsAsync）----
        services.AddHostedService<Services.Maintenance.SettingsChangeApplier>();

        // ---- 模块 8：宿主启动后执行一次更新检查（异步，不阻塞启动）----
        services.AddHostedService<Services.Maintenance.UpdateCheckStartupService>();

        // ---- 模块 7：设置页分组与五个设置页（连接/分类/悬浮窗/文件/维护）----
        services.AddSettingsPageGroup("classing.settings", "\uE713", "CyberTechRep");
        services.AddSettingsPage<Controls.SettingsPages.ConnectionSettingsPage>();
        services.AddSettingsPage<Controls.SettingsPages.ClassificationSettingsPage>();
        // 需求 6：CyberTechRep AI 设置页（四用途识别模式 + 云端采样参数）
        services.AddSettingsPage<Controls.SettingsPages.AiSettingsPage>();
        // 需求 3：学科关键词规则（subjects.json）与消息分类关键词的可视化编辑入口
        services.AddSettingsPage<Controls.SettingsPages.SubjectRulesEditorPage>();
        services.AddSettingsPage<Controls.SettingsPages.OverlaySettingsPage>();
        services.AddSettingsPage<Controls.SettingsPages.FileSettingsPage>();
        services.AddSettingsPage<Controls.SettingsPages.MaintenanceSettingsPage>();

        // ---- 模块 9：首次启动引导（settings.json FirstRunCompleted 标志 + 无边框引导窗）----
        services.AddSingleton(sp => new Services.FirstRun.FirstRunService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetService<ILogger<Services.FirstRun.FirstRunService>>(),
            () => new Views.FirstRunWizardWindow(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<Services.FirstRun.IFirstRunService>())));
        services.AddSingleton<Services.FirstRun.IFirstRunService>(sp =>
            sp.GetRequiredService<Services.FirstRun.FirstRunService>());
        // 宿主启动后检测首次启动并自动弹出引导（失败只记日志，不阻断启动）
        services.AddHostedService<Services.FirstRun.FirstRunStartupService>();

        // ==== 集成收口：消息主数据流统一接线 ====
        // MessageReceived → 分类 → 通知/作业存储 + 文件管道；人工确认 Resolve 写回作业；
        // UpdateDetected → 通知；FileDownload/SubjectClassify/StoreWrite 重试执行器注册；
        // 并负责拉起 IMessageIngestService（此前无宿主启动点）。
        // 全流程 try/catch + 结构化日志，失败按类型进重试队列，任何一环失败不崩溃。
        // 需求 2/4：MemberSelection 模式下消费成员显式绑定存储（Keyword 模式不读取，现状不变），
        // 学科识别模式经委托热读取 settings.json（SubjectRecognitionSettings）。
        services.AddSingleton(sp => new Services.Pipeline.MessageDispatchService(
            sp.GetRequiredService<IMessageIngestService>(),
            sp.GetRequiredService<IMessageClassifier>(),
            sp.GetRequiredService<ISubjectClassifierChain>(),
            sp.GetRequiredService<INoticeStore>(),
            sp.GetRequiredService<IHomeworkStore>(),
            sp.GetRequiredService<IFilePipelineService>(),
            sp.GetRequiredService<Services.Maintenance.RetryQueueService>(),
            sp.GetRequiredService<IUpdateNotifyService>(),
            sp.GetRequiredService<JsonPendingConfirmStore>(),
            sp.GetRequiredService<INoKeywordFallbackClassifier>(),
            sp.GetRequiredService<MemberSubjectBindingStore>(),
            () => settingsService.Current.SubjectRecognition,
            sp.GetService<ILogger<Services.Pipeline.MessageDispatchService>>()));
        services.AddHostedService(sp =>
        {
            // 强制创建协调器完成 SubjectSelectionRequired 事件接线（早于首条消息）
            sp.GetRequiredService<Services.Overlays.SubjectSelectionCoordinator>();
            return sp.GetRequiredService<Services.Pipeline.MessageDispatchService>();
        });

        // ---- 需求 3：未绑定学科选择悬浮窗协调器 ----
        // 订阅 dispatch 的 SubjectSelectionRequired：装载触发请求到单例选择窗并经控制器显示；
        // 用户点选学科 → 写回成员绑定存储 + 该消息作业人工修正 + 文件二次归档。
        services.AddSingleton(sp =>
        {
            var coordinator = new Services.Overlays.SubjectSelectionCoordinator(
                sp.GetRequiredService<Services.Pipeline.MessageDispatchService>(),
                sp.GetRequiredService<ISuspensionWindowController>(),
                () => sp.GetRequiredService<SubjectSelectionSuspensionWindow>(),
                sp.GetRequiredService<MemberSubjectBindingStore>(),
                sp.GetRequiredService<IHomeworkStore>(),
                sp.GetRequiredService<IFilePipelineService>(),
                sp.GetService<ILogger<Services.Overlays.SubjectSelectionCoordinator>>());
            // 事件接线：创建即订阅（均早于首条消息到达）
            coordinator.Attach();
            return coordinator;
        });
        services.AddSingleton<SubjectSelectionSuspensionWindow>(sp => new SubjectSelectionSuspensionWindow(
            (request, subject) => sp.GetRequiredService<Services.Overlays.SubjectSelectionCoordinator>()
                .ApplySelectionAsync(request, subject),
            () => ClassIng.Plugin.Services.SubjectChain.SubjectRuleFile
                .LoadOrSeed(sp.GetRequiredService<SubjectChainOptionsProvider>()).Rules
                .Select(r => r.Subject).ToList()));

        // ---- 模块 5：保留期清理任务（启动时 + 每日跨天 + 设置变更；只删过期桶，绝不动当天与未读）----
        services.AddHostedService<Services.Pipeline.RetentionCleanupService>();

        // ---- 模块 10：上课自动弹出对应学科文件悬浮窗联动 ----
        // IHostedService：StartAsync（宿主容器构建完成后）才解析宿主 ILessonsService；
        // 解析失败只记日志跳过，不影响宿主启动。弹出/收起经 SubjectFilesController 的
        // 联动来源标记执行，只收联动窗不误关手动窗。
        services.AddHostedService<Services.Overlays.ClassAutoOpenService>();
    }

    /// <summary>
    /// 归档相对路径 → 绝对路径（下载根目录 = FileSettings.DownloadRoot，相对路径时基于插件数据目录；
    /// 与文件管道 GetDownloadRoot 的解析规则一致）。路径异常时返回 null（调用方兜底）。
    /// </summary>
    private static string? ResolveArchivePath(FileSettings files, string dataDirectory, string relativePath)
    {
        try
        {
            var configured = files.DownloadRoot;
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = "下载文件";
            }

            var root = Path.IsPathRooted(configured) ? configured : Path.Combine(dataDirectory, configured);
            return Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch
        {
            return null;
        }
    }
}
