using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CyberTechRep.Shared.Abstractions;
using CyberTechRep.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CyberTechRep.Plugin.Services.Files;

/// <summary>
/// 模块 4：文件处理管道。
/// 原子下载（.tmp 半成品文件 → MD5 校验 → 归档到 下载文件/&lt;学科&gt;/&lt;yyyy-MM-dd&gt;/&lt;文件名&gt;）、
/// <b>瞬时失败自动重试 + HTTP Range 断点续传</b>（同一记录的重试从已完成字节继续，服务器不支持续传则自动从头下）、
/// MD5 去重、路径安全校验（拒绝穿越/非法字符/超长/Windows 保留名）、
/// 单文件大小上限、下载并发队列化（SemaphoreSlim）、磁盘占用上限与清理策略。
/// 失败记录 <see cref="FileStatus.Failed"/> + LastError；<see cref="EnqueueAsync"/> 可重入触发尽力重试
/// （持久化重试队列与重放 UI 属模块 8，本模块不做后台自动重试）。
/// HttpClient 可注入（测试用假 HttpMessageHandler）。
/// </summary>
public sealed class FilePipelineService : IFilePipelineService, IFileImportService
{
    /// <summary>初始归档学科目录（学科识别完成后经 ReassignSubject 移动）。</summary>
    private const string UnfiledSubject = "未分类";

    /// <summary>拖放导入文件的 MessageId 占位（files.json 中 MessageId 必填，拖放文件无来源消息）。</summary>
    internal const string DropImportMessageId = "drop-import";

    private const string TempPrefix = ".download-";
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// 单次入队的下载尝试次数（含首次）。瞬时失败（网络抖动/连接被断/读取停滞/5xx）按退避重试，
    /// 已落盘的字节保留，重试用 <c>Range</c> 从断点继续——「文件有时候下载不下来」多数是
    /// 单次抖动导致的永久失败（旧实现只试一次）。
    /// </summary>
    private const int DownloadMaxAttempts = 3;

    /// <summary>重试退避基数（毫秒）：第 n 次失败后等待 base * 3^(n-1)（1s、3s）。</summary>
    private const int DownloadRetryBaseDelayMs = 1000;

    /// <summary>
    /// 读取停滞上限：单次读取超过该时长没有新字节即认定连接僵死（按可重试失败处理、保留半成品续传）。
    /// 取代「整个请求 60 秒硬超时」——大文件在慢链路上过去会因总时长超限直接失败，且半成品被删无法续传。
    /// </summary>
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>重试退避基数（毫秒）；测试可置 0 跳过等待。</summary>
    internal int RetryBaseDelayMs { get; init; } = DownloadRetryBaseDelayMs;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly FilePipelineOptionsProvider _provider;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _downloadSlots;
    private readonly object _lock = new();
    private readonly string _storePath;
    private List<FileRecord> _records = [];

    /// <inheritdoc />
    public event EventHandler<FileRecord>? FileUpdated;

    public FilePipelineService(
        FilePipelineOptionsProvider provider,
        HttpClient? httpClient = null,
        ILogger? logger = null)
    {
        _provider = provider;
        // 总超时不再由 HttpClient 硬控：改为流内「读取停滞上限」（见 DownloadStallTimeout）——
        // 整个请求 60 秒硬超时会让大文件在慢链路上直接失败且无法续传。
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _logger = logger ?? NullLogger.Instance;
        _downloadSlots = new SemaphoreSlim(Math.Max(1, provider.GetSettings().DownloadConcurrency));
        Directory.CreateDirectory(provider.DataDirectory);
        _storePath = Path.Combine(provider.DataDirectory, "files.json");
        Load();

        // Information 级启动自检：归档库位置与记录数全程可见（归档库为空时上课联动不弹窗，
        // 用户须能从日志一眼看出「库是空的、文件在哪」，否则表现为「自动弹窗失效」无从排查）
        _logger.LogInformation(
            "文件管道就绪：记录 {Count} 条，归档库 {StorePath}，归档根目录 {Root}",
            _records.Count, _storePath, GetDownloadRoot());
    }

