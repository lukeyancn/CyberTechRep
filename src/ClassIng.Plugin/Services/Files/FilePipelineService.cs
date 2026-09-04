using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using ClassIng.Shared.Abstractions;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassIng.Plugin.Services.Files;

/// <summary>
/// 模块 4：文件处理管道。
/// 原子下载（.tmp 临时文件 → MD5 校验 → 归档到 下载文件/&lt;学科&gt;/&lt;yyyy-MM-dd&gt;/&lt;文件名&gt;）、
/// MD5 去重、路径安全校验（拒绝穿越/非法字符/超长/Windows 保留名）、
/// 单文件大小上限、下载并发队列化（SemaphoreSlim）、磁盘占用上限与清理策略。
/// 失败记录 <see cref="FileStatus.Failed"/> + LastError；<see cref="EnqueueAsync"/> 可重入触发尽力重试
/// （持久化重试队列与重放 UI 属模块 8，本模块不做后台自动重试）。
/// HttpClient 可注入（测试用假 HttpMessageHandler）。
/// </summary>
public sealed class FilePipelineService : IFilePipelineService
{
    /// <summary>初始归档学科目录（学科识别完成后经 ReassignSubject 移动）。</summary>
    private const string UnfiledSubject = "未分类";

    private const string TempPrefix = ".download-";
    private const string TempSuffix = ".tmp";

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
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _logger = logger ?? NullLogger.Instance;
        _downloadSlots = new SemaphoreSlim(Math.Max(1, provider.GetSettings().DownloadConcurrency));
        Directory.CreateDirectory(provider.DataDirectory);
        _storePath = Path.Combine(provider.DataDirectory, "files.json");
        Load();
    }

    /// <inheritdoc />
    public async Task<FileRecord> EnqueueAsync(string messageId, string fileName, string? url, CancellationToken ct = default)
    {
        var record = BeginEnqueue(messageId ?? "", fileName ?? "");
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

    // ============ 下载与归档 ============

    private async Task<FileRecord> DownloadAndArchiveAsync(
        FileRecord record, string safeName, string url, FileSettings settings, CancellationToken ct)
    {
        var root = GetDownloadRoot();
        var tempPath = Path.Combine(root, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);
        var maxFileBytes = settings.MaxFileSizeMb <= 0
            ? long.MaxValue
            : settings.MaxFileSizeMb * 1024L * 1024L;

        record.Status = FileStatus.Downloading;
        RaiseUpdated(record);

        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var contentLength = resp.Content.Headers.ContentLength;

            // 单文件大小上限：已知 Content-Length 时提前拒绝，避免无谓下载
            if (contentLength is > 0 && contentLength.Value > maxFileBytes)
            {
                return Fail(record, $"文件大小 {contentLength} 字节超过单文件上限 {settings.MaxFileSizeMb} MB", countAttempt: true);
            }

            // 磁盘占用上限预检（含清理策略；拒绝时内部已置 Failed 并告警）
            if (!EnsureDiskBudget(root, settings, contentLength ?? 0, record))
            {
                return record;
            }

            // 原子下载：写临时文件 + 流式 MD5（内容长度未知时在流内强制执行大小上限）
            record.Status = FileStatus.Downloading;
            RaiseUpdated(record);
            var (size, md5) = await DownloadToTempAsync(resp.Content, tempPath, maxFileBytes, settings, ct);
            record.Size = size;

            record.Status = FileStatus.Verifying;
            RaiseUpdated(record);
            record.Md5 = md5;

            // MD5 去重：删除重复文件，仅保留原文件并记日志
            if (settings.Md5DedupEnabled && FindArchivedByMd5(md5) is { } original)
            {
                TryDelete(tempPath);
                record.Status = FileStatus.Duplicate;
                record.CompletedAt = DateTimeOffset.Now;
                PersistAndRaise(record);
                _logger.LogInformation(
                    "文件 {Name} 与已归档文件 {Existing} 内容相同（MD5 {Md5}），重复文件已删除",
                    record.FileName, original.ArchivedRelativePath, md5);
                return record;
            }

            // 归档：下载文件/<学科>/<yyyy-MM-dd>/<文件名>（移动前做防穿越终检）
            var relative = ArchiveFile(root, tempPath, settings.GroupBySubject ? UnfiledSubject : "", record.CreatedAt.LocalDateTime, safeName);
            record.ArchivedRelativePath = relative;
            record.Status = FileStatus.Archived;
            record.CompletedAt = DateTimeOffset.Now;
            PersistAndRaise(record);
            _logger.LogInformation("文件 {Name} 已归档至 {Path}（{Size} 字节，MD5 {Md5}）", record.FileName, relative, size, md5);
            return record;
        }
        catch (Exception ex)
        {
            // 失败：清理临时文件（无半成品归档），置 Failed + LastError，可重入重试
            TryDelete(tempPath);
            return Fail(record, $"下载失败：{ex.Message}", countAttempt: true);
        }
    }

    /// <summary>流式下载到临时文件并同步计算 MD5；支持简易限速与大小上限强制执行。</summary>
    private static async Task<(long Size, string Md5)> DownloadToTempAsync(
        HttpContent content, string tempPath, long maxFileBytes, FileSettings settings, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var md5 = MD5.Create();

        var buffer = new byte[81920];
        var speedLimitKbps = settings.SpeedLimitKbps;
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxFileBytes)
            {
                throw new InvalidOperationException($"文件大小超过单文件上限（{settings.MaxFileSizeMb} MB）");
            }

            md5.TransformBlock(buffer, 0, read, null, 0);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);

            if (speedLimitKbps > 0)
            {
                // 简易限速：按本批字节数换算期望耗时，不足则等待
                var delayMs = (int)(read * 1000d / (speedLimitKbps * 1024d));
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct);
                }
            }
        }

        md5.TransformFinalBlock([], 0, 0);
        await target.FlushAsync(ct);
        return (total, Convert.ToHexString(md5.Hash ?? []).ToLowerInvariant());
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

    /// <summary>入队：同 MessageId+FileName 的 Failed 记录可复用（可重入重试，AttemptCount 累计）。</summary>
    private FileRecord BeginEnqueue(string messageId, string fileName)
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
                Save();
                return existing;
            }

            var record = new FileRecord
            {
                MessageId = messageId,
                FileName = fileName,
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
