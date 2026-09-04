# ClassIng 依赖选型调研报告

> 项目：基于 ClassIsland（C#/.NET 8 WPF，GPL-3.0）以插件形式构建的 QQ 群消息接管与课堂信息分发系统
> 调研日期：2026-09-03（Star 数、最近推送时间均为当日 GitHub API 实测值；NuGet 版本为 nuget.org 实测值）
> 调研方式：GitHub REST API、nuget.org Search API、官方文档与仓库原文核实

## 0. License 兼容性总原则

本项目以 ClassIsland 插件形式分发，ClassIsland 本体为 **GPL-3.0**，因此插件属于 GPL-3.0 派生作品，整体必须以 GPL-3.0 兼容方式发布。结论：

- **MIT / BSD / Apache-2.0** 等宽松许可证均可引入 GPL-3.0 程序中，无兼容性问题（分发时保留原始版权声明即可）。
- **GPL-3.0/LGPL** 依赖与本项目同向兼容。
- **无许可证（All Rights Reserved）** 的代码不可复制、修改或作为库链接，只能作为**独立进程**调用。
- **自定义限制性许可证**（如 NapCat）只要不重新分发其代码，仅作为外部程序使用，不影响本项目。

---

## 1. QQ 协议端接入（OneBot 11）

### 候选 1：NapCat（推荐，作为外部协议端进程）
- 项目地址：https://github.com/NapNeko/NapCatQQ
- Star：10,475｜最近推送：2026-08-31（高度活跃）｜基于 NTQQ，Node.js/TS 实现
- License：**自定义 "Limited Redistribution License"**（GitHub 识别为 NOASSERTION，限制代码的复制/修改/再分发）——但**仅作为外部进程运行、不捆绑分发其代码即完全安全**
- 接入方式：完整支持 OneBot 11 的正向 WebSocket、反向 WebSocket、HTTP；提供 NapCat 扩展 API（群文件 URL 获取等）
- 群文件下载：经 OneBot 11 事件获取文件信息后，可通过协议端 HTTP API 拿到下载 URL（NapCat 支持 `get_image` 返回 URL、`get_group_file_url` 等扩展接口；go-cqhttp 兼容的 `download_file` 亦可），插件侧用 `HttpClient` 携带 access token 下载即可。**可行，实测路径明确。**

### 候选 2：Lagrange.Core（备选，作为外部进程使用）
- 项目地址：https://github.com/LagrangeDev/Lagrange.Core
- Star：2,975｜最近推送：2026-09-01（活跃）｜纯 C# 实现 NTQQ 协议
- License：**重要发现**——仓库 master（V2）**已无 LICENSE 文件**，仅有免责声明（默认保留所有权利）；v1 分支为 **GPL-3.0** 且已宣布 sunset（最后提交 2025-10-10）
- 接入方式：C# NuGet 库（`Lagrange.Core`）或独立运行 `Lagrange.OneBot` 提供标准 OneBot 11 服务
- 评估：若直接以 NuGet 库链接进插件，V2 无许可证存在法律风险，且插件本需处理 GPL 派生问题，得不偿失。**建议仅将 Lagrange.OneBot 作为与 NapCat 平行的独立协议端备选**，不在插件内引用其代码。

### 候选 3：OpenShamrock（排除）
- 原作者（fuqiuluo）已脱离开发、原仓库不可达，社区接手分叉活跃度有限；且基于 Android/Xposed 部署，与「Windows 桌面 + WPF 插件」场景不匹配。排除。

### 候选 4：现成 C# OneBot SDK（不推荐采用）
- 传闻中的 "OneBot.NET"：GitHub/NuGet 上**未找到主流维护版本**（`linli2016/OneBot.Net` 等已 404）。
- cqhttp.Cyan（https://github.com/frank-bots/cqhttp.Cyan ）：64 stars，MIT，最后推送 **2022-06**，面向旧 CQHTTP 时代，已停滞。
- Gdr2333.BotLib.OnebotV11（https://github.com/gdr2333/Gdr2333.BotLib.OnebotV11 ）：Apache-2.0，0 star 个人库，可靠性不足。
- **结论：C# OneBot SDK 生态真空。建议自研轻量接入层**（`System.Net.WebSockets.ClientWebSocket` + `System.Text.Json`，正向 WS 连 NapCat，反序列化事件 + 封装 action 调用 + 断线重连）。OneBot 11 事件 JSON 结构简单，自研量约数百行，可控性、可调试性远好于陈旧第三方 SDK。

**最终推荐：NapCat（外部进程）+ 自研轻量 OneBot 11 WebSocket 接入层；文件下载走协议端 HTTP API + HttpClient。**

---

## 2. WPF 常驻悬浮窗

