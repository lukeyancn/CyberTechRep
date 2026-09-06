using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.SubjectChain;

/// <summary>
/// 本地 AI 提供商标记：链组合器据此按 PreferLocalModel 排序（本地优先/云端优先）。
/// </summary>
public interface ILocalAiProvider
{
}

/// <summary>
/// 模块 3 第②级（本地）：ONNX 小模型学科识别器。
/// <para>
/// 当前为<strong>可运行骨架</strong>：模型文件存在性检查与降级链路完整；
/// 推理调用以占位实现（模型缺失时 <see cref="IsAvailable"/>=false 自动跳到云端/人工）。
/// 接入真实推理仅需替换 <see cref="ClassifyAsync"/> 中「占位」段（Microsoft.ML.OnnxRuntime InferenceSession），
/// 模型路径走 <see cref="AiSettings.LocalModelPath"/> 配置。
/// </para>
/// </summary>
public sealed class LocalOnnxAiProvider : IAiProvider, ILocalAiProvider
{
    private readonly SubjectChainOptionsProvider _provider;
    private readonly ILogger _logger;

    public LocalOnnxAiProvider(SubjectChainOptionsProvider provider, ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public string Name => "LocalOnnx";

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            var settings = _provider.SafeGetAiSettings();
            if (!settings.AiEnabled)
            {
                return false;
            }

            try
            {
                return File.Exists(ResolveModelPath(settings));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public Task<SubjectResult?> ClassifyAsync(string text, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!IsAvailable || string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult<SubjectResult?>(null);
            }

            var settings = _provider.SafeGetAiSettings();
            // —— 推理占位：真实 ONNX 会话加载与 tokenizer 接入见交付报告「已知缺口」 ——
            _logger.LogWarning(
                "本地 ONNX 推理为占位实现（modelPath={ModelPath}），返回 null 交给下一级",
                ResolveModelPath(settings));
            return Task.FromResult<SubjectResult?>(null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "本地模型识别异常，返回 null 进入下一级");
            return Task.FromResult<SubjectResult?>(null);
        }
    }

    private string ResolveModelPath(AiSettings settings)
    {
        var path = string.IsNullOrWhiteSpace(settings.LocalModelPath)
            ? "models/subject-classifier.onnx"
            : settings.LocalModelPath;
        return Path.IsPathRooted(path) ? path : Path.Combine(_provider.DataDirectory, path);
    }
}
