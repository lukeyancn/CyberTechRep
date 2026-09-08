using CyberTechRep.Plugin.Services.Overlays;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 桌面层 Z 序探针纯逻辑单测：用「前→后」列表模拟 Z 序（索引 0 最前/最顶），
/// GW_HWNDNEXT = 向后（更低 Z 序）走一格，GW_HWNDPREV = 向前走一格；
/// 进程号区分宿主进程窗口与外来第三方窗口。
/// 目标位置不变量：悬浮窗之后（更低 Z 序）只允许宿主进程窗口（锚窗=宿主主窗口，
/// 钉在绝对最底且会无条件重申；悬浮窗绝不抢 HWND_BOTTOM 槽位）。
/// </summary>
public sealed class DesktopLevelZProbeTests
{
    private const uint HostPid = 100;
    private const uint ForeignPid = 200;

    private static IntPtr H(long value) => new(value);

    /// <summary>Z 序模拟器：front→back 列表 + 进程号表；委托签名与 DesktopLevelZProbe 对齐。</summary>
    private sealed class ZOrder
    {
        private readonly List<IntPtr> _frontToBack;
        private readonly Dictionary<long, uint> _pids;

        public ZOrder(long[] frontToBack, Dictionary<long, uint> pids)
        {
            _frontToBack = frontToBack.Select(H).ToList();
            _pids = pids;
        }

        public IntPtr GetNext(IntPtr hwnd)
        {
            var index = _frontToBack.IndexOf(hwnd);
            return index >= 0 && index + 1 < _frontToBack.Count ? _frontToBack[index + 1] : IntPtr.Zero;
        }

        public IntPtr GetPrev(IntPtr hwnd)
        {
            var index = _frontToBack.IndexOf(hwnd);
            return index > 0 ? _frontToBack[index - 1] : IntPtr.Zero;
        }

        public uint GetPid(IntPtr hwnd) => _pids.TryGetValue(hwnd.ToInt64(), out var pid) ? pid : 0;

        public DesktopZProbeResult Probe(IntPtr widget, IntPtr anchor) =>
            DesktopLevelZProbe.Probe(widget, anchor, GetNext, GetPid, HostPid);

        public IntPtr ResolveAnchor(IntPtr widget) =>
            DesktopLevelZProbe.ResolveAnchor(widget, GetNext, GetPrev, GetPid, HostPid);
    }

    private static ZOrder MakeZOrder(long[] frontToBack, params (long hwnd, uint pid)[] pidEntries)
    {
        var pids = pidEntries.ToDictionary(e => e.hwnd, e => e.pid);
        // 未显式标注进程号的窗口一律视为宿主进程窗口
        foreach (var hwnd in frontToBack)
        {
            pids.TryAdd(hwnd, HostPid);
        }

        return new ZOrder(frontToBack, pids);
    }

    // ============ Probe：目标位置判定 ============

    [Fact]
    public void Probe_AtTarget_AnchorDirectlyBehind()
    {
        // front→back: [W, H]——锚窗紧邻悬浮窗之后（悬浮窗钉在宿主主窗口正前方）：目标位置
        var z = MakeZOrder([9, 1], (9, HostPid), (1, HostPid));
        Assert.Equal(DesktopZProbeResult.AtTarget, z.Probe(H(9), H(1)));
    }

    [Fact]
    public void Probe_AtTarget_PluginSiblingBetweenWidgetAndAnchor()
    {
        // front→back: [W2, W1, H]——同进程的其他悬浮窗在中间：放行，仍为目标位置
        var z = MakeZOrder([12, 11, 1]);
        Assert.Equal(DesktopZProbeResult.AtTarget, z.Probe(H(12), H(1)));
    }

    [Fact]
    public void Probe_ForeignBehind_DirectlyBehindWidget()
    {
        // front→back: [W, T, H]——第三方窗口插到悬浮窗之后（三方夹层场景）：真位移
        var z = MakeZOrder([9, 50, 1], (50, ForeignPid));
        Assert.Equal(DesktopZProbeResult.ForeignBehind, z.Probe(H(9), H(1)));
    }

