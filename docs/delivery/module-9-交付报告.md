# 模块 9 交付报告：首次启动引导 + GitHub Actions 打包发布

日期：2026-09-04 ｜ 状态：✅ 完成（含单元测试、全量回归、.cipx 本地产出与 MD5 校验、git 初始化）

## 一、首次启动引导（First-Run Wizard）

### 1.1 首启判定依据（设计说明）

采用 **settings.json 显式标志 `FirstRunCompleted`**（`AppSettings` 根级布尔字段，模块 7 的
`ISettingsService` 原子写入机制持久化），而非"是否已有连接配置"的启发式判定。理由：

- 用户**跳过引导**或只填了部分连接字段时，启发式会在下次启动重复弹窗；
- 显式标志与五组设置同文件同机制：原子写入、损坏回退默认值（回退后重新视为首次启动，
  属期望行为）、SchemaVersion 不变（纯增量字段，旧 settings.json 反序列化缺省为 false）；
- `ResetToDefaultsAsync` / 导入配置分别回到"未完成 / 已完成"状态，语义自洽。

### 1.2 `FirstRunService` / `IFirstRunService`（`Services/FirstRun/FirstRunService.cs`）

- `IsFirstRun`：读 `Current.FirstRunCompleted`；
- `MarkCompletedAsync`：置位 + `SaveAsync` 持久化并广播 `SettingsChanged`；写盘失败只记
  结构化日志（内存中已置位，本次会话不再弹窗；下次启动重新引导），**绝不外抛**；
- `ShowWizardAsync`：经 `UiInvoker`（默认 `Dispatcher.UIThread.InvokeAsync`，internal 委托，
  测试注入同步执行）在 UI 线程创建/显示引导窗；窗口已打开时改为 `Activate` 置前；窗口
  `Closed` 后清空引用可再次创建。**工厂未注入 / 工厂抛异常 / 工厂返回 null / 调度失败**
  四种情况均返回 `false` 并记日志，不崩溃；
- 窗口依赖抽象接口 `IFirstRunWizardWindow`（`FirstRunWizardWindow` 天然同签名实现），逻辑层
  不依赖 XAML 实例，可脱离 Avalonia 平台测试（沿用模块 6 控制器模式）；
- `FirstRunStartupService`（IHostedService）：宿主启动后检测首次启动，fire-and-forget 弹出
  引导；检测/调度任何异常只记日志，不阻断宿主启动与插件其余功能。

### 1.3 `FirstRunWizardWindow`（`Views/`）

Avalonia 无边框（`SystemDecorations="None"`）、置顶、圆角浅色卡片窗，风格与模块 6 悬浮窗
一致（同款标题栏拖拽 `BeginMoveDrag`）。三步流程：

1. **欢迎/说明**：插件功能简介 + 引导价值说明 + 跳过提示；
2. **连接配置**：AppID / AppSecret（`PasswordChar` 脱敏，DPAPI 加密落盘）/ API 根地址 /
   AccessToken 地址，写入 `ISettingsService.Current.Connection`；地址留空回退官方默认值；
   重新打开时**预填现有连接配置**（不丢失已有值）；
3. **完成**：`MarkCompletedAsync` 落盘后显示完成页（未填 AppID 时提示可稍后到设置页补全）。

- **跳过引导同样标记完成**（用户明确暂不配置，之后不再自动弹出）；
- 每个事件处理 try/catch + 反馈文案，保存失败停留本步并可稍后在「ClassIng 连接」设置页重试，
  任何一步失败不崩溃。

### 1.4 「重新打开引导」完整入口（模块 8 维护设置页）

`MaintenanceSettingsPage` 新增「首次启动引导」折叠区（按钮 + 说明），点击经
`IFirstRunService.ShowWizardAsync` 唤出（**不检查 IsFirstRun**，完成后仍可反复唤出），
结果写入页面反馈框；DI 构造函数追加可选参数 `IFirstRunService? firstRun = null`，未注册时
降级提示，不影响页面其余功能。

## 二、DI 注册（Plugin.cs 追加于模块 9 标记区）

`FirstRunService`（工厂注入 `FirstRunWizardWindow(settings, firstRun)`）/ `IFirstRunService`、
`AddHostedService<FirstRunStartupService>`。

## 三、GitHub Actions 打包发布

- `.github/workflows/ci.yml`（单 job `windows-latest`）：
  1. `actions/checkout@v4` + `actions/setup-dotnet@v4`（.NET 8.0.x）；
  2. `dotnet restore` → `dotnet build -c Release`（同时产出 `.cipx` 与 `checksums.md`，
     SDK targets 调用 `pwsh generate-md5.ps1`，windows-latest 自带 pwsh）；
  3. `dotnet test -c Debug`（135 既有 + 16 新增全绿才通过）；
  4. **MD5 校验步**：解析 checksums.md 与实际包 `Get-FileHash` 比对，不一致直接 fail
     （与本地验证逻辑一致）；
  5. `actions/upload-artifact@v4` 上传 `.cipx` + `checksums.md`（PR/push 均可下载产物）；
  6. 打 tag（`v*`）时 `softprops/action-gh-release@v2` 将两文件发布为 Release 附件
     （`permissions: contents: write`，`fail_on_unmatched_files: true`）。