    /// <inheritdoc />
    public async Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url,
        string? memberOpenId = null, string? groupOpenId = null, CancellationToken ct = default)
    {
        var record = BeginEnqueue(messageId ?? "", fileName ?? "", memberOpenId, groupOpenId);
        RaiseUpdated(record);

        // ① 文件名路径安全校验（拒绝 ..、路径分隔符、非法字符、超长名、Windows 保留名）
        if (!TryValidateFileName(record.FileName, _provider.GetSettings().MaxFileNameLength, out var safeName, out var nameError))
        {
            return Fail(record, nameError);
        }

        // ② 下载地址缺失：可重入，稍后可再次 EnqueueAsync 重试
        if (string.IsNullOrWhiteSpace(url))
        {
            return Fail(record, "缺少附件下载地址（官方平台附件直链可能已过期），可重新入队重试");
        }

        ct.ThrowIfCancellationRequested();

        var settings = _provider.GetSettings();
        await _downloadSlots.WaitAsync(ct);
        try
        {
            return await DownloadAndArchiveAsync(record, safeName, url.Trim(), settings, ct);
        }
        finally
        {
            _downloadSlots.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReassignSubjectAsync(Guid fileId, string subject, CancellationToken ct = default)
    {
        await Task.CompletedTask;

        var record = FindById(fileId);
        if (record is null)
        {
            _logger.LogWarning("ReassignSubject：未找到文件记录 {FileId}", fileId);
            return;
        }

        if (record.Status != FileStatus.Archived || string.IsNullOrEmpty(record.ArchivedRelativePath))
        {
            _logger.LogWarning("ReassignSubject：文件 {Name} 状态为 {Status}，无可移动的归档文件", record.FileName, record.Status);
            return;
        }

        var settings = _provider.GetSettings();
        if (!settings.GroupBySubject)
        {
            // 未按学科分组归档：无需移动
            return;
        }

        // 学科名同时是归档目录名，走同一套路径安全校验
        if (!TryValidateFileName(subject, Math.Min(settings.MaxFileNameLength, 100), out var safeSubject, out var subjectError))
        {
            _logger.LogWarning("ReassignSubject：学科名 {Subject} 非法：{Error}", subject, subjectError);
            return;
        }

        var currentFirstSegment = record.ArchivedRelativePath.Split('/', '\\')[0];

        // 幂等：已位于目标学科目录时直接返回
        if (string.Equals(currentFirstSegment, safeSubject, StringComparison.Ordinal))
        {
            return;
        }

        var root = GetDownloadRoot();
        var source = Path.GetFullPath(Path.Combine(root, record.ArchivedRelativePath));
        if (!IsInsideRoot(root, source) || !File.Exists(source))
        {
            _logger.LogWarning("ReassignSubject：源文件不存在或路径异常：{Path}", record.ArchivedRelativePath);
            return;
        }

        // 保持原归档日期目录，仅切换学科层级
        var date = record.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd");
        var dir = Path.Combine(root, safeSubject, date);
        Directory.CreateDirectory(dir);
        var target = UniquePath(Path.Combine(dir, Path.GetFileName(source)));
        if (!IsInsideRoot(root, target))
        {
            _logger.LogWarning("ReassignSubject：目标路径越界，已拒绝：{Path}", target);
            return;
        }

        File.Move(source, target);
        record.ArchivedRelativePath = Path.GetRelativePath(root, target);
        PersistAndRaise(record);
        _logger.LogInformation("文件 {Name} 学科二次归档：{From} → {To}", record.FileName, currentFirstSegment, safeSubject);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileRecord>> GetRecordsAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<FileRecord> snapshot = _records.OrderBy(r => r.CreatedAt).ToList();
            return Task.FromResult(snapshot);
        }
    }

    // ============ 拖放导入（学科圆圈栏 / 学科文件悬浮窗 Drop）============

    /// <summary>
    /// 拖放导入：把磁盘源文件复制归档到指定学科，与下载文件共用同一 files.json 记录库与
    /// 「下载文件/&lt;学科&gt;/&lt;yyyy-MM-dd&gt;/&lt;文件名&gt;」归档布局（元数据格式零分叉）。
    /// <para>
    /// Copy 语义（严格）：全程只复制——先流式复制源文件到根目录内临时文件（同步算 MD5），
    /// 再复用既有 <see cref="ArchiveFile"/> 把「临时副本」移动进归档位；源文件自始至终保持原位。
    /// </para>
    /// <para>
    /// 冲突策略（显式、确定性）：目标目录已有同名文件时按「name (2).ext」顺延取空闲名，
    /// 绝不静默覆盖（区别于下载路径的 MD5 去重前置，本路径在去重判定之后仍可能同名冲突）。
    /// </para>
    /// 跳过/失败场景（均非致命、不抛异常）：源不存在、零字节、文件已在归档库内（防自复制）、
    /// 超过单文件大小上限、磁盘占用超上限（复用 EnsureDiskBudget 清理策略）、
    /// 文件名/学科名非法、IO 异常（源被占用等）。
    /// </summary>
    public async Task<SubjectDropImportResult> ImportAsync(string sourcePath, string subject, CancellationToken ct = default)
    {
        var settings = _provider.GetSettings();
        try
        {
            // ① 源文件存在性 / 零字节 / 大小上限预检（拿不到 FileInfo 视为不可访问，跳过）
            FileInfo info;
            try
            {
                info = new FileInfo(sourcePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "拖放导入：源文件不可访问，已跳过：{Path}", sourcePath);
                return SubjectDropImportResult.Skipped($"源文件不可访问，已跳过：{Path.GetFileName(sourcePath)}");
            }

            if (!info.Exists)
            {
                return SubjectDropImportResult.Skipped($"源文件不存在，已跳过：{Path.GetFileName(sourcePath)}");
            }

            if (info.Length == 0)
            {
                _logger.LogInformation("拖放导入：零字节文件已跳过：{Path}", sourcePath);
                return SubjectDropImportResult.Skipped($"零字节文件已跳过：{Path.GetFileName(sourcePath)}");
            }

            var maxFileBytes = settings.MaxFileSizeMb <= 0
                ? long.MaxValue
                : settings.MaxFileSizeMb * 1024L * 1024L;
            if (info.Length > maxFileBytes)
            {
                return SubjectDropImportResult.Failed(
                    $"文件 {info.Name} 大小 {info.Length} 字节超过单文件上限 {settings.MaxFileSizeMb} MB");
            }

            var root = GetDownloadRoot();

            // ② 源文件已在归档库内（从归档窗口往外拖又拖回来）：跳过防自复制
            if (IsInsideRoot(root, Path.GetFullPath(sourcePath)))
            {
                return SubjectDropImportResult.Skipped($"文件已在归档库内，无需重复导入：{info.Name}");
            }

            // ③ 学科名即归档目录名，与 ReassignSubject 同一套路径安全校验
            if (!TryValidateFileName(subject, Math.Min(settings.MaxFileNameLength, 100), out var safeSubject, out var subjectError))
            {
                return SubjectDropImportResult.Failed($"学科名 {subject} 非法：{subjectError}");
            }

            if (!TryValidateFileName(Path.GetFileName(sourcePath), settings.MaxFileNameLength, out var originalName, out var nameError))
            {
                return SubjectDropImportResult.Failed(nameError);
            }

            // ④ 同名冲突预解析：name (2).ext 顺延（策略见方法注释），后续 ArchiveFile 内部的
            //    UniquePath 兜底不会再改名（解析出的名字已空闲）
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            var dir = settings.GroupBySubject
                ? Path.Combine(root, safeSubject, date)
                : Path.Combine(root, date);
            var finalName = SubjectDropImport.ResolveAvailableName(dir, originalName);

            var record = new FileRecord
            {
                MessageId = DropImportMessageId,
                FileName = finalName,
                Status = FileStatus.Pending,
                CreatedAt = DateTimeOffset.Now
            };
            lock (_lock)
            {
                _records.Add(record);
                Save();
            }
            RaiseUpdated(record);

            // ⑤ 磁盘占用预算（与下载同一套：策略 0 拒收 / 策略 1 清理最旧）
            if (!EnsureDiskBudget(root, settings, info.Length, record))
            {
                return SubjectDropImportResult.Failed(record.LastError ?? "磁盘占用已达上限");
            }

            ct.ThrowIfCancellationRequested();

            // ⑥ 复制到临时文件（流式 + MD5），再走既有 ArchiveFile：临时副本 → 归档位（源文件不动）
            var tempPath = Path.Combine(root, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);
            try
            {
                var (size, md5) = await CopyToTempWithMd5Async(Path.GetFullPath(sourcePath), tempPath, maxFileBytes, ct);
                record.Size = size;
                record.Md5 = md5;

                // MD5 去重（与下载路径行为一致）：内容相同的文件不重复入库
                if (settings.Md5DedupEnabled && FindArchivedByMd5(md5) is { } original)
                {
                    TryDelete(tempPath);
                    record.Status = FileStatus.Duplicate;
                    record.CompletedAt = DateTimeOffset.Now;
                    PersistAndRaise(record);
                    _logger.LogInformation(
                        "拖放导入：{Name} 与已归档文件 {Existing} 内容相同（MD5 {Md5}），未重复入库",
                        record.FileName, original.ArchivedRelativePath, md5);
                    return SubjectDropImportResult.Duplicate(
                        $"{info.Name} 与已归档文件 {original.FileName} 内容相同，未重复入库", record);
                }

                var relative = ArchiveFile(
                    root, tempPath, settings.GroupBySubject ? safeSubject : "", record.CreatedAt.LocalDateTime, finalName);
                record.ArchivedRelativePath = relative;
                record.Status = FileStatus.Archived;
                record.CompletedAt = DateTimeOffset.Now;
                PersistAndRaise(record);
                _logger.LogInformation(
                    "拖放导入：{Name} 已复制归档至 {Path}（{Size} 字节，源文件保留）",
                    record.FileName, relative, size);
                return SubjectDropImportResult.Imported(record);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                Fail(record, "拖放导入已取消");
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                Fail(record, $"拖放导入失败：{ex.Message}", countAttempt: true);
                return SubjectDropImportResult.Failed($"复制 {info.Name} 失败：{ex.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拖放导入异常：{Path}", sourcePath);
            return SubjectDropImportResult.Failed($"导入 {Path.GetFileName(sourcePath)} 异常：{ex.Message}");
        }
    }

    /// <summary>流式复制源文件到临时文件并同步计算 MD5；强制执行单文件大小上限（本地复制不限速）。</summary>
    private static async Task<(long Size, string Md5)> CopyToTempWithMd5Async(
        string sourcePath, string tempPath, long maxFileBytes, CancellationToken ct)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var md5 = MD5.Create();

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxFileBytes)
            {
                throw new InvalidOperationException("文件大小超过单文件上限");
            }

            md5.TransformBlock(buffer, 0, read, null, 0);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        md5.TransformFinalBlock([], 0, 0);
        await target.FlushAsync(ct);
        return (total, Convert.ToHexString(md5.Hash ?? []).ToLowerInvariant());
    }

    // ============ 下载与归档 ============

    private async Task<FileRecord> DownloadAndArchiveAsync(
        FileRecord record, string safeName, string url, FileSettings settings, CancellationToken ct)
    {
        var root = GetDownloadRoot();
        // 半成品文件按「记录 id」命名（不是随机名）：同一记录的重试能从已完成字节续传。
        // 前缀/后缀沿用 .download-*.tmp，磁盘清理策略会跳过下载中的临时文件。
        var partialPath = Path.Combine(root, TempPrefix + record.Id.ToString("N") + TempSuffix);
        var maxFileBytes = settings.MaxFileSizeMb <= 0
            ? long.MaxValue
            : settings.MaxFileSizeMb * 1024L * 1024L;

        record.Status = FileStatus.Downloading;
        RaiseUpdated(record);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await DownloadOnceAsync(
                    record, safeName, url, settings, root, partialPath, maxFileBytes, attempt, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 宿主停止/取消：保留半成品（下次入队可续传），不算失败
                throw;
            }
            catch (Exception ex) when (attempt < DownloadMaxAttempts && IsTransientDownloadFailure(ex))
            {
                // 瞬时失败：保留已下载的字节，退避后从断点继续（不是从头再下）
                record.AttemptCount++;
                record.FailureRetriable = true;
                var resumed = PartialLength(partialPath);
                record.LastError =
                    $"下载中断（第 {attempt}/{DownloadMaxAttempts} 次，已下载 {resumed} 字节）：{ex.Message}";
                PersistAndRaise(record);
                var delayMs = RetryBaseDelayMs * (int)Math.Pow(3, attempt - 1);
                _logger.LogWarning(
                    ex, "文件 {Name} 第 {Attempt}/{Max} 次下载失败（已下载 {Bytes} 字节），{Delay}ms 后续传重试",
                    record.FileName, attempt, DownloadMaxAttempts, resumed, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 永久失败（4xx 直链失效、超大小上限、磁盘策略）或重试用尽：清理半成品并落 Failed。
                // 重试用尽但失败仍属瞬时类 → FailureRetriable=true，由调用方投递重试队列稍后再试。
                record.AttemptCount++;
                record.FailureRetriable = IsTransientDownloadFailure(ex);
                TryDelete(partialPath);
                return Fail(record, BuildDownloadErrorMessage(ex));
            }
        }
    }

    /// <summary>单次下载：续传（Range）→ 流式落盘 + 增量 MD5 → 完整性校验 → MD5 去重 → 归档。</summary>
    private async Task<FileRecord> DownloadOnceAsync(
        FileRecord record, string safeName, string url, FileSettings settings, string root, string partialPath,
        long maxFileBytes, int attempt, CancellationToken ct)
    {
        // 断点：半成品已写入的字节数（同时是续传请求的 Range 起点与 MD5 前缀种子的长度）
        var resumeFrom = PartialLength(partialPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }

        using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // 服务器不支持续传（对 Range 请求回 200 完整内容）→ 本次从头发起
        if (resumeFrom > 0 && resp.StatusCode == HttpStatusCode.OK)
        {
            _logger.LogInformation(
                "文件 {Name} 的服务器未按续传应答（HTTP 200），本次从头发起下载", record.FileName);
            resumeFrom = 0;
        }

        // 断点越界（416：源文件被替换/缩小）：丢弃半成品，按可重试失败处理，下一拍从头发起
        if (resumeFrom > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(partialPath);
            throw new IOException(
                $"断点续传被服务端拒绝（HTTP 416，本地半成品 {resumeFrom} 字节），下次从头发起");
        }

        resp.EnsureSuccessStatusCode();

        // 完整文件大小：续传响应取 Content-Range 的总长，完整响应取 Content-Length
        var expectedTotal = resp.StatusCode == HttpStatusCode.PartialContent
            ? resp.Content.Headers.ContentRange?.Length
            : resp.Content.Headers.ContentLength;

        // 单文件大小上限：已知完整大小时提前拒绝，避免无谓下载
        if (expectedTotal is > 0 && expectedTotal.Value > maxFileBytes)
        {
            throw new PermanentDownloadException(
                $"文件大小 {expectedTotal} 字节超过单文件上限 {settings.MaxFileSizeMb} MB");
        }

        // 磁盘占用上限预检（含清理策略；拒绝时内部已置 Failed 并告警）
        var remainingBytes = expectedTotal is > 0 ? Math.Max(0, expectedTotal.Value - resumeFrom) : 0;
        if (!EnsureDiskBudget(root, settings, remainingBytes, record))
        {
            throw new PermanentDownloadException(record.LastError ?? "磁盘占用已达上限");
        }

        record.Status = FileStatus.Downloading;
        RaiseUpdated(record);
        var (size, md5) = await DownloadToPartialAsync(
            resp.Content, partialPath, resumeFrom, maxFileBytes, settings, ct).ConfigureAwait(false);
        record.Size = size;

        if (expectedTotal is > 0 && size != expectedTotal.Value)
        {
            // 服务端给了完整长度就必须对齐；提前结束按可续传失败处理
            throw new IOException($"下载不完整：已收到 {size} / {expectedTotal.Value} 字节");
        }

        record.Status = FileStatus.Verifying;
        RaiseUpdated(record);
        record.Md5 = md5;

        // MD5 去重：删除重复文件，仅保留原文件并记日志
        if (settings.Md5DedupEnabled && FindArchivedByMd5(md5) is { } original)
        {
            TryDelete(partialPath);
            record.Status = FileStatus.Duplicate;
            record.CompletedAt = DateTimeOffset.Now;
            PersistAndRaise(record);
            _logger.LogInformation(
                "文件 {Name} 与已归档文件 {Existing} 内容相同（MD5 {Md5}），重复文件已删除",
                record.FileName, original.ArchivedRelativePath, md5);
            return record;
        }

        // 归档：下载文件/<学科>/<yyyy-MM-dd>/<文件名>（移动前做防穿越终检）
        var relative = ArchiveFile(
            root, partialPath, settings.GroupBySubject ? UnfiledSubject : "", record.CreatedAt.LocalDateTime, safeName);
        record.ArchivedRelativePath = relative;
        record.Status = FileStatus.Archived;
        record.CompletedAt = DateTimeOffset.Now;
        record.FailureRetriable = false; // 本次已成功：清掉历史失败的可重试标记
        PersistAndRaise(record);
        _logger.LogInformation(
            "文件 {Name} 已归档至 {Path}（{Size} 字节，MD5 {Md5}，本次下载尝试次数 {Attempt}）",
            record.FileName, relative, size, md5, attempt);
        return record;
    }

    /// <summary>瞬时失败判定：网络抖动/连接被断/停滞/5xx 可重试；4xx（直链失效）与策略性拒绝不重试。</summary>
    private static bool IsTransientDownloadFailure(Exception ex) => ex switch
    {
        PermanentDownloadException => false,
        HttpRequestException http => http.StatusCode is null
            || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)http.StatusCode!.Value >= 500,
        IOException => true,
        OperationCanceledException => true, // 读取停滞上限触发（宿主取消在上层单独处理，不会走到这里）
        _ => false
    };

    /// <summary>失败文案：直链被拒（401/403/404/410）直指「已过期/被拒绝」并给出可操作建议。</summary>
    private static string BuildDownloadErrorMessage(Exception ex) => ex switch
    {
        PermanentDownloadException => ex.Message,
        HttpRequestException
        {
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Gone
        } http => $"下载失败：直链被拒绝或已过期（HTTP {(int)http.StatusCode!.Value}），可重新入队重试",
        _ => $"下载失败：{ex.Message}"
    };

    /// <summary>半成品文件当前字节数（不存在/不可访问按 0 = 从头下载）。</summary>
    private static long PartialLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>不可重试的下载失败（大小上限/磁盘策略）：不进重试退避，直接落 Failed。</summary>
    private sealed class PermanentDownloadException(string message) : Exception(message);

    /// <summary>
    /// 流式下载到半成品文件并同步计算 MD5（支持续传：从 <paramref name="resumeFrom"/> 字节继续，
    /// MD5 先用已有字节做前缀种子）；单次读取停滞超过 <see cref="DownloadStallTimeout"/> 视为连接僵死。
    /// 支持简易限速与大小上限强制执行。
    /// </summary>
    private static async Task<(long Size, string Md5)> DownloadToPartialAsync(
        HttpContent content, string partialPath, long resumeFrom, long maxFileBytes, FileSettings settings,
        CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var md5 = MD5.Create();

        // 续传：先把半成品里已有的字节喂给 MD5，保证最终哈希覆盖完整文件
        if (resumeFrom > 0)
        {
            await SeedMd5FromFileAsync(md5, partialPath, resumeFrom, ct).ConfigureAwait(false);
        }

        await using var target = new FileStream(
            partialPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        var speedLimitKbps = settings.SpeedLimitKbps;
        var total = resumeFrom;
        while (true)
        {
            int read;
            // 逐次读取设停滞上限：超过该时长没有新字节 → 连接僵死（保留半成品，按可重试失败处理）
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readCts.CancelAfter(DownloadStallTimeout);
                try
                {
                    read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException(
                        $"读取停滞超过 {DownloadStallTimeout.TotalSeconds:0} 秒（连接僵死或网络中断）");
                }
            }

            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > maxFileBytes)
            {
                throw new PermanentDownloadException($"文件大小超过单文件上限（{settings.MaxFileSizeMb} MB）");
            }

            md5.TransformBlock(buffer, 0, read, null, 0);
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

            if (speedLimitKbps > 0)
            {
                // 简易限速：按本批字节数换算期望耗时，不足则等待
                var delayMs = (int)(read * 1000d / (speedLimitKbps * 1024d));
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }
        }

        md5.TransformFinalBlock([], 0, 0);
        await target.FlushAsync(ct).ConfigureAwait(false);
        return (total, Convert.ToHexString(md5.Hash ?? []).ToLowerInvariant());
    }

    /// <summary>把半成品文件已有字节喂给 MD5（续传时保证哈希覆盖完整文件）。</summary>
    private static async Task SeedMd5FromFileAsync(MD5 md5, string path, long length, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException($"半成品文件不完整：期望 {length} 字节，实际读到 {length - remaining}");
            }

            md5.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }
    }

    /// <summary>移动临时文件到 归档目录；最终路径必须位于下载根目录内（防穿越）。</summary>
    private static string ArchiveFile(string root, string tempPath, string subject, DateTimeOffset archivedAt, string safeName)
    {
        var date = archivedAt.LocalDateTime.ToString("yyyy-MM-dd");
        var dir = string.IsNullOrEmpty(subject) ? Path.Combine(root, date) : Path.Combine(root, subject, date);
        Directory.CreateDirectory(dir);

        var target = Path.GetFullPath(UniquePath(Path.Combine(dir, safeName)));
        if (!IsInsideRoot(root, target))
        {
            throw new InvalidOperationException($"归档路径越界：{Path.GetRelativePath(root, target)}");
        }

        File.Move(tempPath, target);
        return Path.GetRelativePath(root, target);
    }

    // ============ 磁盘占用上限 ============

    /// <summary>
    /// 磁盘预算检查。策略 0（停止接收）：超限拒绝新文件并告警；
    /// 策略 1（清理最旧）：删除最旧文件直到腾出空间，仍不足则拒绝。
    /// </summary>
    private bool EnsureDiskBudget(string root, FileSettings settings, long incomingBytes, FileRecord record)
    {
        var limitBytes = settings.MaxDiskUsageMb <= 0
            ? long.MaxValue
            : settings.MaxDiskUsageMb * 1024L * 1024L;
        if (limitBytes == long.MaxValue)
        {
            return true;
        }

        var usage = GetDiskUsageBytes(root);
        if (usage + incomingBytes <= limitBytes)
        {
            return true;
        }

        if (settings.CleanupPolicy == 1)
        {
            var freed = CleanupOldest(root, limitBytes - incomingBytes);
            usage = GetDiskUsageBytes(root);
            if (usage + incomingBytes <= limitBytes)
            {
                _logger.LogWarning(
                    "磁盘占用超上限（{Limit} 字节），已按清理策略删除最旧文件，释放 {Freed} 字节",
                    limitBytes, freed);
                return true;
            }
        }

        record.Status = FileStatus.Failed;
        record.LastError = $"磁盘占用已达上限（{settings.MaxDiskUsageMb} MB，清理策略 {settings.CleanupPolicy}），拒绝接收新文件";
        PersistAndRaise(record);
        _logger.LogWarning(
            "磁盘占用 {Usage} 字节超上限 {Limit} 字节（清理策略 {Policy}），拒绝新文件 {Name}",
            usage, limitBytes, settings.CleanupPolicy, record.FileName);
        return false;
    }

    /// <summary>按最后写入时间从旧到新删除文件（跳过下载中的临时文件），直到占用不超过目标值。</summary>
    private long CleanupOldest(string root, long targetUsageBytes)
    {
        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        long freed = 0;
        var usage = GetDiskUsageBytes(root);
        foreach (var file in candidates)
        {
            if (usage - freed <= targetUsageBytes)
            {
                break;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                freed += length;
                MarkRecordDeleted(root, file.FullName);
                _logger.LogInformation("清理策略：已删除最旧文件 {File}（{Length} 字节）", file.FullName, length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理策略：删除文件失败 {File}", file.FullName);
            }
        }

        Save();
        return freed;
    }

    /// <summary>被清理删除的归档文件对应记录标记为 Failed（与磁盘实际状态保持一致）。</summary>
    private void MarkRecordDeleted(string root, string deletedFullPath)
    {
        lock (_lock)
        {
            foreach (var record in _records)
            {
                if (record.Status != FileStatus.Archived || string.IsNullOrEmpty(record.ArchivedRelativePath))
                {
                    continue;
                }

                var absolute = Path.GetFullPath(Path.Combine(root, record.ArchivedRelativePath));
                if (string.Equals(absolute, deletedFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    record.Status = FileStatus.Failed;
                    record.LastError = "已因磁盘上限被清理策略删除";
                }
            }
        }
    }

    private static long GetDiskUsageBytes(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // 文件正被其他线程操作，跳过
            }
        }

        return total;
    }

    // ============ 路径安全 ============

    /// <summary>
    /// 文件名/目录名安全校验：拒绝空名、路径分隔符、<c>..</c>、非法字符、
    /// 超长名（MaxFileNameLength）与 Windows 保留名（CON/PRN/AUX/NUL/COM1-9/LPT1-9，含带扩展名形式）。
    /// </summary>
    internal static bool TryValidateFileName(
        string? fileName, int maxLength, [NotNullWhen(true)] out string safeName, [NotNullWhen(false)] out string? error)
    {
        safeName = "";
        error = null;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = "文件名为空";
            return false;
        }

        var name = fileName.Trim();
        if (name.Contains('/') || name.Contains('\\'))
        {
            error = "文件名包含路径分隔符";
            return false;
        }

        if (name.Contains(".."))
        {
            error = "文件名包含路径穿越片段（..）";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "文件名包含非法字符";
            return false;
        }

        if (name.Length > Math.Max(1, maxLength))
        {
            error = $"文件名长度 {name.Length} 超过上限 {maxLength}";
            return false;
        }

        var baseName = name.Split('.')[0];
        if (ReservedNames.Contains(baseName))
        {
            error = $"文件名使用了 Windows 保留名（{baseName}）";
            return false;
        }

        // Windows 不允许结尾空格/点
        name = name.TrimEnd(' ', '.');
        if (name.Length == 0)
        {
            error = "文件名无效";
            return false;
        }

        safeName = name;
        return true;
    }

    /// <summary>防穿越终检：绝对路径必须位于下载根目录之内。</summary>
    private static bool IsInsideRoot(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>目标文件名冲突时按 name(n).ext 顺延（同内容已由 MD5 去重拦截，此处为不同内容同名场景）。</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"路径无目录：{path}");
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private string GetDownloadRoot()
    {
        var configured = _provider.GetSettings().DownloadRoot;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "下载文件";
        }

        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_provider.DataDirectory, configured);
        Directory.CreateDirectory(root);
        return root;
    }

    // ============ 记录管理与持久化 ============

    /// <summary>
    /// 入队：同 MessageId+FileName 的 Failed 记录可复用（可重入重试，AttemptCount 累计）。
    /// MemberOpenId/GroupOpenId 随记录持久化（成员绑定回溯归因用；复用记录时补写空缺值）。
    /// </summary>
    private FileRecord BeginEnqueue(string messageId, string fileName, string? memberOpenId, string? groupOpenId)
    {
        lock (_lock)
        {
            var existing = _records.LastOrDefault(r =>
                r.MessageId == messageId && r.FileName == fileName && r.Status == FileStatus.Failed);
            if (existing is not null)
            {
                existing.Status = FileStatus.Pending;
                existing.LastError = null;
                existing.CompletedAt = null;
                existing.MemberOpenId = string.IsNullOrEmpty(existing.MemberOpenId)
                    ? memberOpenId ?? ""
                    : existing.MemberOpenId;
                existing.GroupOpenId = string.IsNullOrEmpty(existing.GroupOpenId)
                    ? groupOpenId ?? ""
                    : existing.GroupOpenId;
                Save();
                return existing;
            }

            var record = new FileRecord
            {
                MessageId = messageId,
                FileName = fileName,
                MemberOpenId = memberOpenId ?? "",
                GroupOpenId = groupOpenId ?? "",
                Status = FileStatus.Pending,
                CreatedAt = DateTimeOffset.Now
            };
            _records.Add(record);
            Save();
            return record;
        }
    }

    private FileRecord Fail(FileRecord record, string error, bool countAttempt = false)
    {
        record.Status = FileStatus.Failed;
        record.LastError = error;
        if (countAttempt)
        {
            record.AttemptCount++;
        }

        PersistAndRaise(record);
        _logger.LogWarning("文件 {Name} 处理失败：{Error}", record.FileName, error);
        return record;
    }

    private FileRecord? FindById(Guid id)
    {
        lock (_lock)
        {
            return _records.FirstOrDefault(r => r.Id == id);
        }
    }

    private FileRecord? FindArchivedByMd5(string md5)
    {
        lock (_lock)
        {
            return _records.FirstOrDefault(r => r.Status == FileStatus.Archived && r.Md5 == md5);
        }
    }

    private void PersistAndRaise(FileRecord record)
    {
        Save();
        RaiseUpdated(record);
    }

    private void RaiseUpdated(FileRecord record)
    {
        FileUpdated?.Invoke(this, record);
    }

    /// <summary>崩溃安全持久化：临时文件 + 原子替换（files.json）。</summary>
    private void Save()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_records, JsonOptions);
            }

            var tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写失败不中断管道（内存态仍在；下次成功写入会覆盖）
            _logger.LogWarning(ex, "files.json 写入失败（内存态不受影响）");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            var json = File.ReadAllText(_storePath);
            var records = JsonSerializer.Deserialize<List<FileRecord>>(json) ?? [];
            lock (_lock)
            {
                _records = records;
            }

            _logger.LogDebug("文件记录已加载 {Count} 条", _records.Count);
        }
        catch (Exception ex)
        {
            // 损坏时保留损坏文件并空库启动
            _logger.LogWarning(ex, "files.json 加载失败，将以空库启动");
            lock (_lock)
            {
                _records = [];
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败忽略（下载根目录内残留临时文件不影响归档一致性）
        }
    }
}
