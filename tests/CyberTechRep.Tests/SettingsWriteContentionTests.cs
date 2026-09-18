using System.Runtime.Versioning;
using CyberTechRep.Plugin.Services.Maintenance;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// 设置写盘在「目标文件被其他句柄占用」时的健壮性（Windows 文件共享语义）。
/// <para>
/// settings.json 的原子替换是「同目录临时文件 → File.Move(overwrite:true)」。实测（见本文件用例的构造）：
/// 只要目标文件被任何其他句柄打开着（哪怕对方以 FileShare.ReadWrite | FileShare.Delete 打开），
/// File.Move 的覆盖替换都会抛 <see cref="UnauthorizedAccessException"/>。
/// 真实场景对应：宿主启动加载设置、排错面板读取设置、杀毒/备份软件扫描、用户在编辑器里打开 settings.json。
/// </para>
/// <para>
/// 因此写盘的重试必须有<b>有限期限</b>（而不是固定几次）：否则一次偶发占用就会静默丢掉整次保存，
/// 用户在设置页改完看到「已保存」，重启却回退到旧值。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsWriteContentionTests : IDisposable
{
    private readonly string _dir;

    public SettingsWriteContentionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "settings-contention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task Save_WhileAnotherHandleHoldsFile_RetriesUntilReleased_AndPersists()
    {
        var svc = new SettingsService(_dir);
        await svc.SaveAsync(); // 先落一份 settings.json

        var path = Path.Combine(_dir, "settings.json");

        // 占用方：持有文件句柄 600ms 后释放（对照：旧实现只重试 3 次 × 50ms = 150ms，整段占用期内必然全部失败）
        var holder = Task.Run(() =>
        {
            using var stream = File.OpenRead(path); // FileShare.Read：与生产 LoadOrDefault 一致
            _ = stream.ReadByte();
            Thread.Sleep(600);
        });

        await Task.Delay(100); // 确保占用方已拿到句柄

        svc.Current.Maintenance.LogLevel = "Debug";
        var error = await Record.ExceptionAsync(() => svc.SaveAsync());

        await holder;

        // 写入必须等到占用释放后落盘（而不是抛异常 / 静默丢弃）
        Assert.Null(error);
        svc.Current.Maintenance.LogLevel = "Debug";
        Assert.Equal("Debug", new SettingsService(_dir).Current.Maintenance.LogLevel);
    }
}