- 触发：所有分支 push / PR / `v*` tag push。

## 四、git 初始化

- 已执行 `git init -b main`，本地用户配置 `ClassIng CI <ci@local>`；
- `.gitignore`：`bin/`、`obj/`、`cipx/`（包产物）、`TestResults/`、`.vs/`、`*.user`，以及
  内嵌参考目录 `ClassIsland/`、`ClassIsland.ExamplePlugins/`、`ClassIsland.PluginTemplate2/`
  （上游宿主源码只读参考，共 ~49MB 不入库）与本地便携 SDK `dotnet-sdk/`（~714MB）；
- 初始提交 `29b6dcf`：99 文件，15918 行；工作区干净。

**用户后续手工步骤（发布）**：

```powershell
# 1. 在 GitHub 上创建空仓库（不要勾选自动生成 README/.gitignore）
# 2. 关联远程并推送
git remote add origin https://github.com/<你的用户名>/<仓库名>.git
git push -u origin main
# 3. 打 tag 触发 Release（workflow 自动构建、测试、校验 MD5 并上传 Release 附件）
git tag v0.1.0
git push origin v0.1.0
# 4. 在仓库 Actions 页确认 CI 全绿；Releases 页下载 .cipx 与 checksums.md
```

## 五、改动文件

新增：

- `src/ClassIng.Plugin/Services/FirstRun/FirstRunService.cs`（`IFirstRunService` +
  `IFirstRunWizardWindow` + `FirstRunService` + `FirstRunStartupService`）
- `src/ClassIng.Plugin/Views/FirstRunWizardWindow.axaml` / `.axaml.cs`
- `.github/workflows/ci.yml`
- `.gitignore`
- `tests/ClassIng.Tests/FirstRunServiceTests.cs`（16 用例）

修改：

- `src/ClassIng.Shared/Models/SettingsModels.cs`（`AppSettings` 追加 `FirstRunCompleted` 字段，
  其余未动）
- `src/ClassIng.Plugin/Plugin.cs`（模块 9 标记区追加 4 行注册）
- `src/ClassIng.Plugin/Controls/SettingsPages/MaintenanceSettingsPage.axaml` / `.axaml.cs`
  （新增引导折叠区、`IFirstRunService?` 可选参数与按钮处理，其余未动）

## 六、验证结果

- **构建**：`dotnet build ClassIng.sln`（`--no-incremental` 全量重建）Release 与 Debug 均
  **0 错误**；全解决方案警告 372 条（低于既有基线约 382 条）。本次新增/改动文件
  （FirstRunService.cs / FirstRunWizardWindow.* / FirstRunServiceTests.cs / SettingsModels.cs /
  Plugin.cs / 维护设置页新增代码行）**贡献 0 警告**——新增测试文件通过
  `[SupportedOSPlatform("windows")]` 与假窗口补齐关闭事件规避了 CA1416/CS0067，维护设置页
  26 条警告全部位于既有代码行（与改动前一致）。
- **测试**：`dotnet test -c Debug` → **151/151 通过，0 失败**（135 既有 + 16 新增）：
  - 首启检测（全新/损坏 settings.json 回退后判定）2 例；
  - 完成标记持久化（跨实例、SettingsChanged 广播、跳过路径）3 例；
  - 连接配置写入 ISettingsService 并随完成标记持久化（含 DPAPI 明文回读）1 例；
  - 引导失败不崩溃（工厂未注入 / 工厂抛异常 / 工厂返回 null）3 例；
  - 窗口显示/复用/关闭生命周期 3 例；启动钩子 3 例；重新打开入口 1 例。
- **.cipx 验证**：本地 Release 构建产出 `src/ClassIng.Plugin/cipx/ClassIng.Plugin.cipx`，
  `checksums.md` 记录 `542704CBDFF5C2EFD360B71DCAB7045E`，与
  `Get-FileHash -Algorithm MD5` 实际值**一致**（与 CI 中校验步骤同一逻辑）。
- **编码**：全部新增/改动文件 UTF-8 无 BOM，逐文件校验无 U+FFFD 乱码。

## 七、遗留风险与说明

1. **导入/导出携带 FirstRunCompleted**：导出的脱敏 JSON 含该标志，导入到新机器视为"已完成
   引导"不再自动弹窗（导入即自带完整配置，语义合理；如需重新引导可在维护页手动唤出）。
2. **恢复默认后重新引导**：`ResetToDefaultsAsync` 会把标志归零，下次启动重新弹出引导，属
   预期行为，已写入代码注释。
3. **UI 运行时行为未经真机验证**：引导窗在宿主进程内的显示效果（置顶层级、DPI、与宿主
   窗口焦点交互）需要用户在 ClassIsland 中实际运行确认；逻辑层已全部单测覆盖，窗口代码
   与既有悬浮窗同模式、风险低。
4. **CI 首跑未执行**：workflow 需仓库推送到 GitHub 后才会真实运行；MD5 校验步与本地步骤
   同一逻辑已验证，pwsh 可用性由 windows-latest 镜像保证。
5. **仓库地址占位**：`manifest.yml` 的 url 与模块 8 更新检测的 `Repository` 仍为占位值，
   用户建仓后建议一并替换（属既有模块配置，本模块未改动）。
