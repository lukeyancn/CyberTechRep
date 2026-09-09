using CyberTechRep.Shared.Models;

namespace CyberTechRep.Plugin.Services.MessageAccess;

/// <summary>
/// 消息接入网关客户端抽象：QQ 官方 <see cref="QQOfficialWsClient"/> 与
/// NapCat <see cref="NapCatWsClient"/> 共同满足，供 <see cref="MessageIngestService"/>
/// 按设置模式无感切换（分发/状态/生命周期语义与官方客户端完全一致）。
/// </summary>
public interface IMessageGatewayClient : IAsyncDisposable
{
    /// <summary>当前连接状态（经 <see cref="ConnectionStateChanged"/> 广播）。</summary>
    ConnectionStatus Status { get; }

    /// <summary>
    /// 分发事件透传：(t, 原始 JSON 文本)。NapCat 客户端把 OneBot 11 事件规范化为
    /// 官方事件 JSON 后透传，接入管道无需感知协议差异。
    /// </summary>
    event EventHandler<(string Type, string Data)>? DispatchReceived;

    /// <summary>
    /// 消息撤回（NapCat 模式：OneBot 11 <c>notice.group_recall</c> / <c>friend_recall</c>）。
    /// 官方模式当前无该事件（不触发）。接入层据此联动删除通知/作业存档。
    /// </summary>
    event EventHandler<MessageRecallEvent>? MessageRecalled;

    event EventHandler<ConnectionStatus>? ConnectionStateChanged;

    /// <summary>
    /// 经当前连接调用一次协议端原生 API（OneBot 11 <c>{action, params, echo}</c>）。
    /// 仅 NapCat 客户端支持；官方客户端返回 null（能力缺口，不抛异常）。
    /// 失败/超时/未连接一律返回 null，不抛异常、不断连接。
    /// </summary>
    Task<System.Text.Json.JsonElement?> CallApiAsync(
        string action, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct = default);

    /// <summary>启动客户端（非阻塞；内部长循环，随 StopAsync/Dispose 结束）。</summary>
    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    /// <summary>手动重连（排错面板触发）。</summary>
    Task ReconnectAsync();
}
