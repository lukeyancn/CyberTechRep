using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

// 测试程序集级 Avalonia 应用入口：HeadlessUnitTestSession 未显式指定时使用「空应用」，
// 其默认渲染后端是空实现（无字体管理器），任何文本测量/排版都会抛
// KeyNotFoundException: fonts:SystemFonts。需求 6 的可交互区域来自真实文本框坐标，
// 必须完成一次真实布局，因此这里改用 Skia 渲染后端（系统字体可用）。
[assembly: AvaloniaTestApplication(typeof(CyberTechRep.Tests.TestAppBuilder))]

namespace CyberTechRep.Tests;

/// <summary>测试程序集的 Avalonia Headless 应用构造（见程序集特性）。</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<Application>()
        // UseHeadlessDrawing=false：不接管绘制，交给 Skia（含字体管理器）
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseSkia();
}
