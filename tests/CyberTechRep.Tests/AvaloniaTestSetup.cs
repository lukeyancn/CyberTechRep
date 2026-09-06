using Avalonia.Headless;

namespace CyberTechRep.Tests;

/// <summary>
/// 测试程序集级 Avalonia Headless 会话：为需要构造 <see cref="Avalonia.Controls.Window"/>
/// 实例的单测提供 UI 线程（悬浮窗门控等 AvaloniaObject 状态测试）。
/// 经 <see cref="HeadlessUnitTestSession.Dispatch(System.Action, System.Threading.CancellationToken)"/>
/// 把测试体调度到会话的 UI 线程执行（xunit 并行下测试线程不固定，直接构造控件会触发
/// Avalonia 的「Call from invalid thread」校验）。
/// </summary>
internal static class AvaloniaTestSetup
{
    public static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaTestSetup).Assembly);
}