### 候选 1：自研 WindowChrome + Win32（推荐）
- 无边框：`WindowChrome`（`UseAeroCaptionButtons=false`，`CaptionHeight=0`）或 `ResizeMode` + `AllowsTransparency`；
- 置顶/工具窗：P/Invoke `SetWindowPos`（`HWND_TOPMOST`）、`WS_EX_TOOLWINDOW` 隐藏 Alt-Tab；
- 可拖拽：`DragMove()` 或 `WM_NCHITTEST` 返回 `HTCAPTION`；
- 可缩放：`ResizeBorderThickness`（WindowChrome 自带边缘 resize）；
- DPI/多显示器：`PerMonitorV2`（app.manifest）+ WPF 4.6.2+ 原生支持，仅需处理 `DpiChanged` 事件微调尺寸。
- 复杂度：约 200–400 行，无第三方依赖，与 ClassIsland 宿主风格最一致。**复杂度可控，推荐自研。**

### 候选 2：MicaWPF（可选增强）
- https://github.com/Simnico99/MicaWPF ｜Star 269｜最近推送 2026-08-27（活跃）｜MIT
- NuGet：`MicaWPF` 7.1.0（约 81 万下载）。提供 Mica/Acrylic 窗口材质与标题栏简化。
- 评估：仅当悬浮窗需要系统材质背景时引入；本项目悬浮窗更可能用自绘半透明，非必需。

### 候选 3：WPF UI（lepoco/wpfui）（备选）
- https://github.com/lepoco/wpfui ｜Star 9,624｜最近推送 2026-06-27（活跃）｜MIT
- NuGet：`WPF-UI` 4.3.0（约 112 万下载）。完整 Fluent 控件库，含窗口/托盘辅助。
- 评估：控件全但体积大，与 ClassIsland 自有 UI 体系可能样式冲突。悬浮窗场景收益低。

### 候选 4：AdonisUI（不推荐）
- https://github.com/benruehl/adonis-ui ｜Star 1,868｜MIT｜**最后推送 2022-09，已停止维护**。暗色主题库，与悬浮窗核心需求（无边框/置顶/DPI）弱相关。

**最终推荐：自研 WindowChrome + Win32 API（PerMonitorV2），按需加 MicaWPF 做材质；不引入 WPF UI/AdonisUI。** 顺带建议：托盘常驻用 `H.NotifyIcon.Wpf` 2.4.1（MIT，活跃）。

---

## 3. MD5 去重

- **推荐：.NET 内置 `System.Security.Cryptography.MD5`（自研，无需第三方）**。用法：`MD5.HashData(bytes)`（.NET 5+ 静态方法，无分配开销）；文件用流式 `TransformBlock/TransformFinalBlock` 计算。MD5 用于文件指纹/消息去重属非安全用途，合规且够快（数百 MB/s）。
- 去重记录持久化：内存 LRU（`MemoryCache` 或自写环形集合）应对会话内去重；需跨重启去重时配 `Microsoft.Data.Sqlite`（见第 5 节）。
- 备选：`System.IO.Hashing`（微软官方，MIT，XxHash128 更快）——若仅做文件完整性指纹可换，但 QQ 群文件场景已有服务端文件 ID/MD5 可用，优先用协议端提供的现成哈希。
- **结论：内置 MD5 即可，无需第三方。**

---

## 4. 轻量 AI 文本分类/识别

### 候选 1：ONNX Runtime + 中文小型 BERT（推荐，本地离线）
- NuGet：`Microsoft.ML.OnnxRuntime` 1.29.0（MIT，约 1,440 万下载，微软官方持续维护）
- 模型方案：中文 TinyBERT 4 层（如 HuggingFace `huawei-noah/TinyBERT_General_4L_zh`，约 14.5M 参数；ONNX fp32 约 60MB，int8 量化后约 15–20MB）。魔搭 ModelScope 亦有镜像。**需要自备少量标注数据离线微调「通知 vs 作业」「学科多分类」头后导出 ONNX**（Python 侧一次性工作，插件侧只做 tokenizer（词表文件）+ 推理）。
- 性能量级：CPU 单条短文本推理约 5–50ms，完全满足消息流节奏；置信度：**中**（模型体积/速度为业界共识量级，未做实测）。
- 最小可行路径：规则/关键词先行 → 置信不足时走 ONNX 二分类 → 可选云端兜底。

### 候选 2：ML.NET（备选）
- https://github.com/dotnet/machinelearning ｜Star 9,353｜MIT｜活跃（2026-09 有推送）
- 评估：适合 n-gram + 线性模型（体积小、CPU 快、纯 C# 训练），但对 BERT 类深度中文语义分类支持弱；可作为**零标注冷启动**的基线（关键词特征 + Lbfgs/Sdca）。NuGet：`Microsoft.ML`。

