# ClassIsland 插件体系可行性调研：QQ 群消息接管与课堂信息分发系统

> 调研日期：2026-09-03
> 分析对象：https://github.com/ClassIsland/ClassIsland master 分支快照（约 2026-09，因并行任务以 zip 方式获取源码，工作副本无 `.git` 元数据，代码内容与 master 当前状态一致）
> 辅助仓库：ClassIsland/ExamplePlugins（1.x 时代 WPF 示例，已过时）、ClassIsland/PluginTemplate2（2.x 插件模板）
> 结论先行：**可行**。但有一个关键认知修正——**ClassIsland 2.x 已经不是 WPF，而是 Avalonia UI**。

---

## 1. 结论速览

| 项目 | 结论 |
| --- | --- |
| 技术栈 | .NET 8（`net8.0`）+ **Avalonia UI 11.3**（FluentAvalonia 主题），**不是 WPF** |
| 当前版本线 | 稳定版 **2.1.0.1**（Avalonia）；master 为 **2.2 "Misha" 开发者预览**（官方明确警告勿用于生产） |
| 插件 API 版本 | v2（`apiVersion >= 2.0.0.0`），SDK NuGet 包 `ClassIsland.PluginSdk`（2.0.0.*） |
| DI 容器 | `Microsoft.Extensions.Hosting`（Generic Host）+ `Microsoft.Extensions.DependencyInjection` |
| License | **GPL-3.0**（整个仓库，含 ClassIsland.Core / PluginSdk 源码） |
| 配置持久化 | 应用级 `Settings.json`（自动保存）+ 插件私有 `PluginConfigFolder`（JSON 手动读写）+ 档案系统 `Profiles/*.json` |
| 单文件发布 | 官方构建**不使用** `PublishSingleFile`，产物为 **self-contained 文件夹** zip/deb/pkg |
| 扩展点总体评估 | 设置页分组 / 配置持久化 / 自建悬浮窗 / 后台 WebSocket 服务 / 通知服务复用：**全部可行**；托盘菜单注入：**无官方 API（受阻，可绕过）** |

---

## 2. 版本、目标框架与 License 结论

### 2.1 版本与目标框架

- `global.json`：SDK `9.0.100`（rollForward latestFeature），各项目 `TargetFramework` 均为 **`net8.0`**。
- `ClassIsland/ClassIsland.csproj`：`UseWPF=false`，`OutputType=Library`；入口 exe 为 `ClassIsland.Desktop/ClassIsland.Desktop.csproj`（`OutputType=Exe`）。
- UI 框架为 **Avalonia 11.3.17**（`AvaloniaShared.props` 中 `AvaloniaVersion`）+ FluentAvalonia 2.4.1。所有视图为 `.axaml`。
- 官方开发文档（docs.classisland.tech/dev）确认：.NET 8 + Avalonia + Microsoft.Extensions.Hosting。
- 版本线（来自 GitHub Releases）：
  - 稳定版：**2.1.0.1**；
  - 2.1.1.0 / 2.1.1.1 是 **2.2 预览版**（官方注记"仅适用于开发者进行插件移植和技术性预览，不要在生产环境使用"）；
  - master 即 2.2 开发线。2.1.1.x 曾发生档案模型破坏性变更，说明**预览线 API 不稳定，插件应面向稳定版 2.1.x 编译**。

### 2.2 License（GPL-3.0）合规要求

根目录 `LICENSE.txt` 为完整 **GNU GPL v3**（`ClassIsland.PluginSdk/LICENSE.txt` 同样为 GPL-3.0）。对本项目的影响：

1. **分发"魔改版成品"（fork + 内嵌 QQ 插件的完整安装包）**：属于 GPL 第 5/6 条的 conveying covered work，必须：
   - 以 GPL-3.0 整体授权整个作品，随附完整**对应源代码**（或三年有效的书面提供承诺）；
   - 保留并显著标注修改声明（修改的文件与日期）；
   - 附带 GPL-3.0 许可证文本与无保修声明；
   - **不能以闭源/商业授权形式分发该成品**（除非获得版权方另行授权）。
2. **只分发独立插件（.cipx 包 / 插件市场安装）**：插件通过官方 SDK 公开 API 与宿主交互、运行期动态加载，一般认为接近"独立作品通过接口交互"（GPL 的 aggregate 边界）。但稳妥且社区惯例的做法是：
   - 插件**以 GPL-3.0 兼容许可开源**（ClassIsland 官方插件市场生态默认开源）；
   - 插件依赖的 `ClassIsland.PluginSdk` NuGet 包本身源自 GPL-3.0 的 ClassIsland.Core，闭源分发插件存在合规争议，**不建议闭源**。
