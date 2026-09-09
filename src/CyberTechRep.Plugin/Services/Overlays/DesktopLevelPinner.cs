using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>
/// 悬浮窗层级钉底器：关闭置顶时把窗口钉在「宿主主窗口正前方」（桌面层：桌面之上、
/// 其他进程窗口之下），开启置顶时交还 Avalonia <c>Topmost</c>（浮在所有窗口之上）。
/// 与「固定」「鼠标穿透」开关无关（那两个开关只影响交互方式）。
/// <para>
/// Win32 实现（仅 Windows 生效，其他平台静默跳过）：
/// - <c>WS_EX_NOACTIVATE</c> 恒开：点击/显示都不激活窗口——否则一次点击就会把悬浮窗
///   抬到所有窗口之上（这正是「悬浮窗盖住其他窗口」问题的根源）；
/// - 非置顶：探针驱动（<see cref="DesktopLevelZProbe"/>）——仅当外来窗口真位移
///   （侵入悬浮窗之后 / 悬浮窗落到宿主锚窗之后）才 <c>SetWindowPos</c> 一次，插回
///   宿主锚窗正前方，绝不用 <c>HWND_BOTTOM</c>（那是宿主主窗口的槽位，抢占必拉锯）；
///   置顶：不打扰 Z 序；
/// - 1 秒兜底计时器恒开：抗「显示桌面」（Win+D 后探测原生 iconic/隐藏态并无激活还原，
///   悬浮窗不消失），同时按需压回锚窗前。已在目标位置时零 SetWindowPos（真 no-op），
///   不会与宿主 Bottommost 重申逻辑、本窗 Show/Hide 或用户拖拽（WM_ENTERSIZEMOVE 期间
///   完全让路）形成任何拉锯；
/// - 鼠标穿透（clickThrough）：<c>WS_EX_TRANSPARENT</c> 让鼠标点击直接穿过悬浮窗落到
///   下方窗口；窗口未启用层叠合成时补 <c>WS_EX_LAYERED</c> +
///   <c>SetLayeredWindowAttributes(255)</c>（该补位由本类负责移除）。
///   若窗口注册了可交互区域（<see cref="IOverlayInteractiveRegionProvider"/>，需求 6：
///   通知内容需在穿透模式下仍可选中复制），则改用 <c>WM_NCHITTEST</c> 分流——
///   区域内返回 <c>HTCLIENT</c>（接收鼠标），区域外返回 <c>HTTRANSPARENT</c>（穿透），
///   此时不设 <c>WS_EX_TRANSPARENT</c>（该样式会让窗口完全收不到鼠标消息）。
/// </para>
/// <para>
/// 设计取舍：未采用「拦截 WM_WINDOWPOSCHANGING 取代周期重申」——外来窗口插入/宿主重申
/// 只改变 <b>其他</b> 窗口的相对 Z 序，悬浮窗自身不会收到任何窗口消息，拦截器看不到这类
/// 位移；探针（纯读、零副作用）+ 按需单次移动能达到同样的「真位移才动作」效果且不依赖
/// 消息时序。<c>SWP_NOACTIVATE</c>（配合 <c>WS_EX_NOACTIVATE</c>）保证移动永不抢焦点，
/// <c>SWP_NOOWNERZORDER</c> 保证不扰动属主链。
/// </para>
/// </summary>
internal sealed class DesktopLevelPinner
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpNoownerzorder = 0x0200;

    /// <summary>系统进入/退出模态移动或缩放循环（拖拽标题栏/边框期间）。</summary>
    private const uint WmEnterSizeMove = 0x0231;
    private const uint WmExitSizeMove = 0x0232;

    /// <summary>鼠标命中测试（需求 6：按可交互区域决定「接收」还是「穿透」）。</summary>
    private const uint WmNcHitTest = 0x0084;

    /// <summary>WM_NCHITTEST 返回值：客户区（接收鼠标）。</summary>
    private static readonly IntPtr HtClient = IntPtr.Zero;

    /// <summary>WM_NCHITTEST 返回值：透明（鼠标消息交给下方窗口）。</summary>
    private static readonly IntPtr HtTransparent = new(-1);

    /// <summary>屏幕坐标点（ScreenToClient 用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref Point32 lpPoint);

    /// <summary>ShowWindow 命令：显示/还原但不激活（配合 WS_EX_NOACTIVATE，绝不抢焦点）。</summary>
    private const int SwShownoactivate = 8;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>取 Z 序相邻窗口（GW_HWNDNEXT=其后方（更低 Z 序）窗口；GW_HWNDPREV=其前方窗口）。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    /// <summary>GW_HWNDPREV：Z 序中位于指定窗口前方的窗口句柄。</summary>
    private const uint GwHwndPrev = 3;

    /// <summary>GW_HWNDNEXT：Z 序中位于指定窗口后方（更低）的窗口句柄。</summary>
    private const uint GwHwndNext = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly uint _hostPid;
    private bool _layeredByUs;
    private bool _topmost;
    private bool _inSystemMoveSizeLoop;
    private volatile bool _clickThrough;
    private volatile IOverlayInteractiveRegionProvider? _interactiveRegionProvider;

    public DesktopLevelPinner(Window window)
    {
        _window = window;
        _hostPid = (uint)Environment.ProcessId;

        // 跟踪系统模态移动/缩放循环：拖拽期间恢复/压底必须让路，避免兜底计时器的
        // SetWindowPos 与 Avalonia BeginMoveDrag 的模态移动循环打架（拖拽中被重排 Z 序
        // 会中断拖拽会话）。同一钩子同时承担需求 6 的命中测试分流。
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(window, WndProcHook);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TimerTick();

        // Win+D 还原瞬间立即恢复+压回：兜底计时器最长要等 1 秒，期间非置顶窗可能浮在
        // 所有窗口之上（违背「钉在桌面层」不变量）；WindowState 回到 Normal 时马上补一次。
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Normal)
            {
                TimerTick();
            }
        };
    }

    /// <summary>
    /// 设置「可交互区域」提供者（需求 6，UI 线程调用）：非 null 时穿透改为命中测试分流
    /// （区域内接收鼠标、区域外穿透），不再整窗 <c>WS_EX_TRANSPARENT</c>；
    /// 传 null 恢复既有「整窗穿透」契约。设置后需再次 <see cref="Apply"/> 生效。
    /// </summary>
    public void SetInteractiveRegionProvider(IOverlayInteractiveRegionProvider? provider)
        => _interactiveRegionProvider = provider;

    /// <summary>WndProc 钩子：记录系统移动缩放循环 + 穿透模式的命中测试分流，不吞其他消息。</summary>
    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            _inSystemMoveSizeLoop = true;
            return IntPtr.Zero;
        }

        if (msg == WmExitSizeMove)
        {
            _inSystemMoveSizeLoop = false;
            return IntPtr.Zero;
        }

        if (msg == WmNcHitTest && _clickThrough && _interactiveRegionProvider is not null)
        {
            return HitTestWithInteractiveRegion(hWnd, lParam, ref handled);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 命中测试：把屏幕坐标换算到客户区逻辑坐标，落在可交互区域内返回 HTCLIENT（接收），
    /// 否则返回 HTTRANSPARENT（穿透到下方窗口）。提供者异常/句柄失效一律按「交回默认处理」兜底。
    /// 分流判定与坐标换算本身是纯逻辑，见 <see cref="OverlayHitTestRouter"/>（可单测）。
    /// </summary>
    private IntPtr HitTestWithInteractiveRegion(IntPtr hWnd, IntPtr lParam, ref bool handled)
    {
        Rect region;
        try
        {
            region = _interactiveRegionProvider!.GetInteractiveRegion();
        }
        catch
        {
            return IntPtr.Zero; // 提供者异常：交回默认处理（避免悬浮窗变「点不透」）
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            handled = true;
            return HtTransparent;
        }

        var (screenX, screenY) = OverlayHitTestRouter.UnpackScreenPoint(lParam);
        var point = new Point32
        {
            X = screenX,
            Y = screenY
        };
        if (!ScreenToClient(hWnd, ref point))
        {
            return IntPtr.Zero;
        }

        var logical = OverlayHitTestRouter.ToClientLogical(point.X, point.Y, _window.RenderScaling);
        handled = true;
        return OverlayHitTestRouter.IsInteractive(region, logical) ? HtClient : HtTransparent;
    }

    /// <summary>
    /// 应用层级与穿透设置（须在 UI 线程调用）。置顶=关闭时钉宿主锚窗前；置顶=开启时
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

        _clickThrough = clickThrough;

        // 需求 6：有「可交互区域」提供者时走命中测试分流（WM_NCHITTEST），
        // 不能设 WS_EX_TRANSPARENT——该样式会让窗口完全收不到鼠标消息，命中测试也无从谈起。
        var useHitTestRouting = clickThrough && _interactiveRegionProvider is not null;

        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        exStyle |= WsExNoActivate;

        if (clickThrough && !useHitTestRouting)
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
        TimerTick();
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    /// <summary>
    /// 兜底计时器心跳（UI 线程）：① 抗 Win+D 还原；② 非置顶时按需压回宿主锚窗前。
    /// 整体吞异常：句柄失效/窗口关闭等瞬态一律静默跳过，下个心跳重试，计时器绝不抛异常。
    /// </summary>
    private void TimerTick()
    {
        try
        {
            RecoverFromShowDesktop();
            AssertZPosition();
        }
        catch
        {
            // 兜底逻辑不允许向 DispatcherTimer 抛异常；瞬态失败由下个心跳自然重试
        }
    }

    /// <summary>
    /// 抗「显示桌面」（Win+D）与外壳隐藏：三条路径全覆盖——
    /// ① Avalonia 已感知最小化（用户点最小化按钮等）：走 Avalonia 还原（既有行为不变）；
    /// ② Avalonia 未感知的原生最小化（Win+D 经外壳直接最小化、不经 WM_SYSCOMMAND，
    ///    Avalonia WindowState 可能保持 Normal 造成探针失灵——用户实测「Win+D 后悬浮窗
    ///    永不回来」的根因）：探测 <c>IsIconic</c> 用 <c>SW_SHOWNOACTIVATE</c> 无激活还原；
    /// ③ 原生被隐藏而 Avalonia 仍视为可见（外壳隐藏变体）：同样无激活恢复。
    /// 控制器/用户主动 Hide（Avalonia IsVisible=false）不在此列，绝不复活。
    /// </summary>
    private void RecoverFromShowDesktop()
    {
        // 控制器/用户 Hide 管辖的窗口不复活（可见性归 Show/Hide 路径管）
        if (!_window.IsVisible)
        {
            return;
        }

        // 系统模态移动/缩放循环中（用户正在拖拽标题栏/角部）：完全让路，
        // 保证拖拽会话不被钉底器打断。
        if (_inSystemMoveSizeLoop)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
            return;
        }

        var hwnd = GetHandle();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return;
        }

        if (IsIconic(hwnd))
        {
            // 原生 iconic 而 Avalonia 未感知：无激活还原（不抢焦点，尊重 WS_EX_NOACTIVATE）；
            // 还原后 Avalonia 会经 WM_SIZE 自行同步 WindowState，此处不写属性避免二次 ShowWindow
            ShowWindow(hwnd, SwShownoactivate);
        }
        else if (!IsWindowVisible(hwnd))
        {
            // 原生已隐藏而 Avalonia IsVisible 仍为 true：外壳隐藏变体，无激活恢复
            ShowWindow(hwnd, SwShownoactivate);
        }
    }

    /// <summary>
    /// 非置顶时把悬浮窗压回宿主锚窗正前方。核心不变量：悬浮窗之后（更低 Z 序）只允许
    /// 宿主进程自己的窗口。探针判定已在目标位置时直接返回（零 SetWindowPos，真 no-op），
    /// 从机制上消除与宿主 Bottommost 重申逻辑的 Z 序拉锯（三方夹层闪烁根因）；
    /// 锚窗解析失败时保守跳过（宁可不归位也不盲目 HWND_BOTTOM 抢宿主槽位）。
    /// </summary>
    private void AssertZPosition()
    {
        // 置顶模式不打扰 Z 序（Avalonia Topmost 已置 HWND_TOPMOST）；仅非置顶时归位
        if (_topmost)
        {
            return;
        }

        var hwnd = GetHandle();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return;
        }

        IntPtr GetNext(IntPtr h) => GetWindow(h, GwHwndNext);
        IntPtr GetPrev(IntPtr h) => GetWindow(h, GwHwndPrev);
        uint GetPid(IntPtr h)
        {
            _ = GetWindowThreadProcessId(h, out var pid);
            return pid;
        }

        var anchor = DesktopLevelZProbe.ResolveAnchor(hwnd, GetNext, GetPrev, GetPid, _hostPid);
        if (anchor == IntPtr.Zero || anchor == hwnd)
        {
            // 解析不到宿主锚窗（极端瞬态）：本次跳过，下个心跳重试
            return;
        }

        if (DesktopLevelZProbe.Probe(hwnd, anchor, GetNext, GetPid, _hostPid) == DesktopZProbeResult.AtTarget)
        {
            // 已在目标位置（锚窗在悬浮窗之后，其后全为宿主进程窗口）：真 no-op，
            // 不发任何 SetWindowPos——宿主重申自身到最底不改变这一关系，系统稳定收敛
            return;
        }

        // 真位移才移动一次：插回宿主锚窗正前方（SWP_NOACTIVATE 不抢焦点，
        // SWP_NOOWNERZORDER 不扰动属主链；移动后探针即达 AtTarget 不动点）
        SetWindowPos(hwnd, anchor, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate | SwpNoownerzorder);
    }

    private IntPtr GetHandle() => _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
}
