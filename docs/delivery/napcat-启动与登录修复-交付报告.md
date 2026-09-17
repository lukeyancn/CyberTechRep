# NapCat 启动与登录修复交付报告（快速登录参数 / QQ 实例独占 / 启动结果以连接确认 / WebUI 探活）

> 状态：**完成**（逐条修复 → 补回归测试 → 全量测试全绿 → Release 构建与 CIPX 打包通过）
> 日期：2026-09-11
> 基线：`cb607be`（2.0.0.0-beta.6）
> 验证：`dotnet build src/CyberTechRep.Plugin -c Release` **0 错误**（CIPX + checksums 生成成功）；
> `dotnet test tests/CyberTechRep.Tests -c Debug` **689 通过 / 0 失败 / 0 跳过**。
> 现场：本次同时完成运行环境恢复（NapCat 已登录并接入，反向 WS 23001 已建立，启动核对补齐 26 条消息）。

---

## 0. 现象（现场时间线，来自 `data/Logs` 与文件时间戳）

| 时间 | 事实 |
| --- | --- |
| 21:48:18 | QQ 由**插件之外**启动（普通实例，`NapCatWinBootHook.dll` 未加载；插件此时尚未启动） |
| 21:50:33 | ClassIsland 启动，插件反向 WS 监听 `ws://127.0.0.1:23001` 就绪 |
| 21:50:39 | 插件自动拉起 NapCat（`launcher-user.bat`），插件日志报「运行中 [pid 13136]」「WebUI 地址已发现 http://127.0.0.1:6099/webui」 |
| 21:51:43 / 21:51:44 | 用户停止 → 再次启动 |
| 21:51:46 / 22:37:11 | NapCat 两次生成登录二维码（`napcat/cache/qrcode.png` 更新时间） |
| 22:02:32 | 维护服务报「环境告警：协议端已离线 10 分钟」 |
| 全程 | **6099 从未监听**，`23001` 上从未出现已建立的连接（即 NapCat 从未接入） |

用户观感：「NapCat 连不上」，且点「打开 WebUI」只打开死页面。

## 1. 根因

### 1.1 快速登录参数格式错误（设置项「QQ 号快速登录」实际不可用）

插件把参数组装成 `-q QQ号`（`NapCatRunnerService.BuildArguments`），而 NapCat 的启动器
`NapCatWinBootMain.exe` **只认裸 QQ 号**并自己把它转成 NTQQ 的 `-q QQ号`。

实测证据（同一次启动，两条命令对照）：

```
# 传 "-q 1223437061"
Boot Command:"C:\Program Files\Tencent\QQNT\QQ.exe" --enable-logging      ← 参数被丢弃
[NapCat] 没有 -q 指令指定快速登录，将使用二维码登录方式                    ← 退回扫码

# 传 "1223437061"（裸 QQ 号）
Boot Command:"C:\Program Files\Tencent\QQNT\QQ.exe" --enable-logging -q 1223437061
[NapCat] 正在快速登录  1223437061                                          ← 直接登录成功
```

后果：即使把登录方式配成「QQ 号快速登录」，NapCat 仍按扫码处理。而扫码依赖 NapCat WebUI，
于是被下面的 1.2 卡死，形成「永远连不上」。

### 1.2 WebUI 端口落在 Windows 保留端口段（扫码无处可扫）

NapCat 的 `napcat/config/webui.json` 端口为 `6099`，该端口落在本机 Windows 保留端口段
`6049–6148` 内（`netsh int ipv4 show excludedportrange protocol=tcp`，来自 Hyper-V/WSL 之类的动态保留）。
NapCat 绑定失败：

```
host或port不可用 Error: 遇到错误: EACCES
[NapCat] [WebUi] Current WebUi is not run.
```

后果：NapCat 起来了也没有 WebUI，二维码只落在控制台/图片文件里；而插件侧的两条日志
（「NapCat 运行状态：Running」「WebUI 地址已发现」）都是**假成功**——
前者只看启动器进程存活，后者只读磁盘上的 `webui.json`（当时那份还是 9/8 的旧文件），
因此 UI 上看起来一切正常，实际 6099 后面没有服务。

### 1.3 「进程存活」被当作「已就绪」，且 QQ 实例被争用

- `launcher-user.bat` 结尾有 `pause`，启动器进程会一直存活 → 插件 2 秒窗口判定为「运行中」。
- NapCat 以注入方式运行在 QQ 进程内，需要独占 QQ 实例；本机当时已有普通 QQ 在运行
  （插件启动前 2 分 15 秒），NapCat 的实例拿不到已登录会话，只能走二维码登录流程。

## 2. 修复