3. 结论：若我们的目标是"在 ClassIsland 基础上做扩展并分发"，**选插件形态 + 开源（GPL-3.0 或兼容）是最合规、摩擦最小的路径**；若必须闭源，则不要分发 ClassIsland 本体或其衍生构建，仅限内部/自用不触发 GPL 传导义务。

---

## 3. 插件 API 速查表

### 3.1 插件入口与生命周期

| API | 位置 | 说明 |
| --- | --- | --- |
| `PluginBase`（抽象类） | `ClassIsland.Core/Abstractions/PluginBase.cs` | 唯一入口基类。提供 `PluginConfigFolder`（插件私有配置目录）、`Info`（PluginInfo）。需实现 `Initialize(HostBuilderContext context, IServiceCollection services)` |
| `[PluginEntrance]` 特性 | `ClassIsland.Core/Attributes/` | 标记入口类；PluginService 按"基类为 PluginBase 或带此特性"查找入口 |
| `manifest.yml` | 插件根目录 | 清单文件，YAML + camelCase |
| `PluginService` | `ClassIsland/Services/PluginService.cs` | 扫描 `Plugins/` 目录 → 解析清单 → `PluginLoadContext`（独立 ALC，`ClassIsland/PluginLoadContext.cs`）加载 → 拓扑排序依赖 → 实例化并调用 `Initialize` → 注册为 DI 单例 |

清单格式（仓库内 `ClassIsland.ExamplePlugin/manifest.yml`）：

```yaml
id: classisland.example
name: 示例插件
description: 插件描述
entranceAssembly: "ClassIsland.ExamplePlugin.dll"
url: https://github.com/ClassIsland/ClassIsland
apiVersion: 2.0.0.0     # 必须 >= 2.0.0.0，否则拒绝加载
version: 2.0.0.0
author: ClassIsland
```

入口写法（`ClassIsland.ExamplePlugin/Plugin.cs`）：

```csharp
[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSettingsPage<HelloSettingsPage>();
    }
}
```

> 插件目录：`AppRoot/Plugins/<插件id>/`；禁用/卸载通过目录内 `.disabled` / `.uninstall` 标记文件实现；插件包为 zip 格式的 `.cipx`。开发期可用 `-epp <目录>` 参数加载外部插件目录（见示例的 `Properties/launchSettings.json`）。

### 3.2 注册机制（IServiceCollection 扩展，均在 `ClassIsland.Core/Extensions/Registry/`）

| 注册方式 | 文件 | 作用 |
| --- | --- | --- |
| `services.AddSettingsPage<T>()` | `SettingsWindowRegistryExtensions.cs` | 向设置窗口注册页面，T 必须 `[SettingsPageInfo("id", "标题")]` 且继承 `SettingsPageBase`；`[Group("groupId")]` 归组 |
| `services.AddSettingsPageGroup(id, icon, name)` | 同上 | **新增设置页分组**（导航侧栏分组） |
| `services.AddComponent<T>()` / `AddComponent<T, TSettings>()` | `ComponentRegistryExtensions.cs` | 向主界面课表行注册**组件**（可被用户在编辑模式中摆放） |
| `services.AddNotificationProvider<T>()` | `NotificationProviderRegistryExtensions.cs` | 注册**提醒提供方**（T 继承 `NotificationProviderBase`，需 `[NotificationProviderInfo]`；内部自动 `AddHostedService<T>()`） |
| `services.AddXamlTheme(...)` / `AddProfileTransferProvider(...)` / `AddSpeechProvider(...)` 等 | `App.Services.xaml.cs` 有官方用例 | 主题、档案导入导出、语音源等扩展点 |

设置页写法（`ClassIsland.ExamplePlugin/Views/SettingsPages/HelloSettingsPage.axaml.cs`）：

```csharp
[SettingsPageInfo("classisland.example-plugin.hello", "Hello world!")]
public partial class HelloSettingsPage : SettingsPageBase
{
    public HelloSettingsPage() => InitializeComponent();
}
```

### 3.3 服务获取与关键内置服务

