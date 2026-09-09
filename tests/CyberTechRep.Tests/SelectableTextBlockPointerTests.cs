using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Simple;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 回归：<see cref="SelectableTextBlock"/> 会吞掉 <c>PointerPressed</c>（自己标记为已处理并捕获指针），
/// 但 <c>Tapped</c> 照常冒泡。作业文档「点击进入编辑态」必须挂在 Tapped 上，这条测试锁住该前提，
/// 避免有人改回 PointerPressed 后编辑功能再次静默失效。
/// </summary>
public sealed class SelectableTextBlockPointerTests
{
    private static void EnsureControlTheme()
    {
        if (Application.Current is { } app && !app.Styles.OfType<SimpleTheme>().Any())
        {
            app.Styles.Add(new SimpleTheme());
        }
    }

    [Fact]
    public Task PointerPressed_IsHandled_But_Tapped_Bubbles()
    {
        return AvaloniaTestSetup.Session.Dispatch(() =>
        {
            EnsureControlTheme();

            var block = new SelectableTextBlock
            {
                Text = "练习册 P12 第 1-5 题",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MinHeight = 48
            };
            var window = new Window { Content = block, Width = 360, Height = 300 };
            window.Show();
            window.UpdateLayout();

            var pointerPressedDefault = 0;
            var pointerPressedHandledToo = 0;
            var tappedDefault = 0;
            var tappedHandledToo = 0;
            block.AddHandler(InputElement.PointerPressedEvent, (_, _) => pointerPressedDefault++, handledEventsToo: false);
            block.AddHandler(InputElement.PointerPressedEvent, (_, _) => pointerPressedHandledToo++, handledEventsToo: true);
            block.AddHandler(InputElement.TappedEvent, (_, _) => tappedDefault++, handledEventsToo: false);
            block.AddHandler(InputElement.TappedEvent, (_, _) => tappedHandledToo++, handledEventsToo: true);

            // 注意：SelectableTextBlock 只在文字字形上命中，方块内空白处点不到，这里点文字区域
            var origin = block.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(origin);
            window.MouseDown(new Point(origin!.Value.X + 20, origin.Value.Y + 10), MouseButton.Left);
            window.MouseUp(new Point(origin.Value.X + 20, origin.Value.Y + 10), MouseButton.Left);

            Assert.Equal(0, pointerPressedDefault);      // 默认处理器收不到：事件已被 SelectableTextBlock 标记处理
            Assert.Equal(1, pointerPressedHandledToo);   // 只有 handledEventsToo 能收到
            Assert.Equal(1, tappedDefault);              // Tapped 未被吞，XAML 事件属性挂它才有效
            Assert.Equal(1, tappedHandledToo);
            Assert.True(block.IsFocused, "点击后 SelectableTextBlock 应获得焦点（选中/复制需要）");
        }, CancellationToken.None);
    }
}
