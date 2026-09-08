namespace CyberTechRep.Plugin.Services.Overlays;

/// <summary>探针结论（纯逻辑，可单测）。</summary>
internal enum DesktopZProbeResult
{
    /// <summary>已处于目标位置：宿主锚窗紧邻其后，或其后只有宿主进程窗口。</summary>
    AtTarget,

    /// <summary>外来窗口（非宿主进程）位于悬浮窗之后：真位移，需压回锚窗正前方。</summary>
    ForeignBehind,

    /// <summary>沿 Z 序走到末端仍未遇到宿主锚窗：悬浮窗落到了锚窗之后（被宿主桌面层窗口遮挡）。</summary>
    AnchorNotBehind
}

/// <summary>
/// 桌面层 Z 序探针（纯函数；Win32 调用经委托注入，可脱离窗口句柄单测）。
/// <para>
/// 目标位置定义：悬浮窗直接位于宿主主窗口（锚窗）<b>正前方</b>——宿主主窗口钉在 Z 序绝对
/// 最底且会无条件重申（官方 2.1.0.1 行为，宿主侧不可改），悬浮窗绝不去抢「绝对最底」这个
/// 槽位（那正是与宿主重申逻辑形成 Z 序拉锯、三方夹层闪烁的根因），而是把「锚窗正前方」
/// 当作不动点：宿主重申自身到最底不会改变「锚窗在悬浮窗之后」这一关系，探针判定为
/// AtTarget 后不再发任何 SetWindowPos，系统收敛、零振荡。
/// </para>
/// <para>
/// 悬浮窗之后（Z 序更低）允许出现的窗口只有宿主进程自己的窗口（宿主桌面层窗口 + 同进程
/// 的其他悬浮窗，进程号判定）：外来第三方窗口必须位于悬浮窗前方（遮挡悬浮窗），出现在
/// 其后即为「被外来窗口真位移」，此时才执行一次 SetWindowPos（插回锚窗正前方）。
/// </para>
/// </summary>
internal static class DesktopLevelZProbe
{
    /// <summary>取 Z 序相邻窗口委托（模拟 <c>GetWindow</c> 的 GW_HWNDNEXT / GW_HWNDPREV）。</summary>
    public delegate IntPtr GetRelativeWindow(IntPtr hwnd);

    /// <summary>取窗口归属进程号委托（模拟 <c>GetWindowThreadProcessId</c>）。</summary>
    public delegate uint GetOwningPid(IntPtr hwnd);

    /// <summary>向下遍历步数上限：防御异常 Z 序链（理论上不会出现）导致的死循环。</summary>
    private const int MaxWalkSteps = 1024;

    /// <summary>
    /// 判定 <paramref name="widgetHwnd"/> 是否已处于锚窗正前方的目标位置。
    /// 调用契约：<paramref name="anchorHwnd"/> 必须为已解析的有效锚窗（非 0），由
    /// <see cref="ResolveAnchor"/> 提供；锚窗解析失败时调用方应整体跳过 Z 序干预。
    /// </summary>
    public static DesktopZProbeResult Probe(
        IntPtr widgetHwnd, IntPtr anchorHwnd, GetRelativeWindow getNext, GetOwningPid getPid, uint hostPid)
    {
        var current = getNext(widgetHwnd);
        var steps = 0;
        while (current != IntPtr.Zero && steps++ < MaxWalkSteps)
        {
            if (current == anchorHwnd)
            {
                return DesktopZProbeResult.AtTarget;
            }

            if (getPid(current) != hostPid)
            {
                // 外来窗口侵入悬浮窗之后：真位移（无论其前是否还有宿主窗口，均需压回锚窗前）
                return DesktopZProbeResult.ForeignBehind;
            }

            // 同进程窗口（宿主其他窗口/其他悬浮窗）：放行，继续向下找锚窗
            current = getNext(current);
        }

        // 走到 Z 序末端仍未遇到锚窗：锚窗在悬浮窗前方或句柄已失效——
        // 悬浮窗此刻位于锚窗之后（会被宿主桌面层窗口遮挡），需要插回锚窗正前方
        return DesktopZProbeResult.AnchorNotBehind;
    }

    /// <summary>
    /// 解析宿主锚窗（应为钉在绝对最底的宿主主窗口）：
    /// 主路径沿 GW_HWNDNEXT 向 Z 序末端走，取其后最后一个宿主进程窗口（最深锚窗）；
    /// 末端没有宿主窗口（悬浮窗已落到锚窗之后）时，沿 GW_HWNDPREV 向前找第一个宿主窗口。
    /// 找不到任何宿主窗口返回 <see cref="IntPtr.Zero"/>，调用方据此整体跳过 Z 序干预
    ///（宁可暂不归位也绝不盲目 HWND_BOTTOM 与宿主拉锯）。
    /// </summary>
    public static IntPtr ResolveAnchor(
        IntPtr widgetHwnd, GetRelativeWindow getNext, GetRelativeWindow getPrev,
        GetOwningPid getPid, uint hostPid)
    {
        // 主路径：悬浮窗之后（更低 Z 序）的最深宿主窗口
        var anchor = IntPtr.Zero;
        var current = getNext(widgetHwnd);
        var steps = 0;
        while (current != IntPtr.Zero && steps++ < MaxWalkSteps)
        {
            if (getPid(current) == hostPid)
            {
                anchor = current;
            }

            current = getNext(current);
        }

        if (anchor != IntPtr.Zero)
        {
            return anchor;
        }

        // 兜底：锚窗不在悬浮窗之后（悬浮窗在锚窗之后/绝对最底），向前找第一个宿主窗口
        current = getPrev(widgetHwnd);
        steps = 0;
        while (current != IntPtr.Zero && current != widgetHwnd && steps++ < MaxWalkSteps)
        {
            if (getPid(current) == hostPid)
            {
                return current;
            }

            current = getPrev(current);
        }

        return IntPtr.Zero;
    }
}