| API / 服务 | 位置 | 说明 |
| --- | --- | --- |
| `IAppHost.GetService<T>()` / `TryGetService<T>()` | `ClassIsland.Shared/IAppHost.cs` | 静态服务定位器（`IAppHost.Host` 即 Generic Host），插件运行期取服务的主要方式 |
| `INotificationHostService` | `ClassIsland.Core/Abstractions/Services/INotificationHostService.cs`，实现 `ClassIsland/Services/NotificationHostService.cs` | 提醒主机：注册/拉取/显示提醒，管理提醒提供方设置（`GetNotificationProviderSettings<T>`） |
| `NotificationProviderBase` / `NotificationProviderBase<TSettings>` | `ClassIsland.Core/Abstractions/Services/NotificationProviders/NotificationProviderBase.cs` | 提醒提供方基类，**自带 `IHostedService`**，有 `ShowNotification(NotificationRequest)` / `ShowChainedNotifications`，支持提醒渠道 `[NotificationChannelInfo]` |
| `INotificationProvider`（旧接口） | `ClassIsland.Shared/Interfaces/INotificationProvider.cs` | 2.x 仍保留；新代码请用 `NotificationProviderBase` |
| `IProfileService` / `ProfileService` | `ClassIsland/Services/ProfileService.cs` | 档案（课表）系统，`Profiles/*.json`，含课程/时间表模型（`ILessonsService` 提供上课/下课等事件） |
| `SettingsService` | `ClassIsland/Services/SettingsService.cs` | 应用设置：`Settings.json`，属性变更即自动 `SaveSettings`（ConfigureFileHelper JSON 读写） |
| `ClassIsland.Shared.IPC` | `ClassIsland.Shared.IPC/`（`IpcClient`、`IPublicLessonsService`、`IPublicProfileService` 等） | **跨进程 IPC**：外部进程可读取当前课表/上课科目等，也支持向应用路由通知（`IpcRoutedNotifyIds`）——对"外部 QQ 机器人进程与 ClassIsland 联动"是现成通道 |
| `ConfigureFileHelper.LoadConfig<T>/SaveConfig<T>` | `ClassIsland.Shared/Helpers/` | JSON 配置读写工具（官方插件示例用它实现插件配置持久化） |
| 本地化 | `ClassIsland/Assets/Localization/**/*.resx`（Designer 生成类） | 标准 .NET resx 本地化（zh-Hans/zh-Hant/en），无独立 Localize 服务 API |

### 3.4 插件配置持久化的官方模式（`ExamplePlugins/PluginWithSettingsPage/Plugin.cs`）

```csharp
public override void Initialize(HostBuilderContext context, IServiceCollection services)
{
    Settings = ConfigureFileHelper.LoadConfig<Settings>(Path.Combine(PluginConfigFolder, "Settings.json"));
    Settings.PropertyChanged += (sender, args) =>
        ConfigureFileHelper.SaveConfig<Settings>(Path.Combine(PluginConfigFolder, "Settings.json"), Settings);
    services.AddSettingsPage<ExampleSettingsPage>();
}
```

### 3.5 开发/打包工具链

- 插件项目引用 NuGet `ClassIsland.PluginSdk 2.0.0.*`（`ExcludeAssets=runtime`，避免把宿主程序集打进包里），`TargetFramework=net8.0`，无需 `EnableDynamicLoading` 之外的特殊配置（主仓内 ExamplePlugin 带 `EnableDynamicLoading=true` 与 `CreateCipx=true`）。
- `ClassIsland.PluginSdk/targets` 在 Build 后把输出目录打成 `<插件名>.cipx`（zip）并生成 MD5 摘要，可直接拖入应用设置安装或发布到插件市场（市场索引由 `ClassIsland/PluginIndex` 维护）。
- 官方插件模板：`ClassIsland/PluginTemplate2`（`dotnet new` 模板 + CI 发布 workflow）。
- 注意：`ClassIsland/ExamplePlugins` 仓库是 **1.x WPF API** 的旧示例（`System.Windows.*`、MaterialDesignThemes.Wpf），**不要**照抄；以主仓 `ClassIsland.ExamplePlugin`（Avalonia v2 API）和 PluginTemplate2 为准。

---

## 4. 扩展点可行性逐项评估

