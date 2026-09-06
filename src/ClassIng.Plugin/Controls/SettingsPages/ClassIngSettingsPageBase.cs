using System.ComponentModel;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using ClassIsland.Core.Abstractions.Controls;

namespace ClassIng.Plugin.Controls.SettingsPages;

/// <summary>
/// ClassIng 设置页公共基类：注入 <see cref="ISettingsService"/>，以
/// <c>Settings.*</c> 绑定路径暴露当前配置；页面关闭（从可视树分离）时自动保存
/// 并触发 <see cref="ISettingsService.SettingsChanged"/> 热生效；设置被导入/恢复默认
/// （Current 对象整体替换）后通过 CLR 属性通知刷新绑定源。
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class ClassIngSettingsPageBase : SettingsPageBase, INotifyPropertyChanged
{
    /// <summary>设置服务（由 DI 注入）。</summary>
    protected ISettingsService SettingsService { get; }

    /// <summary>插件数据目录（subjects.json 等外置文件根）。</summary>
    protected string DataDirectory { get; }

    private readonly bool _autoSaveOnDetach = true;
    private PropertyChangedEventHandler? _clrPropertyChanged;

    protected ClassIngSettingsPageBase(ISettingsService settingsService, string dataDirectory)
    {
        // ClassIsland 约定：设置页必须自持 DataContext，否则绑定解析到设置窗口的 VM，输入全部丢失
        DataContext = this;
        SettingsService = settingsService;
        DataDirectory = dataDirectory;
        SettingsService.SettingsChanged += OnSettingsChangedExternal;
        DetachedFromVisualTree += OnDetachedAutoSave;
    }

    /// <summary>当前配置（绑定根路径）。</summary>
    public AppSettings Settings => SettingsService.Current;

    /// <summary>
    /// CLR 属性变更通知：Avalonia 反射绑定订阅 INotifyPropertyChanged；
    /// AvaloniaObject 自带的 PropertyChanged 事件（AvaloniaPropertyChangedEventArgs）
    /// 无法在派生类引发，故在此重实现 INPC 接口映射。
    /// </summary>
    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => _clrPropertyChanged += value;
        remove => _clrPropertyChanged -= value;
    }

    /// <summary>引发 CLR 属性变更通知（派生页通知只读聚合属性用）。</summary>
    protected void RaisePropertyChanged(string name)
        => _clrPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>立即保存并广播热生效（显式「保存并应用」按钮，sender 为按钮以展示成功/失败反馈）。</summary>
    protected void SaveNow(object? sender) => _ = SaveCoreAsync(sender as Button);

    /// <summary>立即保存并广播热生效（页面关闭自动保存等无按钮场景，无 UI 反馈）。</summary>
    protected void SaveNow() => _ = SaveCoreAsync(null);

    /// <summary>保存核心流程：默认写 settings.json 并广播；展示成功对勾/失败提示反馈。
    /// 需要前置校验或落盘其他文件的页面（如词表编辑页）可重写并在此前后插入逻辑。</summary>
    protected virtual async Task SaveCoreAsync(Button? saveButton)
    {
        try
        {
            await SettingsService.SaveAsync().ConfigureAwait(true);
            SaveButtonFeedback.ShowSuccess(saveButton);
        }
        catch (Exception ex)
        {
            // 保存失败：给用户明确失败反馈，不假装成功；服务内部已记日志
            SaveButtonFeedback.ShowFailure(saveButton, ex.Message);
            System.Diagnostics.Debug.WriteLine($"保存设置失败：{ex.Message}");
        }
    }

    private void OnDetachedAutoSave(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_autoSaveOnDetach)
        {
            SaveNow();
        }
    }

    private void OnSettingsChangedExternal(object? sender, AppSettings e)
    {
        // 导入/恢复默认后 Current 被整体替换：通知绑定重新求值
        RaisePropertyChanged(nameof(Settings));
    }

    /// <summary>多行文本 → 行列表（去除空行与首尾空白）。</summary>
    protected static IReadOnlyList<string> TextToLines(string? text)
        => (text ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .ToList();

    /// <summary>列表 → 多行文本（关键词表/白名单编辑框显示用）。</summary>
    protected static string LinesToText(IReadOnlyList<string>? lines)
        => lines is null ? "" : string.Join(Environment.NewLine, lines);
}
