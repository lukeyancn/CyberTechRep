using System.Runtime.Versioning;
using Avalonia.Threading;
using CyberTechRep.Plugin.Views;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 图片悬浮窗自动展示服务（需求 9，<see cref="IHostedService"/>）：
/// <para>
/// 订阅文件管道 <see cref="IFilePipelineService.FileUpdated"/>，筛选
/// <see cref="FileStatus.Archived"/> 且按扩展名判定为图片（<see cref="ImageOverlayLogic.IsImageFile"/>，
/// 至少覆盖 png/jpg/jpeg/jfif/gif/bmp/webp）的记录，经 <see cref="Dispatcher.UIThread"/> 调度到 UI 线程后：
/// ① 解析 <see cref="ImageSuspensionWindow"/> 单例（由 DI 提供，首次解析在 UI 线程构造，
/// 避免「Call from invalid thread」导致窗口永不显示的历史缺陷）并调用 <c>AddImage</c> 加入图片；
/// ② 若设置项 <c>Overlays.ImageAutoShowOnReceive</c> 为 true，经
/// <see cref="ISuspensionWindowController.ShowAsync"/> 自动弹出窗口。
/// </para>
/// <para>
/// 开关语义：关闭时图片仍加入窗口数据（用户手动打开也能看到），只是不自动弹窗；
/// 开关为热读取（设置保存后立即生效）。窗口默认隐藏、不随宿主启动显示（<c>Visible</c> 默认 false，
/// 本服务不在启动时主动 Show）。全部异常只记日志，绝不向管道事件源抛；<see cref="StopAsync"/> 退订。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ImageOverlayService(
    IFilePipelineService pipeline,
    ISettingsService settingsService,
    ISuspensionWindowController overlays,
    Func<ImageSuspensionWindow> windowAccessor,
    ILogger<ImageOverlayService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<ImageOverlayService>.Instance;
    private bool _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        pipeline.FileUpdated += OnFileUpdated;
        _subscribed = true;
        _logger.LogInformation(
            "图片悬浮窗自动展示已接入文件管道（收到图片自动展示={AutoShow}）",
            settingsService.Current.Overlays.ImageAutoShowOnReceive);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribed)
        {
            pipeline.FileUpdated -= OnFileUpdated;
            _subscribed = false;
        }

        return Task.CompletedTask;
    }

    private void OnFileUpdated(object? sender, FileRecord e)
    {
        try
        {
            // 只有归档完成的图片才展示；下载中/失败/重复等中间态忽略
            if (e.Status != FileStatus.Archived || !ImageOverlayLogic.IsImageFile(e.FileName))
            {
                return;
            }

            // 事件源可能在后台线程；窗口为 UI 线程亲和对象，统一调度后再解析/操作
            Dispatcher.UIThread.Post(() => _ = HandleImageOnUiThreadAsync(e));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "图片悬浮窗：归档图片事件处理失败（已吞掉，不影响文件管道）");
        }
    }

    /// <summary>UI 线程处理：加入图片 → 按开关决定是否自动弹出。</summary>
    private async Task HandleImageOnUiThreadAsync(FileRecord record)
    {
        try
        {
            // 首次解析在 UI 线程完成窗口构造（DI 单例；与其他悬浮窗同一实例）
            var window = windowAccessor();
            window.AddImage(record);

            // 开关热读取：设置保存后立即生效（关闭时照旧加入数据，只是不自动弹窗）
            if (!settingsService.Current.Overlays.ImageAutoShowOnReceive)
            {
                _logger.LogInformation(
                    "图片悬浮窗：已加入图片 {FileName}，但「收到图片自动展示」关闭，不自动弹出（手动打开仍可见）",
                    record.FileName);
                return;
            }

            await overlays.ShowAsync(SuspensionWindowController.ImageKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "图片悬浮窗：展示归档图片失败（已吞掉）：{FileName}", record.FileName);
        }
    }
}