| 扩展点 | 结论 | 依据 |
| --- | --- | --- |
| 注册设置页分组 | **可行** | `AddSettingsPageGroup(id, icon, name)` + `AddSettingsPage<T>()` + `[Group]`，注册表驱动，插件与内置页走完全相同的机制（`SettingsWindowRegistryService.Groups`/`Registered`） |
| 持久化自定义配置 | **可行** | 插件拿到 `PluginConfigFolder` 专属目录；官方示例即"ObservableObject + PropertyChanged 自动 SaveConfig(JSON)"；也可直接用 `Microsoft.Extensions.Options`/自行 JSON，无限制 |
| 常驻置顶无边框悬浮窗 | **可行** | UI 为 Avalonia：插件可自建 `Window`，`SystemDecorations=None` + `Topmost=true` 即无边框置顶；宿主 `MainWindow` 本身就是无边框置顶窗口，且内置 `WindowPlatformService.SetWindowFeature(WindowFeatures.Topmost, ...)`、`AcquireTopmostLock` 等基础设施可参考（`ClassIsland/MainWindow.axaml.cs`）。注意：没有"现成可复用的悬浮窗服务 API"，窗口管理需插件自理；Avalonia 的 Topmost 行为与 WPF 不同（如全屏检测逻辑可参考宿主的 `TopmostEffectWindow` 实现） |
| 后台服务（WebSocket 客户端长连接） | **可行** | `Initialize` 中拿到 `IServiceCollection`，可 `services.AddHostedService<T>()` 注册任意 `IHostedService`/`BackgroundService`，随 Generic Host 生命周期启停；`NotificationProviderBase` 本身就是 IHostedService 的先例。也可在插件内自管 `System.Net.WebSockets.ClientWebSocket`/ClientWebSocket 库 |
| 通知服务复用 | **可行** | 两种路径：(a) 插件实现 `NotificationProviderBase`，用 `ShowNotification(new NotificationRequest{ OverlayContent=..., MaskSpeechContent=... })` 复用主界面提醒遮罩、语音播报、提醒渠道体系；(b) 只做发送方——通过 `IAppHost.GetService<INotificationHostService>()` 直接调 `ShowNotification`。QQ 消息触发的课堂提醒可直接落到这套系统 |
| 向主界面注入 UI | **可行** | `AddComponent<T>()` 注册主界面组件（用户可在编辑模式摆放）；提醒内容通过 `NotificationRequest.OverlayContent/MaskContent` 注入任意 Avalonia 控件 |
| 托盘菜单注入 | **受阻（可绕过）** | 无公开注册 API。托盘由 `TaskBarIconService` 管理（`MoreOptionsMenuItems` 属性可读，理论上可反射/Harmony 注入，但属内部实现，跨版本易碎）。建议：QQ 相关入口放在自己的设置页/悬浮窗内，不依赖托盘 |

**总体结论：目标系统（QQ 群消息接管 + 课堂信息分发）所需的全部关键能力均有官方插件 API 支撑，可行。** 建议形态：单个插件 = WebSocket 后台服务（IHostedService）+ 通知提供方（复用提醒 UI/语音）+ 设置页分组（连接配置、群绑定）+ 可选自建悬浮窗；QQ 机器人若为独立进程，可另走 `ClassIsland.Shared.IPC` 与插件联动。

---

## 5. 自包含单文件发布结论

- 官方构建（Nuke，`build/Build.App.cs`）：`dotnet publish` 时仅设置 `SelfContained=true/false` + RID，产物为**文件夹**（Windows zip、Linux deb、macOS pkg）。全仓库 **没有任何 `PublishSingleFile` / `EnableCompressionInSingleFile` 配置**；Releases 的产物名也全部是 `*_selfContained_folder.zip`。
- 本体是 Avalonia 而非 WPF，因此"WPF 单文件发布的历史限制"不再适用；但仍然**不建议单文件**，理由：
  1. **插件加载模型**：`PluginService`/`PluginLoadContext` 从磁盘目录加载插件程序集并解析依赖，宿主若打成单文件 bundle，宿主程序集解析路径会复杂化（需额外处理 ALC 回退），收益为零；
  2. **Avalonia 单文件**需要 `IncludeNativeLibraries/self-extracted` 等额外配置且启动解包有副作用，官方自己都不用；
  3. **Trimming 不可用**：Avalonia（XAML 反射、Binding）不支持 IL 裁剪，且应用与插件大量运行时反射（插件发现、组件注册），裁剪必然裁掉插件所需元数据；
  4. 单文件显著拖慢冷启动（自解压）。
- **建议**：与官方保持一致——`dotnet publish -c Release -r win-x64 --self-contained true`（文件夹形态，zip 分发）。若希望缩小体积，可考虑框架依赖部署（要求用户装 .NET 8 Desktop Runtime）或官方的资产拆分（2.1 已把资产文件拆出本体）。**不要**使用 `PublishSingleFile` 与 `PublishTrimmed`。

---

## 6. 风险与建议

1. **面向稳定版 2.1.0.1 开发**（SDK 2.0.x/2.1.x），不要面向 master（2.2 预览）——预览线已出现档案模型等破坏性变更，且官方明示勿用于生产。跟踪 2.2 正式发布后再迁移。
2. **API 文档**：docs.classisland.tech/dev（开发文档）+ api.docs.classisland.tech（API 参考）+ 官方示例（主仓 `ClassIsland.ExamplePlugin`、模板 `PluginTemplate2`）。
3. **合规**：插件以 GPL-3.0 兼容许可开源；不分发闭源魔改版 ClassIsland。
4. **UI 迁移成本**：若团队此前按 WPF 规划了 UI 代码（XAML/控件/MaterialDesign），需按 Avalonia 重写——这是本次调研最大的范围修正点。
5. 本机调试：插件项目 `launchSettings.json` 配置 `commandName: Executable`，`commandLineArgs: -epp $(TargetDir)`，直接 F5 用宿主 exe 加载插件目录。