### 候选 3：云端 OpenAI 兼容 API（兜底备选）
- DeepSeek（`https://api.deepseek.com`）、阿里云百炼/DashScope 兼容模式、智谱 GLM 均提供 OpenAI 兼容 REST；用 `HttpClient` + 自写少量请求体即可，**无需第三方 SDK**。
- 优点：零模型管理、效果上限高；缺点：教室网络环境不稳定、学生消息出网隐私敏感、有费用与延迟。建议仅作可开关的兜底通道。

**最终推荐：ONNX Runtime + 自微调中文 TinyBERT 小模型（int8 量化）；ML.NET 作冷启动基线；云端 API 作可关闭兜底。**

---

## 5. 配置持久化

- **JSON：内置 `System.Text.Json`（推荐，无需第三方）**。模块内部配置用 POCO + 源生成器（`JsonSerializerContext`）AOT 友好；导入导出直接整体序列化为带 `schemaVersion` 字段的单文件，反序列化时用可空属性 + 默认值容错旧版本。
- **SQLite：`Microsoft.Data.Sqlite` 10.0.11（MIT，约 1.23 亿下载，微软官方）**——用于去重记录、消息历史等批量/可查询数据；EF Core 不是必需，`Microsoft.Data.Sqlite` + 裸 SQL 足够轻。
- ClassIsland 自带 profile/Settings 机制：插件设置项优先挂接 ClassIsland 的插件设置页；模块私有配置落盘到插件数据目录的 JSON。两者分工：**ClassIsland 管「用户可见设置」，模块 JSON 管「内部状态」，SQLite 管「历史数据」**。
- 备选：CommunityToolkit.Mvvm 8.4.2（MIT）——ClassIsland 本体即基于它，插件遵循同一 MVVM 体系可减少摩擦。

---

## 6. 打包发布