    [Fact]
    public void Probe_ForeignBehind_DeepBehindHostWindow()
    {
        // front→back: [W, H2, T, H]——外来窗口位于宿主窗口之后：仍是真位移（外来必须在悬浮窗前方）
        var z = MakeZOrder([9, 2, 50, 1], (2, HostPid), (50, ForeignPid));
        Assert.Equal(DesktopZProbeResult.ForeignBehind, z.Probe(H(9), H(1)));
    }

    [Fact]
    public void Probe_AnchorNotBehind_WidgetAtAbsoluteBottom()
    {
        // front→back: [H, W]——悬浮窗落到绝对最底（被宿主桌面层窗口遮挡）：需插回锚窗前
        var z = MakeZOrder([1, 9]);
        Assert.Equal(DesktopZProbeResult.AnchorNotBehind, z.Probe(H(9), H(1)));
    }

    [Fact]
    public void Probe_AnchorNotBehind_WidgetAtBottomBehindForeign()
    {
        // front→back: [H, T, W]——悬浮窗在锚窗之后且其后有外来窗口：需插回锚窗前
        var z = MakeZOrder([1, 50, 9], (50, ForeignPid));
        Assert.Equal(DesktopZProbeResult.AnchorNotBehind, z.Probe(H(9), H(1)));
    }

    [Fact]
    public void Probe_ForeignAboveWidget_IsAtTarget()
    {
        // front→back: [T, W, H]——外来窗口在悬浮窗前方（正确语义：外来窗口遮挡悬浮窗）：目标位置
        var z = MakeZOrder([50, 9, 1], (50, ForeignPid));
        Assert.Equal(DesktopZProbeResult.AtTarget, z.Probe(H(9), H(1)));
    }

    // ============ ResolveAnchor：锚窗解析 ============

    [Fact]
    public void ResolveAnchor_DeepestHostWindowBehindWidget()
    {
        // front→back: [T, W, H]——其后最深宿主窗口即锚窗
        var z = MakeZOrder([50, 9, 1], (50, ForeignPid));
        Assert.Equal(H(1), z.ResolveAnchor(H(9)));
    }

    [Fact]
    public void ResolveAnchor_PrefersDeepestHostWindow()
    {
        // front→back: [H2, W, H1]——之后有两个宿主窗口，取最深（H1，更接近绝对最底）
        var z = MakeZOrder([2, 9, 1]);
        Assert.Equal(H(1), z.ResolveAnchor(H(9)));
    }

    [Fact]
    public void ResolveAnchor_FallbackForward_WhenWidgetAtAbsoluteBottom()
    {
        // front→back: [H, T, W]——之后无宿主窗口，向前找第一个宿主窗口
        var z = MakeZOrder([1, 50, 9], (50, ForeignPid));
        Assert.Equal(H(1), z.ResolveAnchor(H(9)));
    }

    [Fact]
    public void ResolveAnchor_ReturnsZero_WhenNoHostWindowExists()
    {
        // front→back: [T1, T2, W]——全为外来窗口：解析失败（调用方据此跳过 Z 序干预）
        var z = MakeZOrder([60, 50, 9], (60, ForeignPid), (50, ForeignPid));
        Assert.Equal(IntPtr.Zero, z.ResolveAnchor(H(9)));
    }

    [Fact]
    public void Probe_BoundedWalk_TerminatesOnCyclicChain()
    {
        // 防御：异常环链不挂死（自环 next），返回结果即可
        IntPtr Next(IntPtr h) => h;
        var result = DesktopLevelZProbe.Probe(H(9), H(1), Next, _ => HostPid, HostPid);
        Assert.True(result is DesktopZProbeResult.ForeignBehind or DesktopZProbeResult.AnchorNotBehind);
    }
}
