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
        services.AddSingleton<JsonPendingConfirmStore>(sp =>
            new JsonPendingConfirmStore(
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<JsonPendingConfirmStore>>()));
        services.AddSingleton<IPendingConfirmStore>(sp => sp.GetRequiredService<JsonPendingConfirmStore>());
        // 人工确认写回：由 Services/Pipeline/MessageDispatchService 在启动时挂接 store.Resolved 回调
        services.AddSingleton<ISubjectClassifierChain>(sp =>
            new SubjectClassifierChain(
                sp.GetRequiredService<ISubjectClassifier>(),
                sp.GetServices<IAiProvider>(),
                sp.GetRequiredService<IPendingConfirmStore>(),
                sp.GetRequiredService<SubjectChainOptionsProvider>(),
                sp.GetService<ILogger<SubjectClassifierChain>>()));

        // ==== 模块 DI 注册区 ====
        // 模块 4：文件处理管道（原子下载 / MD5 去重 / 路径安全 / 磁盘上限）
        services.AddSingleton(_ => new FilePipelineOptionsProvider
        {
            // 模块 7：接入 ISettingsService，热读取文件设置（含配置热更新）
            GetSettings = () => settingsService.Current.Files,
            DataDirectory = dataDir
        });
        services.AddSingleton<FilePipelineService>();
        services.AddSingleton<IFilePipelineService>(sp => sp.GetRequiredService<FilePipelineService>());

        // ---- 模块 5：持久化层（通知/作业存储，JSON 原子写入 + .bak 损坏恢复）----
        services.AddSingleton(sp =>
            new NoticeStore(dataDir, sp.GetService<ILogger<NoticeStore>>()));
        services.AddSingleton<INoticeStore>(sp => sp.GetRequiredService<NoticeStore>());
        services.AddSingleton(sp =>
            new HomeworkStore(dataDir, sp.GetService<ILogger<HomeworkStore>>()));
        services.AddSingleton<IHomeworkStore>(sp => sp.GetRequiredService<HomeworkStore>());

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
            sp.GetService<ILogger<SubjectFilesSuspensionWindow>>()));
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
            settingsService));
        services.AddSingleton(sp =>
            new SuspensionWindowController(
                dataDir,
                sp.GetService<ILogger<SuspensionWindowController>>(),
                overlayKey => overlayKey switch
                {
                    SuspensionWindowController.NoticeKey => new NoticeSuspensionWindow(
                        sp.GetRequiredService<INoticeStore>()),
                    SuspensionWindowController.HomeworkKey => new HomeworkSuspensionWindow(
                        sp.GetRequiredService<IHomeworkStore>()),
                    SuspensionWindowController.FilesKey => sp.GetRequiredService<SubjectFilesSuspensionWindow>(),
                    SuspensionWindowController.CircleKey => (Window?)sp.GetRequiredService<SubjectCircleBarWindow>(),
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

        // ---- 模块 7：设置变更热生效接线（ReloadRules / 悬浮窗 ApplySettingsAsync）----
        services.AddHostedService<Services.Maintenance.SettingsChangeApplier>();

        // ---- 模块 8：宿主启动后执行一次更新检查（异步，不阻塞启动）----
        services.AddHostedService<Services.Maintenance.UpdateCheckStartupService>();

        // ---- 模块 7：设置页分组与五个设置页（连接/分类/悬浮窗/文件/维护）----
        services.AddSettingsPageGroup("classing.settings", "\uE713", "ClassIng");
        services.AddSettingsPage<Controls.SettingsPages.ConnectionSettingsPage>();
        services.AddSettingsPage<Controls.SettingsPages.ClassificationSettingsPage>();
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
        services.AddHostedService<Services.Pipeline.MessageDispatchService>();

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
