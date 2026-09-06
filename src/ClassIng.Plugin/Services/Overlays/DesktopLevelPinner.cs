using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ClassIng.Plugin.Services.Overlays;

/// <summary>
/// 悬浮窗层级钉底器：关闭置顶时把窗口永远钉在桌面层最底（桌面之上、其他所有窗口之下），
/// 开启置顶时交还 Avalonia <c>Topmost</c>（浮在所有窗口之上）。与「固定」「鼠标穿透」开关无关
/// （那两个开关只影响交互方式）。
/// <para>
/// Win32 实现（仅 Windows 生效，其他平台静默跳过）：
/// - <c>WS_EX_NOACTIVATE</c> 恒开：点击/显示都不激活窗口——否则一次点击就会把悬浮窗
///   抬到所有窗口之上（这正是「悬浮窗盖住其他窗口」问题的根源）；
/// - 非置顶：<c>SetWindowPos(HWND_BOTTOM)</c> 压到 Z 序最底；置顶：不打扰 Z 序；
/// - 1 秒兜底计时器恒开：抗「显示桌面」（Win+D 最小化后立即还原，悬浮窗不消失），
///   非置顶模式下同时持续压底（用户拖拽/缩放窗口期间自动让路，不与拖拽会话打架）；
/// - 鼠标穿透（clickThrough）：<c>WS_EX_TRANSPARENT</c> 让鼠标点击直接穿过悬浮窗落到
///   下方窗口；窗口未启用层叠合成时补 <c>WS_EX_LAYERED</c> +
///   <c>SetLayeredWindowAttributes(255)</c>（该补位由本类负责移除）。
/// </para>
/// </summary>
internal sealed class DesktopLevelPinner
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;

    /// <summary>系统进入/退出模态移动或缩放循环（拖拽标题栏/边框期间）。</summary>
    private const uint WmEnterSizeMove = 0x0231;
    private const uint WmExitSizeMove = 0x0232;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private bool _layeredByUs;
    private bool _topmost;
    private bool _inSystemMoveSizeLoop;

    public DesktopLevelPinner(Window window)
    {
        _window = window;
        // 跟踪系统模态移动/缩放循环：拖拽期间 PushToBottom 必须让路，避免
        // 1 秒兜底 SetWindowPos(HWND_BOTTOM) 与 Avalonia BeginMoveDrag 的模态
        // 移动循环打架（拖拽中被压底/重排 Z 序会中断拖拽会话）。
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(window, TrackMoveSizeLoop);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => PushToBottom();

        // Win+D 还原瞬间立即压底：兜底计时器最长要等 1 秒，期间非置顶窗会浮在所有窗口之上
        //（违背「钉在桌面层」不变量）；WindowState 回到 Normal 时马上补一次压底。
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Normal)
            {
                PushToBottom();
            }
        };
    }

    /// <summary>WndProc 钩子：仅记录进入/退出系统移动缩放循环，不吞任何消息。</summary>
    private IntPtr TrackMoveSizeLoop(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            _inSystemMoveSizeLoop = true;
        }
        else if (msg == WmExitSizeMove)
        {
            _inSystemMoveSizeLoop = false;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 应用层级与穿透设置（须在 UI 线程调用）。置顶=关闭时钉桌面层最底；置顶=开启时
    /// 不干预 Z 序（由 Avalonia Topmost 托管）；抗 Win+D 计时器两种模式恒开；
    /// 窗口句柄未创建时（Show 之前调用）跳过样式与计时器，下次 ApplyToWindow 重试。
    /// </summary>
    public void Apply(bool clickThrough, bool topmost)
    {
        var hwnd = GetHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        exStyle |= WsExNoActivate;

        if (clickThrough)
        {
            if ((exStyle & WsExLayered) == 0)
            {
                // 非层叠窗口打上 WS_EX_TRANSPARENT 不生效，需补 LAYERED 并设置属性保证仍正常渲染
                exStyle |= WsExLayered;
                _layeredByUs = true;
                SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
            }

            exStyle |= WsExTransparent;
        }
        else
        {
            exStyle &= ~WsExTransparent;
            if (_layeredByUs)
            {
                exStyle &= ~WsExLayered;
                _layeredByUs = false;
            }
        }

        SetWindowLong(hwnd, GwlExStyle, exStyle);
        _topmost = topmost;
        PushToBottom();
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void PushToBottom()
    {
        if (!_window.IsVisible)
        {
            return;
        }

        // 系统模态移动/缩放循环中（用户正在拖拽标题栏/角部）：完全让路，
        // 既不还原最小化也不压底，保证拖拽会话不被钉底器打断。
        if (_inSystemMoveSizeLoop)
        {
            return;
        }

        // 抗 Win+D：「显示桌面」会把窗口最小化，立即还原，悬浮窗不消失（置顶/非置顶都生效）
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        // 置顶模式不打扰 Z 序（Avalonia Topmost 已置 HWND_TOPMOST）；仅非置顶时压到最底
        if (_topmost)
        {
            return;
        }

        var hwnd = GetHandle();
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
        }
    }

    private IntPtr GetHandle() => _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
}
