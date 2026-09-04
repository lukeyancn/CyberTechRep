# 模块 1 交付报告：QQ 消息接入层

> 状态：**完成**（编码 → 单元测试 → 自检 → 可编译可运行 闭环已走完）
> 日期：2026-09-03

## 0. 关键决策变更（相对模块 0 契约）

协议端由「NapCat/OneBot 11」改为 **QQ 官方机器人开放平台（q.qq.com）群消息全量模式**（用户决策：零账号风控风险）。影响：

- 标识符体系改为 OpenID 字符串：`MessageRecord.MessageId/GroupOpenId/MemberOpenId` 均为 `string`；
- `ConnectionSettings` 改为 AppId/AppSecret/ApiBase/TokenApiUrl/群 OpenID 白名单；
- 接入协议为官方 WebSocket 网关（op 0/1/2/6/7/9/10/11，Intent `GROUP_AND_C2C_EVENT = 1<<25`）；
- 能力缺口（已在调研文档声明）：无历史消息补拉 API（`FetchHistoryAsync` 返回空并记警告）；群文件面板文件收不到（仅聊天消息内的文件/图片）。

## 1. 改动文件清单

| 文件 | 说明 |
| --- | --- |
| `ClassIng.sln` | 解决方案（4 个工程） |
| `src/ClassIng.Shared/ClassIng.Shared.csproj` | 契约层工程（net8.0，无 Avalonia/宿主依赖） |
| `src/ClassIng.Shared/Models/Enums.cs` | 公共枚举（MessageKind/SubjectSource/FileStatus/ConnectionStatus 等） |
| `src/ClassIng.Shared/Models/MessageModels.cs` | MessageRecord/MessageSegment/ClassifiedMessage/NoticeItem/HomeworkItem/FileRecord/SubjectRule/SubjectResult/PendingConfirmItem/RetryQueueItem（OpenID 体系修订版） |
| `src/ClassIng.Shared/Models/SettingsModels.cs` | 五组设置 DTO（连接设置按官方平台修订） |
| `src/ClassIng.Shared/Abstractions/Abstractions.cs` | 全部模块接口（IMessageIngestService/ISubjectClassifier/…/UpdateInfo） |
| `src/ClassIng.Plugin/ClassIng.Plugin.csproj` | 插件工程（EnableDynamicLoading + CreateCipx，PluginSdk 2.1.0.1 编译期引用） |
| `src/ClassIng.Plugin/manifest.yml` | 插件清单（id=classisland.classing, apiVersion 2.0.0.0） |
| `src/ClassIng.Plugin/Plugin.cs` | 插件入口：向宿主 DI 注册消息接入服务 |
| `src/ClassIng.Plugin/Utils/SecretProtector.cs` | DPAPI 敏感信息加密（AppSecret/API Key） |
| `src/ClassIng.Plugin/Services/MessageAccess/QQOfficialModels.cs` | 网关帧模型（GatewayOp/事件类型/GroupMessageEvent 容错解析） |
| `src/ClassIng.Plugin/Services/MessageAccess/QQOfficialWsClient.cs` | WebSocket 网关客户端：AccessToken 获取与刷新、网关获取、Identify/Resume、心跳、op7/op9 处理、指数退避重连 |
| `src/ClassIng.Plugin/Services/MessageAccess/MessageIdempotencyStore.cs` | 消息幂等存储（持久化 FIFO 5000 条，临时文件原子替换） |
| `src/ClassIng.Plugin/Services/MessageAccess/MessageIngestPipeline.cs` | 接入管道：事件→MessageRecord 映射、白名单过滤、幂等、脱敏快照 |
| `src/ClassIng.Plugin/Services/MessageAccess/MessageIngestService.cs` | IMessageIngestService 实现：组合客户端+管道，状态广播、设置热更新、单事件异常隔离 |
| `tools/ClassIng.IngestConsole/` | 验收自检工具（真实凭据连官方平台，结构化日志打印收到的消息） |
| `tests/ClassIng.Tests/` | 单元/集成测试（10 个用例）+ 本地假网关服务器（RFC6455 最小实现） |
| `tools/shim/pwsh.bat` | 本机构建垫片：cipx 打包脚本调用 pwsh，转发到 Windows PowerShell（CI 容器自带 pwsh，不受影响） |

