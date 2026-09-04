using ClassIng.Plugin.Services.MessageAccess;
using ClassIng.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ClassIng.IngestConsole;

/// <summary>
/// 模块 1 验收自检工具：连接真实 QQ 官方机器人平台，把收到的群消息打印为结构化日志。
/// 用法：
///   dotnet run --project tools/ClassIng.IngestConsole -- --appid 123456 --secret YOUR_SECRET [--groupopenid AACDxxx]
/// 或用环境变量：CLASSING_APPID / CLASSING_SECRET。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? appId = null, secret = null, groupOpenId = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--appid":
                    appId = args[i + 1];
                    break;
                case "--secret":
                    secret = args[i + 1];
                    break;
                case "--groupopenid":
                    groupOpenId = args[i + 1];
                    break;
            }
        }

        appId ??= Environment.GetEnvironmentVariable("CLASSING_APPID");
        secret ??= Environment.GetEnvironmentVariable("CLASSING_SECRET");

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(secret))
        {
            Console.WriteLine("缺少凭据。用法：");
            Console.WriteLine("  dotnet run --project tools/ClassIng.IngestConsole -- --appid <AppID> --secret <AppSecret> [--groupopenid <群OpenID>]");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss.fff ";
                })
                .SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("ClassIng.IngestConsole");

        var settings = new ConnectionSettings
        {
            AppId = appId,
            AppSecretProtected = secret, // 控制台工具内直接使用明文（仅本地自检，不持久化）
            GroupWhitelist = groupOpenId is null ? [] : [groupOpenId]
        };

        var dataDir = Path.Combine(Path.GetTempPath(), "classing-ingest-console");
        var service = new MessageIngestService(
            new IngestOptionsProvider
            {
                GetSettings = () => settings,
                DataDirectory = dataDir
            },
            logger);

        service.MessageReceived += (_, m) =>
        {
            // 结构化输出：消息已通过幂等与白名单检查
            logger.LogInformation(
                "[消息] id={MessageId} group={GroupOpenId} sender={MemberOpenId} 段数={Segments} 快照字节={SnapshotBytes}",
                m.MessageId, m.GroupOpenId, m.MemberOpenId, m.Segments.Count, m.RawJsonSnapshot.Length);
            foreach (var seg in m.Segments)
            {
                logger.LogInformation("  段[{Type}] 文本={Text} 文件名={FileName} URL存在={HasUrl}",
                    seg.Type, seg.Text, seg.FileName ?? "-", !string.IsNullOrEmpty(seg.Url));
            }
        };
        service.StatusChanged += (_, s) => logger.LogInformation("[连接] {Status}", s);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        logger.LogInformation("启动接入服务（Ctrl+C 退出）… AppId={AppId} 白名单={Whitelist}",
            appId, groupOpenId ?? "(全部群，建议配置白名单)");
        await service.StartAsync(cts.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await service.StopAsync();
        logger.LogInformation("已退出。幂等记录数：可查看 {DataDir}\\seen-message-ids.json", dataDir);
        return 0;
    }
}