| # | 变更 | 位置 |
| --- | --- | --- |
| 1 | 快速登录参数改为**裸 QQ 号**（不再拼 `-q`），并在注释中记录对照实测结论 | `NapCatRunnerService.BuildArguments` |
| 2 | 新增设置项 `NapCatEndExistingQq`（默认**开启**）：拉起 NapCat 前结束已在运行的 `QQ`/`QQEX` 进程，结束数量写入排错面板日志流；关闭时由用户自行保证 QQ 未运行 | `SettingsModels.cs`、`NapCatRunnerService.EndQqProcesses`、连接设置页开关 |
| 3 | 启动前**已在运行检测**：反向 WS 已连接，或 `webui.json` 端口已在监听 → 记「已在运行（依据），跳过重复启动」，不再拉起第二个实例（避免两个 QQ 实例争用同一登录会话） | `NapCatRunnerService.DetectRunningNapCat` |
| 4 | 启动结果改为**以反向 WS 连接确认**：进程存活后再轮询最长 35 秒，接入成功→「运行中 [pid]（已接入）」；未接入→「运行中 [pid]（尚未接入：等待登录或反向连接）」并把排查指引写入日志流 | `NapCatRunnerService.ConfirmConnectionAsync` |
| 5 | WebUI **探活**后才算可用：地址解析成功即缓存，端口可连接才标记 `WebUiReady`；「打开 WebUI」打开前再探一次，未监听则不打开死页面，改为写入原因（含 `excludedportrange` 排查命令）；看门狗在未就绪时重试发现，覆盖 WebUI 起得慢的情况 | `NapCatRunnerService.DiscoverWebUiUrlAsync / OpenWebUi / ProbePort` |
| 6 | 设置页文案同步：快速登录说明（需该账号已在本机登录过）、结束 QQ 进程开关、WebUI 未监听提示、一键启动说明补「已运行不重复启动 / 启动结果以连接确认」 | `ConnectionSettingsPage.axaml(.cs)` |

工具链附带修复：`tools/deploy-plugin.ps1` 原为 UTF-8 无 BOM，PowerShell 5.1 会按 ANSI 解码导致
中文串破坏语法（现场报「表达式或语句中包含意外的标记」）；已改写为 **UTF-8 with BOM**，
5.1 与 7 均可解析（已用 `Parser::ParseFile` 校验通过）。

## 3. 回归测试

`tests/CyberTechRep.Tests/NapCatRunnerServiceTests.cs`：

| 用例 | 覆盖点 |
| --- | --- |
| `快速登录_传入裸QQ号` | 参数不含 `-q` 前缀（改动 1 的锁） |
| `启动_反向WS已连接_跳过重复启动` | 已接入时既不结束 QQ 进程也不拉起新进程（改动 3） |
| `启动_WebUI端口在监听_跳过重复启动` | 以 WebUI 端口探活判定「已在运行」（改动 3） |
| `启动_结束QQ进程开关开启_调用结束委托并写入日志` | 开关开启时调用结束委托、日志写明结束数量（改动 2） |
| `启动_结束QQ进程开关关闭_不调用结束委托` | 开关关闭时不动 QQ 进程（改动 2） |
| `打开WebUI_尚未发现地址_写入失败原因` | 未发现地址时不打开、写原因（改动 5） |
| `打开WebUI_端口未监听_不打开并写入端口原因` | 端口未监听时不打开死页面、写入保留端口排查提示（改动 5） |

**测试安全**：`NapCatRunnerService` 新增的「结束 QQ 进程」是注入委托，两个测试类的 `CreateRunner`
一律注入假委托（默认返回 0 且不计数），任何用例都不会真的结束机器上运行的 QQ。

## 4. 现场处置（本次运行环境恢复）

1. 完全退出 QQ，手动以 `launcher-user.bat 1223437061` 拉起 NapCat（裸 QQ 号 → 快速登录成功）。
2. `napcat/config/webui.json` 端口 `6099` → `6260`（避开保留段；原文件另存 `webui.json.bak-6099`），
   WebUI 恢复可访问：`http://127.0.0.1:6260/webui?token=…`。
3. 结果：NapCat 接入 23001，启动核对补齐 26 条消息（4 个群）。
4. **用户侧待办**：设置页把「登录方式」改为 **QQ 号快速登录** 并填 `1223437061`
   （改动 1 已让该设置真正生效；不改则每次启动仍需扫码）。

## 5. 未决 / 风险

- Windows 保留端口段会在重启/服务变化后漂移，`6260` 未来仍可能被吞；届时按日志提示改
  `webui.json` 的 `port` 即可（插件会探活并明确报出）。
- 「已在运行检测」以「反向 WS 已连接」或「WebUI 端口可连接」为据；若 NapCat 处于
  「进程在但 WebUI 端口不可用且从未接入」的中间态，插件会按未运行处理并重新拉起（此时
  `NapCatEndExistingQq` 会先结束旧 QQ 实例）。
