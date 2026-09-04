# 模块 7 交付报告：设置页集成

日期：2026-09-04 ｜ 状态：✅ 完成（含单元测试与全量回归）

## 一、交付范围

实现 `ISettingsService`（ClassIng.Shared 契约）与 ClassIsland 2.1.0.1 设置页集成（Avalonia 11，非 WPF），设置变更经 `SettingsChanged` 广播后对接各模块已存在的热生效方法。

## 二、实现清单

### 2.1 SettingsService（`src/ClassIng.Plugin/Services/Maintenance/SettingsService.cs`）

- `settings.json` **原子写入**（同目录 `.tmp` + `File.Move(overwrite)`），加载失败/Schema 不符时回退默认值；
- `SettingsChanged` 事件在 保存/导入/恢复默认 后广播；
- **导入**：强校验 `SchemaVersion`（不等于 1 抛 `FormatException`）；`AppSecretProtected`、`CloudApiKeyProtected` **保留本地值**不被覆盖；
- **导出**：**脱敏**（敏感字段置空后序列化）；
- **恢复默认**：`new AppSettings()` + 持久化 + 广播；
- `Protect/Unprotect` 委托既有 `Utils/SecretProtector`（DPAPI CurrentUser）。

### 2.2 设置页（`src/ClassIng.Plugin/Controls/SettingsPages/`，Avalonia 11 控件）

- 公共基类 `ClassIngSettingsPageBase : SettingsPageBase`（ClassIsland.Core.Abstractions.Controls，经 `ci:` xmlns 验证）：注入 `ISettingsService`，页面从可视树分离时**自动保存**，`Current` 被整体替换（导入/恢复默认）后通过 CLR INPC 通知刷新绑定；
- 注册：`services.AddSettingsPageGroup("classing.settings", "⚙", "ClassIng")` + 5 个 `AddSettingsPage<T>()`，每页 `[SettingsPageInfo]` + `[Group("classing.settings")]`；
- 密码框用 `TextBox PasswordChar="●"`，写入时经 `SettingsService.Protect` 加密持久化。

### 2.3 热生效接线（`Services/Maintenance/SettingsChangeApplier.cs`，IHostedService）

订阅 `SettingsChanged` 后调用已存在方法：

| 目标 | 方法 | 状态 |
| --- | --- | --- |
| 通知/作业关键词分类器 | `KeywordMessageClassifier.ReloadRules()` | ✅ |
| 学科关键词分类器 | `KeywordSubjectClassifier.ReloadRules()` | ✅ |
| 悬浮窗（模块 6） | `ISuspensionWindowController.ApplySettingsAsync("notice"/"homework", …)` | ✅ |
| 连接/文件等 | OptionsProvider `GetSettings` 委托改读 `settingsService.Current.*`（Plugin.cs 内 4 处 TODO 注释完成接线） | ✅ |

## 三、设置项覆盖核对表（硬性清单逐项）

### ① 连接（7/7 ✓）

| 设置项 | 状态 | 控件 |
| --- | --- | --- |
| AppId | ✓ | TextBox |
| AppSecret（密码框，脱敏显示） | ✓ | TextBox PasswordChar + DPAPI |
| ApiBase | ✓ | TextBox |
| TokenApiUrl | ✓ | TextBox |
| 群白名单（增删） | ✓ | 多行 TextBox（一行一个，增删即编辑行） |
| 重连参数（初始延迟/倍增系数/上限） | ✓ | 3 × NumericUpDown（Expander） |
| 历史回溯天数 | ✓ | 只读斜体提示「官方平台不支持历史补拉」 |

### ② 分类（11/11 ✓）

| 设置项 | 状态 | 控件 |
| --- | --- | --- |
| 通知关键词表增删改 | ✓ | 多行 TextBox（保存后 ReloadRules 热生效） |
| 作业关键词表增删改 | ✓ | 多行 TextBox |
| 学科词表 subjects.json 路径 | ✓ | TextBox |
| 打开学科词表编辑按钮 | ✓ | Button（`Process.Start` 打开文件，缺失时先落 `[]` 模板） |
| AI 开关 | ✓ | ToggleSwitch |
| 本地模型路径 | ✓ | TextBox |
| 云端端点 | ✓ | TextBox |
| 云端 Key（密码框） | ✓ | TextBox PasswordChar + DPAPI |
| 云端每日限额 | ✓ | NumericUpDown |
| 置信度阈值 | ✓ | Slider 0~1（附当前值显示） |
| 人工队列开关 | ✓ | ToggleSwitch |

### ③ 悬浮窗（17/17 ✓）

