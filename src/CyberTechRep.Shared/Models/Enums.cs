namespace CyberTechRep.Shared.Models;

/// <summary>消息分类结果：普通消息两大类。</summary>
public enum MessageKind
{
    Unknown,
    Notice,
    Homework
}

/// <summary>学科识别来源级别（对应三级降级链）。</summary>
public enum SubjectSource
{
    /// <summary>①关键词规则命中。</summary>
    KeywordRule,

    /// <summary>②本地 ONNX 小模型。</summary>
    LocalModel,

    /// <summary>③云端 LLM API。</summary>
    CloudLlm,

    /// <summary>人工确认/手动修正。</summary>
    Manual
}

/// <summary>人工确认队列条目状态。</summary>
public enum PendingConfirmStatus
{
    Waiting,
    Resolved,
    Expired
}

/// <summary>文件处理状态机。</summary>
public enum FileStatus
{
    /// <summary>已入队未下载。</summary>
    Pending,

    /// <summary>临时文件下载中。</summary>
    Downloading,

    /// <summary>MD5 校验中。</summary>
    Verifying,

    /// <summary>归档完成。</summary>
    Archived,

    /// <summary>MD5 重复：新文件已删除（保留原记录并记日志）。</summary>
    Duplicate,

    /// <summary>失败（进重试队列）。</summary>
    Failed
}

/// <summary>重试队列操作类型。</summary>
public enum RetryOperationType
{
    FileDownload,

    /// <summary>AI 识别重试。</summary>
    SubjectClassify,

    /// <summary>持久化写入重试。</summary>
    StoreWrite,

    /// <summary>其他（含排错面板手动重放）。</summary>
    Custom
}

/// <summary>协议端连接状态。</summary>
public enum ConnectionStatus
{
    Disconnected,

    /// <summary>正在建立连接（拨号 / 反向监听等待接入）。</summary>
    Connecting,

    /// <summary>
    /// 传输层已就绪但尚未确认 OneBot 侧可用（等待生命周期/心跳 meta_event）。
    /// NapCat 模式不得仅凭 TCP/WS 握手成功就判定「已连接」——握手成功只代表管道通了。
    /// </summary>
    Authenticating,

    /// <summary>已连接：已收到 OneBot 生命周期（lifecycle）或心跳（heartbeat）事件。</summary>
    Connected,

    /// <summary>鉴权失败（access token 不匹配 / 401 / 403）：需要用户改配置，不是暂时性抖动。</summary>
    AuthenticationFailed,

    Reconnecting,
    Faulted
}

/// <summary>重试队列条目状态。</summary>
public enum RetryItemStatus
{
    Waiting,
    Retrying,
    Succeeded,
    GivenUp
}

/// <summary>消息段类型（规范化后的子集）。</summary>
public static class SegmentTypes
{
    public const string Text = "text";
    public const string Image = "image";
    public const string File = "file";
    public const string Video = "video";
    public const string Voice = "voice";
    public const string At = "at";
}
