# NapCat 有头模式（显示 QQ 界面）交付报告

> 状态：**完成**（设置可选形态 → 按形态适配运行判定/停止/日志 → 补回归测试 → 全量测试 3 轮全绿 → Release 构建与打包通过）
> 日期：2026-09-17
> 基线：`cb607be`（2.0.0.0-beta.6）+ 工作区未提交的「NapCat 启动与登录修复」
> 验证：`dotnet test` **706 通过 / 0 失败 / 0 跳过**（连续 3 轮）；`dotnet build -c Release` **0 错误**（CIPX + checksums 生成成功）。

---

## 1. 需求

用户提问：能否为 NapCat 启用「有头模式」——像普通桌面版 QQ 一样弹框并显示完整操作界面。
随后确认要做：**设置里可选「有头 / 无头」**，并按调查报告里列出的 4 点（运行判定、停止语义、日志来源、快速登录参数）适配。

## 2. 事实依据（为什么必须「换包 + 适配」，而不是加个窗口开关）

| 结论 | 依据 |
| --- | --- |
| 「有头 / 无头」由 NapCat 的**发行包形态**决定 | NapCat 官方 Boot 文档：`NapCat.Shell.Windows.OneKey.zip` 标注「**无头**绿色版本」；`NapCat.Shell.Windows.Framework.zip` 标注「**有头**绿色版本」（入口 `NapCatWinBootMain.exe`）；另有 `NapCat.Framework.Windows.Once.zip`（内置 LiteLoader 一键）与手动 LiteLoaderQQNT + `NapCat.Framework.zip` 方案 |
| 现有 Shell 包无法显示 QQ 界面 | 本机 `NapCat.Shell.v4.18.19\napcat\` 的 `launcher.bat` / `launcher-user.bat` / `launcher-win10-user.bat` 只做「找 QQ.exe → 带 hook dll 交给 `NapCatWinBootMain.exe`」，无任何窗口参数；`qqnt.json` 为 `"isPureShell": true` |
| 风险提示 | 官方 Framework 页顶部写明：自 QQ 9.9.19 后 LiteLoaderQQNT 维护欠缺、鼓励迁移到 Shell；本机 QQ 为 `9.9.22-40990`（`qqnt.json`），属于该范围之后 |

## 3. 实现

### 3.1 设置项与设置页

- 新增 `NapCatRunMode { Headless = 0（默认）, Framework = 1 }` 与 `ConnectionSettings.NapCatRunMode`（默认无头，零回归）。
- 连接设置页 → NapCat 一键启动：新增「运行形态」单选（无头（Shell 包：不显示 QQ 界面）/ 有头（Framework 包：显示完整 QQ 界面））+ 两形态的路径示例与说明；可执行文件路径的标签与水印按形态给出示例：
  - 无头：`D:\NapCat\napcat\launcher-user.bat`
  - 有头：`D:\NapCatFramework\NapCatWinBootMain.exe`（或手动 LiteLoader 形态的官方 `QQ.exe`）

### 3.2 启动参数（`BuildArguments` 重载）

- 无头形态：沿用既有规则（快速登录传**裸 QQ 号**，扫码不传）——零回归。
- 有头形态：按入口名判定是否传参（`AcceptsQuickLoginArgument`）：
  - `NapCatWinBootMain*.exe` / `launcher*.bat` → 仍传裸 QQ 号（官方一键有头版的 quick 用法即 `NapCatWinBootMain.exe 10001`）；
  - 官方 `QQ.exe` / 其它启动器 → **不传参**，且不再因「QQ 号为空/非法」把启动判成失败，改为在日志流提示「登录请在 QQ 界面里完成」。

### 3.3 运行判定 / 看门狗 / 停止（核心适配）

- 新增 `IsRunAlive(process)`：无头看启动进程；**有头看启动进程或 QQ 进程**（`_isQqProcessRunning` 可注入，默认探测 `QQ`/`QQEX`，与既有「结束 QQ 进程」用同一组进程名）。
- 启动 2 秒窗口期检查、连接确认轮询、WebUI 探活重试、看门狗循环全部改用 `IsRunAlive`——有头形态下启动器退出不再被判为「已停止」。
- `OnProcessExited`：有头形态且 QQ 仍在运行 → 保留看门狗与日志跟随，状态继续「运行中」并写日志说明（不再置 Stopped）；无头形态走原逻辑。
- 状态详情 `RunningDetail`：有头形态下启动器已退出时显示「运行中 [QQ 进程内（有头）]（…）」，不再显示已退出启动器的 pid。
- 「停止」与宿主关停（`Dispose`）：有头形态下若 QQ 进程仍在运行，调用结束 QQ 进程委托（= 关闭 QQ 界面）并写日志——否则 NapCat 会继续在 QQ 进程里运行、反向 WS 不断，而插件已认为停止。
- 「启动」早退判定：启动器进程存活，或（有头）本次托管运行中且 QQ 进程仍在 → 跳过重复启动（避免拉起第二个 QQ 实例争用登录会话）。

### 3.4 日志来源（有头形态）

- 新增 `NapCatLogFileTailer`（新文件 `Services/MessageAccess/NapCatLogFileTailer.cs`）：按轮询增量读取日志文件并切分完整行。
  - 首次打开**对齐末尾**（只跟随本次启动后的新日志，不整篇灌入面板）；日志轮转/被清空（长度回退）自动从头；未写完的**半行留待下次补齐**；单行超长按 8 KB 断开保证内存有界；文件被占用/删除不抛异常。
- 新增 `FindNewestNapCatLogFile`（与既有 `FindWebUiConfigPath` 同套路）：在「工作目录/入口目录及其上级目录」下的 `logs` 与 `napcat\logs` 中取**最后写入时间最晚**的 `*.log`。
- 有头形态启动后开启日志跟随循环（1 秒轮询），新增行写入既有的排错面板日志缓冲（`NapCatLogStream.StdOut`），与无头形态的 stdout 泵体验一致；未发现日志文件时写一条可操作提示（一次性，不刷屏）。

## 4. 回归测试（新增 17 个用例）

`NapCatRunnerServiceTests`（有头形态组）：

| 用例 | 覆盖点 |
| --- | --- |
| `有头形态_注入启动器入口_快速登录仍传裸QQ号` | `NapCatWinBootMain.exe` 入口传裸 QQ 号 |
| `有头形态_官方QQ入口_不传快速登录参数且不因QQ号为空报错` | 官方 `QQ.exe` 入口不传参、不误报失败 |
| `无头形态_入口检查不生效_沿用既有校验` | 零回归：无头仍按登录设置校验 |
| `AcceptsQuickLoginArgument_按入口名判定`（Theory 5 例） | 入口名判定表 |
| `有头形态_启动器拉起后退出但QQ在运行_判为运行中` | `IsRunAlive` + `OnProcessExited` 有头分支、状态与日志 |
| `有头形态_停止_启动器已退出时结束QQ进程` | 停止走「结束 QQ 进程」路径、状态置未运行 |

`NapCatLogFileTailerTests`（新文件）：

| 用例 | 覆盖点 |
| --- | --- |
| `日志跟随_首次对齐末尾_之后只读新增行` | 不重复历史 + 增量行（含 `\r\n`） |
| `日志跟随_半行留待下次补齐` | 半行缓冲 |
| `日志跟随_文件被截断重建_从头重新读` | 轮转/清空 |
| `日志跟随_切换文件_从新文件末尾开始` | 文件切换 |
| `日志跟随_路径为空或文件不存在_返回空且不抛异常` | 容错 |
| `查找最新日志文件_优先较新者且覆盖两种目录布局` | 候选目录 + 取最新 |
| `查找最新日志文件_无日志目录_返回null` | 无日志目录 |

**测试安全**：`CreateRunner` 现在一律注入 QQ 进程探测假委托（默认恒为「不在运行」）与「结束 QQ 进程」假委托（返回 0 且不计数），任何用例都不会真的结束机器上运行的 QQ。

## 5. 使用方式（有头模式）

1. 下载 **有头包**（任选其一）：
   - `NapCat.Shell.Windows.Framework.zip`（官方标注「有头绿色版本」）：解压 → `NapCatInstaller.exe` 自动配置 → 进 `NapCat.XXXX.Framework` 目录；
   - `NapCat.Framework.Windows.Once.zip`（内置 LiteLoader 一键）；
   - 手动：装 LiteLoaderQQNT → 导入 `NapCat.Framework.zip` → 正常启动官方 QQ。
   - 有头包请放在**独立目录**（路径不含空格/中文），不要与现有 Shell 包的 `napcat\config`、`cache` 共用。
2. 插件设置：连接设置 → NapCat 一键启动 → 运行形态选「有头」；可执行文件路径填有头入口：
   - 一键有头版：`...\NapCat.XXXX.Framework\NapCatWinBootMain.exe`（可继续用「QQ 号快速登录」）；
   - 手动 LiteLoader 形态：填官方 `QQ.exe`（快速登录设置不适用，登录在 QQ 界面里点）。
3. 点「启动 NapCat」：会弹出自带 QQ 的完整界面（登录/聊天可见）；反向 WS 与 WebUI 配置与无头形态完全一致。
4. 「停止 NapCat」= 关闭 QQ 界面（并断开局域网连接）；宿主退出时同样会结束 QQ 进程。

## 6. 未决 / 风险

- **上游维护风险**：官方已声明 QQ 9.9.19 之后 LiteLoaderQQNT 维护欠缺、鼓励使用 Shell（无头）。有头形态在 QQ 自动更新后可能失效，需配合 `LiteLoaderQQNT-Kill-Update` 之类防更新措施；本机 QQ `9.9.22-40990` 属于该范围之后，实际使用请以现场为准。
- **QQ 实例独占**：有头形态下 QQ 界面就是 NapCat 本体，建议继续用小号；「启动前结束已在运行的 QQ 进程」开关默认开启（否则注入拿不到会话）。
- **日志跟随目录**：默认按「工作目录/入口目录及其上级的 `logs`、`napcat\logs`」探测；若你的有头包装在非常规布局，面板会提示未发现日志文件（日志仍可在 WebUI 或安装目录查看）——把设置里的「工作目录」指向 NapCat 安装目录即可命中。
- **真机联调**：本次为单元/进程级验证（真实短命进程模拟启动器 + 假 QQ 探测）；有头包在真机上的登录流程与界面行为未在本次范围内实测。