| 设置项 | 状态 |
| --- | --- |
| 通知窗 X/Y/宽/高/透明度/字号/置顶/可见 | ✓（8 项：NumericUpDown×5 + Slider + ToggleSwitch×2） |
| 作业窗 同上 8 项 | ✓ |
| 一键复位（通知/作业） | ✓（恢复默认设置值 + 调用模块 6 `ResetPositionAsync`） |
| 开机随宿主 `LaunchWithHost` | ✓ ToggleSwitch |

### ④ 文件（9/9 ✓）

| 设置项 | 状态 |
| --- | --- |
| 下载根目录 | ✓ |
| 按学科建目录 | ✓ ToggleSwitch |
| MD5 去重 | ✓ ToggleSwitch |
| 磁盘上限（MB） | ✓ NumericUpDown |
| 清理策略（停止接收/清理最旧） | ✓ ComboBox（0/1） |
| 并发下载数 | ✓ NumericUpDown |
| 限速 KB/s | ✓ NumericUpDown |
| 单文件上限 MB | ✓ NumericUpDown |
| 文件名最大长度 | ✓ NumericUpDown |

### ⑤ 维护（5/5 ✓）

| 设置项 | 状态 |
| --- | --- |
| 日志级别（Trace~Error） | ✓ ComboBox |
| 失败重试队列查看 | ✓ 排错面板 ListBox（联动模块 8 `IDiagnosticsService`） |
| 重放入口 | ✓ 选中条目 → 重放按钮 |
| 配置导入导出 | ✓ 剪贴板通道（导出脱敏 / 导入保留本地 Secret；设置窗口内免文件选择器） |
| 恢复默认 | ✓ Button |

**合计：49/49 项全覆盖。**

## 四、改动文件

新增：

- `src/ClassIng.Plugin/Services/Maintenance/SettingsService.cs`
- `src/ClassIng.Plugin/Services/Maintenance/SettingsChangeApplier.cs`
- `src/ClassIng.Plugin/Controls/SettingsPages/ClassIngSettingsPageBase.cs`
- `src/ClassIng.Plugin/Controls/SettingsPages/ConnectionSettingsPage.axaml(.cs)`
- `src/ClassIng.Plugin/Controls/SettingsPages/ClassificationSettingsPage.axaml(.cs)`
- `src/ClassIng.Plugin/Controls/SettingsPages/OverlaySettingsPage.axaml(.cs)`
- `src/ClassIng.Plugin/Controls/SettingsPages/FileSettingsPage.axaml(.cs)`
- `src/ClassIng.Plugin/Controls/SettingsPages/MaintenanceSettingsPage.axaml(.cs)`
- `src/ClassIng.Plugin/PluginRuntime.cs`（插件数据目录静态载体，供设置页无参场景读取）
- `tests/ClassIng.Tests/SettingsServiceTests.cs`（11 个用例）

修改（最小化、遵守并行协作纪律）：

- `src/ClassIng.Plugin/Plugin.cs`：在 `// ==== 模块 DI 注册区 ====` 标记后追加模块 7 注册块；将 4 处 `GetSettings = () => new XxxSettings()` 的占位 lambda（模块 1/2/3/4 各自注释标明的「模块 5/7 接入 ISettingsService 后替换」）接线为 `settingsService.Current.*`；
- `src/ClassIng.Plugin/ClassIng.Plugin.csproj`：追加 `Avalonia 11.3.17`（`ExcludeAssets="runtime; native"`，仅编译期；与宿主同版本）；追加 `AvaloniaUseCompiledBindingsByDefault=false`（模块 4-6 已有 axaml 以反射绑定编译，保持其运行时语义）；
- `tests/ClassIng.Tests/ClassIng.Tests.csproj`：追加 `Avalonia 11.3.17`（模块 6 测试与设置页类型运行期加载需要）。

## 五、测试结果

`SettingsServiceTests` 11 个用例全绿：保存/重载往返、原子写不留临时文件、`SettingsChanged` 触发、导出不含明文与密文 Secret、导入保留本地 Secret、`SchemaVersion` 校验拒绝、非法 JSON 拒绝、恢复默认并持久化、损坏文件回退默认、DPAPI 往返、导入后广播合并实例。

回归：全解决方案 `dotnet test` **122/122 通过**（含模块 1/2/3 的 36 个既有测试与模块 4/5/6 测试，0 失败）。

## 六、已知说明

- 导入导出走**剪贴板**（ClassIsland 设置窗口内最简可靠通道），文件级选择器可在模块 9 打包后按需增强；
- `ShowTickBar` 为 WPF 属性，Avalonia Slider 不支持，已用 `TickFrequency` 替代；
- 一次偶发观察：模块 6 的 `SuspensionWindowControllerTests` 在并行测试下出现过 2 例非稳定失败（与本模块代码无关），随后多次复跑全绿，已在模块 8 报告的回归记录中注明。