## 2. 测试与验收结果

```
已通过! - 失败: 0，通过: 10，已跳过: 0，总计: 10
构建：0 警告 / 0 错误；ClassIng.Plugin.cipx 插件包已生成
```

| 用例 | 覆盖点 |
| --- | --- |
| 同一消息重复推送_只产生一条记录_幂等 | message id 幂等 |
| 幂等存储_跨实例持久化_重启后不重复 | 幂等持久化 |
| 群白名单_命中接收_未命中过滤 / 白名单为空_接收全部但仅告警一次 | 白名单 |
| 事件映射_文本与附件段_正确解析 | OpenID/附件字段映射 |
| 缺少消息id_事件被丢弃_不静默丢失 / 非群消息事件_被忽略 | 防错 |
| 鉴权_Identify携带QQBot前缀Token与Intent_并按间隔发送心跳 | Identify 内容 + 心跳节律 |
| 断线后指数退避重连_恢复会话Resume_消息送达不丢失 | 重连 + Resume(session_id/seq) + 消息送达 |
| 历史补拉_官方平台无此能力_返回空并记日志 | 能力缺口降级 |

真实环境验收（需用户凭据，二选一）：
1. 运行 `dotnet run --project tools/ClassIng.IngestConsole -- --appid <AppID> --secret <AppSecret>`，在已接入机器人的班级群发言，观察结构化日志输出；
2. 待模块 9 打包后在干净环境验证。

## 3. 约束自查（逐条）

| 约束 | 自查结果 |
| --- | --- |
| 协议端与业务解耦（可更换协议端） | ✅ 业务只见 `IMessageIngestService` 与 `MessageRecord`；OneBot 语义未泄漏出 `Services/MessageAccess` 文件夹 |
| 消息幂等（协议端重发不产生重复条目） | ✅ `MessageIdempotencyStore`（消息 id 持久化 + 单测两条） |
| 断线重连（指数退避） | ✅ 2s×2 退避至 5min 上限，可配置；重连集成测试通过 |
| 历史回放/断线补拉 | ⚠️ 官方平台无历史 API，接口保留、返回空并记警告（已在调研与决策中声明并接受） |
| 任何一环失败不崩溃主程序 | ✅ 单条事件处理 try/catch 隔离；幂等存储损坏时空库启动保损坏文件；接收循环异常退避重连 |
| 结构化日志（含脱敏快照） | ✅ ILogger 结构化字段；快照不含凭据；HTTP 失败只输出状态码 |
| 敏感信息加密存储 | ✅ `SecretProtector`（DPAPI CurrentUser）；日志不输出 Secret |
| 识别降级链/设置完备等 | 属于模块 2/3/7，本模块不涉及 |
| 模块间只经接口通信 | ✅ 仅暴露 `ClassIng.Shared` 契约与 `IMessageIngestService` |
| GPL-3.0 合规 | ✅ 本模块代码将随项目整体以 GPL-3.0 开源；依赖仅 .NET 内置与宿主 SDK（LGPL-3.0） |

## 4. 已知事项 / 后续模块衔接点

- `Plugin.cs` 的数据目录暂用 `%LocalAppData%\ClassIsland\Plugins\classisland.classing\data`，模块 7 将替换为宿主 PluginConfigFolder 并接入 `ISettingsService`（含热更新 `ApplySettings`）；
- 群白名单空 = 接收全部群（仅调试期），首次启动引导（模块 9）将强制配置；
- `tools/shim/pwsh.bat` 为本机补丁；GitHub Actions 的 windows-latest 自带 pwsh，无需处理。
