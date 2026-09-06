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

    /// <summary>去重标记：最近一次已评估的上课科目名（同一节课重复事件不再反复查询/弹窗）。</summary>
    private string? _lastHandledSubject;

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

        settingsService.SettingsChanged -= OnSettingsChanged;
        _lessons = null;
        _subscribed = false;
        return Task.CompletedTask;
    }

    private void OnLessonsEvent(object? sender, EventArgs e) => EvaluateOnUi("时间状态迁移");

    private void OnSettingsChanged(object? sender, AppSettings e) => EvaluateOnUi("设置变更");

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
                // 下课/放学/空档/查不到当前课：只收联动窗（手动打开的不动），哨兵科目不弹窗
                _lastHandledSubject = null;
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

            if (string.Equals(_lastHandledSubject, name, StringComparison.OrdinalIgnoreCase))
            {
                return; // 同一节课的重复事件（状态迁移 + 科目变更），去重
            }

            _lastHandledSubject = name;

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
                _logger.LogInformation(
                    "上课联动：学科 {Subject} 没有已归档文件，不弹出悬浮窗（文件记录 {Count} 条，归档学科段 [{Segments}]）",
                    name, records.Count, string.Join(", ", archivedSubjects.Distinct()));
                await filesController.HideIfAutoOpenedAsync("当前学科无已归档文件");
                return;
            }

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