### dotnet publish 单文件（.NET 8/9 WPF）现状与已知坑
- 命令：`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
- 已知坑：
  - **不要开启 `PublishTrimmed`**（WPF 使用反射，裁剪会运行时崩）；
  - 原生库（如 ONNX Runtime 的 `onnxruntime.dll`）需 `-p:IncludeNativeLibrariesForSelfExtract=true`，或改为框架依赖发布；
  - 建议加 `-p:PublishReadyToRun=true` 优化首次启动；
  - 自包含体积约 100–180MB（含 .NET 运行时）；框架依赖发布仅数 MB，但要求目标机已装 .NET 8 Desktop Runtime——ClassIsland 本体即 .NET 8，插件跟随宿主运行时，**插件本身建议框架依赖发布**，只有「协议端全家桶」才需要自包含。

### 安装器对比
- **Inno Setup（推荐）**：https://github.com/jrsoftware/issrc ｜Star 5,610｜2026-09 活跃｜"Inno Setup License"（自定义宽松许可，免费含商用，GitHub 识别为 NOASSERTION 但允许任意分发使用）。脚本简单、中文官方语言包完善。
- NSIS：zlib/libpng 许可，脚本繁琐，社区源文档参差，无特殊收益不选。
- **单 zip（插件场景优先推荐）**：ClassIsland 插件 = DLL 放入插件目录 + 重启加载，zip 解压即用，最贴合插件分发惯例；Inno Setup 仅在打包「插件 + NapCat + 配置向导」一体化安装时使用。

### GitHub Actions
- 常见做法：`windows-latest` + `actions/setup-dotnet@v4`（`dotnet-version: 8.0.x`）→ `dotnet publish` → `actions/upload-artifact@v4`；发版用 `softprops/action-gh-release` 附带 zip；NuGet 缓存用 `actions/cache`（`~/.nuget/packages`）。成熟无坑，置信度：高。

---

## 7. 幂等与重试队列

- **Polly（推荐引入）**
  - https://github.com/App-vNext/Polly ｜Star 14,235｜2026-08-28 活跃｜**BSD-3-Clause**（宽松许可，与 GPL-3.0 完全兼容）
  - NuGet：`Polly.Core` 8.7.0（约 6.3 亿下载，依赖极简，包体积数百 KB 量级）
  - 适用点：WebSocket 断线重连（`Retry Forever` + 指数退避 + jitter）、群文件 HTTP 下载重试（`Retry` + 超时 + 熔断）、AI API 调用重试。`Microsoft.Extensions.Resilience`/Polly v8 管道式 API 与 DI 集成良好。
- 备选：对 WS 重连这类单点场景，自写「退避循环 + 状态机」也只需几十行；若团队希望零依赖可先自研，其余网络调用统一走 Polly。
- **结论：引入 Polly.Core，用于全部出站网络调用；WS 重连可叠加简单状态机。**

---

## 依赖选型表

| 模块 | 推荐方案 | 备选 | NuGet/来源 | License | 维护状态 | 理由 | 置信度 |
|---|---|---|---|---|---|---|---|
| QQ 协议端 | NapCat（外部进程） | Lagrange.OneBot（外部进程） | 官网 Release / LagrangeDev | NapCat：自定义限制性许可（外部使用安全）；Lagrange v1：GPL-3.0、V2 无许可证 | NapCat 活跃（2026-08）；Lagrange 活跃但 v1 已 sunset | 用户基数最大、OneBot 11 全支持；外部进程规避许可风险 | 高 |
| OneBot 接入层 | **建议自研**（ClientWebSocket + System.Text.Json） | cqhttp.Cyan | 内置 / frank-bots（MIT，2022 停滞） | MIT | SDK 生态真空 | 无活跃 C# SDK；协议简单，自研数百行可控 | 高 |
| 群文件下载 | 协议端 HTTP API + HttpClient | go-cqhttp 兼容 `download_file` | 内置 | — | — | `get_image`/`get_group_file_url` 返回 URL，token 鉴权下载 | 中 |
| WPF 悬浮窗 | **建议自研**（WindowChrome + Win32 + PerMonitorV2） | MicaWPF（材质增强） | 内置 / `MicaWPF` 7.1.0 | MIT（MicaWPF） | MicaWPF 活跃 | 悬浮窗核心能力 WPF 原生即可，约 300 行；第三方 UI 库体积/风格冲突大于收益 | 高 |
| MD5 去重 | 内置 `MD5.HashData`（自研） | System.IO.Hashing (xxHash) | 内置 | MIT | 随 .NET | 非安全用途合规够快；跨重启去重再配 SQLite | 高 |
| AI 文本分类 | ONNX Runtime + 自微调中文 TinyBERT（int8） | ML.NET 基线；DeepSeek/通义/智谱云端兜底 | `Microsoft.ML.OnnxRuntime` 1.29.0；`Microsoft.ML` | MIT | onnxruntime 活跃（微软官方）；ML.NET 活跃 | 离线、隐私安全、CPU 5–50ms/条；模型约 15–20MB（量化） | 中 |
| 配置持久化 | System.Text.Json（设置/导入导出）+ Microsoft.Data.Sqlite（历史数据） | ClassIsland profile 挂接 | 内置 / `Microsoft.Data.Sqlite` 10.0.11 | MIT | 随 .NET / 官方活跃 | 分工清晰、零第三方风险；schemaVersion 做导入导出版本容错 | 高 |
| 打包发布 | 插件：框架依赖 zip；全家桶：Inno Setup | NSIS；自包含单文件 | `jrsoftware/issrc`（Inno Setup License，免费含商用） | 宽松自定义 | Inno Setup 活跃（2026-09） | 插件分发惯例是解压即用；自包含需注意禁用 Trimmed、IncludeNativeLibrariesForSelfExtract | 高 |
| CI 构建 | GitHub Actions windows-latest + setup-dotnet@v4 | Azure Pipelines | 官方 actions | MIT | 官方维护 | 生态成熟，artifact/release 一条龙 | 高 |
| 幂等与重试 | Polly.Core 8.7.0 | 自写退避循环 | `Polly.Core` | BSD-3-Clause（GPL 兼容） | 活跃（2026-08） | 6.3 亿下载、依赖极简；覆盖重试/超时/熔断/重连 | 高 |

## 附：数据快照（2026-09-03 实测）

| 仓库 | Star | 最近推送 | License（API 识别） |
|---|---|---|---|
| NapNeko/NapCatQQ | 10,475 | 2026-08-31 | NOASSERTION（Limited Redistribution License，原文已核） |
| LagrangeDev/Lagrange.Core | 2,975 | 2026-09-01 | master 无 LICENSE；v1 分支 GPL-3.0（原文已核） |
| lepoco/wpfui | 9,624 | 2026-06-27 | MIT |
| Simnico99/MicaWPF | 269 | 2026-08-27 | MIT |
| benruehl/adonis-ui | 1,868 | 2022-09-29 | MIT（已停更） |
| microsoft/onnxruntime | 21,754 | 2026-09-03 | MIT |
| dotnet/machinelearning | 9,353 | 2026-09-03 | MIT |
| App-vNext/Polly | 14,235 | 2026-08-28 | BSD-3-Clause |
| jrsoftware/issrc | 5,610 | 2026-09-03 | NOASSERTION（Inno Setup License） |
| frank-bots/cqhttp.Cyan | 64 | 2022-06-23 | MIT（已停滞） |
| ClassIsland/ClassIsland | 2,742 | 2026-09-01 | GPL-3.0 |
