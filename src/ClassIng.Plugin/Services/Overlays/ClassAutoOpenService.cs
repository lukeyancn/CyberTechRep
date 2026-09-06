using Avalonia.Threading;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Overlays;

/// <summary>
/// 上课自动弹出对应学科文件悬浮窗联动（模块 10）：
/// <para>
/// 订阅宿主课程服务 <see cref="ILessonsService"/> 的状态迁移事件——
/// <see cref="ILessonsService.OnClass"/>（进入上课）、<see cref="ILessonsService.OnBreakingTime"/>（下课进课间）、
/// <see cref="ILessonsService.OnAfterSchool"/>（放学），另监听
/// <see cref="ILessonsService.CurrentSubject"/> 的 PropertyChanged 以覆盖「连堂无课间直接切课」
/// （事件本身仅在值变化时触发一次，不存在每 tick 重复触发）。
/// </para>
/// <para>
/// 行为：进入上课且科目为真实科目（非哨兵）时，查该学科已归档文件——有文件且
/// <see cref="SubjectCircleBarSettings.AutoOpenWithClass"/> 开启 → 弹出/切换文件悬浮窗到该学科；
/// 无文件 → 不弹，且若此前由联动打开过窗则收起（避免残留上一节课的过期内容；
/// 用户手动打开的窗不受影响，以联动来源标记区分）。下课/放学/查不到当前课 → 收起联动窗。
/// 另监听文件管道 <see cref="IFilePipelineService.FileUpdated"/>（仅 Archived 记录触发评估）：
/// 课中学生把文件发进群归档后，当前这节课立即补弹，不必等到下一次状态迁移。
/// </para>
/// <para>
/// 时序约束：宿主服务必须在 <see cref="StartAsync"/>（宿主容器构建完成后）解析，
/// 不能在插件 Initialize 阶段解析（IAppHost.Host 未就绪）；解析失败只记日志并跳过，
/// 不影响宿主启动。所有评估经 <see cref="Dispatcher.UIThread"/> 调度、整体吞异常记日志，
/// 绝不向宿主事件源抛异常；<see cref="StopAsync"/> 退订全部事件。
/// </para>
/// </summary>
public sealed class ClassAutoOpenService(
    IFilePipelineService pipeline,
    ISettingsService settingsService,
    SubjectFilesController filesController,
    ILogger<ClassAutoOpenService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<ClassAutoOpenService>.Instance;
    private ILessonsService? _lessons;
    private bool _subscribed;

    /// <summary>去重标记：最近一次已成功弹出联动窗的科目名（同科目重复事件不再反复查询/弹窗）。
    /// 仅在真正弹窗后设置；「无归档文件不弹」不算已处理——课中文件归档到达后须允许同科目重评估补弹。</summary>
    private string? _lastOpenedSubject;

    /// <summary>防刷屏标记：最近一次记过「无归档文件不弹」日志的科目名（同一科目课中不重复打这条日志）。</summary>
    private string? _lastNoArchiveSubject;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 宿主容器此时已构建完成；TryGetService 失败（老宿主/异常环境）只记日志跳过
        var lessons = ClassIsland.Shared.IAppHost.TryGetService<ILessonsService>();
        if (lessons is null)
        {
            _logger.LogWarning("未能从宿主解析 ILessonsService，上课联动不启用（不影响宿主启动）");
            return Task.CompletedTask;
        }

        _lessons = lessons;
        lessons.OnClass += OnLessonsEvent;
        lessons.OnBreakingTime += OnLessonsEvent;
        lessons.OnAfterSchool += OnLessonsEvent;
        // 覆盖连堂/无课间直接切课：CurrentSubject 变化时同样评估（宿主仅在实际变化时触发）
        lessons.PropertyChanged += OnLessonsPropertyChanged;
        // 课中文件归档到达（群里发来附件并下载完成）立即重评估：当前这节课马上补弹，不等下一次状态迁移
        pipeline.FileUpdated += OnPipelineFileUpdated;
        // 设置变更（含上课中途打开 AutoOpenWithClass 开关）立即重新评估，
        // 否则开关在课中打开要等到下一次状态迁移才生效，用户感知为「开了也不弹」
        settingsService.SettingsChanged += OnSettingsChanged;
        _subscribed = true;
        _logger.LogInformation("上课联动已接入宿主课程服务，当前状态 {State}，AutoOpenWithClass={AutoOpen}",
            lessons.CurrentState, settingsService.Current.Overlays.SubjectCircle.AutoOpenWithClass);

        // 启动即对一次表：插件在上课中途加载（宿主重启/热重载）时也能恢复联动
        EvaluateOnUi("启动对表");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lessons is not null && _subscribed)
        {
            _lessons.OnClass -= OnLessonsEvent;
            _lessons.OnBreakingTime -= OnLessonsEvent;
            _lessons.OnAfterSchool -= OnLessonsEvent;
            _lessons.PropertyChanged -= OnLessonsPropertyChanged;
        }

        pipeline.FileUpdated -= OnPipelineFileUpdated;
        settingsService.SettingsChanged -= OnSettingsChanged;
        _lessons = null;
        _subscribed = false;
        return Task.CompletedTask;
    }

    private void OnLessonsEvent(object? sender, EventArgs e) => EvaluateOnUi("时间状态迁移");

    private void OnSettingsChanged(object? sender, AppSettings e) => EvaluateOnUi("设置变更");

    private void OnPipelineFileUpdated(object? sender, FileRecord e)
    {
        // 只有真正归档落盘的记录才可能改变「当前学科有无文件」的判定结果；
        // 下载中/失败/重复的中间态不值得一次评估
        if (e.Status == FileStatus.Archived)
        {
            EvaluateOnUi("文件归档更新");
        }
    }

    private void OnLessonsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // ILessonsService 实现 INotifyPropertyChanged；科目属性名以字符串比较（跨程序集稳定）
        if (e.PropertyName == "CurrentSubject")
        {
            EvaluateOnUi("当前科目变更");
        }
    }

    private void EvaluateOnUi(string reason)
    {
        // 事件源在 Avalonia UI 线程，但保持统一调度以兼容任意来源；Post 不阻塞宿主事件派发
        Dispatcher.UIThread.Post(() => _ = EvaluateCoreAsync(reason));
    }

    /// <summary>核心评估：吞掉全部异常记日志，绝不向宿主事件源抛。</summary>
    private async Task EvaluateCoreAsync(string reason)
    {
        try
        {
            var lessons = _lessons;
            if (lessons is null)
            {
                return;
            }

            var state = lessons.CurrentState;
            var subject = lessons.CurrentSubject;
            var confirmed = lessons.IsLessonConfirmed;

            _logger.LogInformation(
                "上课联动评估：Reason={Reason} State={State} Subject={Subject} Confirmed={Confirmed}",
                reason, state, subject?.Name, confirmed);

            // 判定「正在上课」必须以 CurrentState == OnClass 为准（辅以 IsLessonConfirmed），
            // CurrentSubject 非空不代表在上课（查不到课表/科目时是 Fallback 哨兵而非 null）
            if (!ClassAutoOpenDecider.IsInClassState(
                    state == TimeState.OnClass, confirmed, subject?.Name))
            {
                // 下课/放学/空档/查不到当前课：只收联动窗（手动打开的不动），哨兵科目不弹窗；
                // 两个去重标记一并复位，下一节课重新评估
                _lastOpenedSubject = null;
                _lastNoArchiveSubject = null;
                await filesController.HideIfAutoOpenedAsync(reason);
                return;
            }

            var name = subject!.Name!.Trim();
            var autoOpen = settingsService.Current.Overlays.SubjectCircle.AutoOpenWithClass;
            if (!autoOpen)
            {
                // Information 级：运行时 LogLevel=Info 也可取证（此前是 Debug，开关联动关闭时用户完全无迹可循）
                _logger.LogInformation(
                    "上课联动：检测到上课 {Subject}，但 AutoOpenWithClass 未开启（悬浮窗设置页可开启），跳过", name);
                return;
            }

            if (string.Equals(_lastOpenedSubject, name, StringComparison.OrdinalIgnoreCase))
            {
                return; // 本节课已弹过窗的重复事件（状态迁移 + 科目变更 + 文件归档），去重
            }

            var records = await pipeline.GetRecordsAsync();
            var archivedSubjects = records
                .Select(r => SubjectFilesQuery.ExtractSubject(r.ArchivedRelativePath))
                .ToList();
            var matched = ClassAutoOpenDecider.MatchArchivedSubject(name, archivedSubjects);
            if (matched is null)
            {
                // 该学科没有已归档文件：不弹；若联动窗开着（上一节课留下的）则收起。
                // 取舍：不保留旧学科内容展示，避免悬浮窗与当前课不一致造成误导；
                // 用户手动打开的窗仍保持不动。
                // 「无归档文件」不算已处理（不设 _lastOpenedSubject）：课中文件归档到达后
                // 同科目可重评估补弹。日志按科目去重防刷屏，并为空库/不匹配两种情况给出可行动提示。
                if (!string.Equals(_lastNoArchiveSubject, name, StringComparison.OrdinalIgnoreCase))
                {
                    _lastNoArchiveSubject = name;
                    if (records.Count == 0)
                    {
                        _logger.LogInformation(
                            "上课联动：学科 {Subject} 不弹窗——归档库为空（文件记录 0 条）。" +
                            "群里发附件归档后（见文件设置页归档根目录），本节课内或下一节课会自动弹出",
                            name);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "上课联动：学科 {Subject} 不弹窗——归档库有 {Count} 条记录但无该学科文件" +
                            "（归档学科段 [{Segments}]，检查文件设置页学科目录名是否与课表科目名一致）",
                            name, records.Count, string.Join(", ", archivedSubjects.Distinct()));
                    }
                }
                await filesController.HideIfAutoOpenedAsync("当前学科无已归档文件");
                return;
            }

            _lastOpenedSubject = name;
            _lastNoArchiveSubject = null;
            _logger.LogInformation(
                "上课联动：学科 {Subject} 归档命中 {Matched}，弹出文件悬浮窗（文件记录 {Count} 条）",
                name, matched, records.Count);
            await filesController.OpenForClassAsync(matched);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上课联动评估失败 Reason={Reason}（已吞掉，不影响宿主事件源）", reason);
        }
    }
}
