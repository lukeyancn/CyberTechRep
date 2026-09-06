using System.Text.Json;
using CyberTechRep.Plugin.Services.Maintenance;
using CyberTechRep.Shared.Models;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>模块 7：SettingsService 单元测试（加载/保存/导入导出脱敏/恢复默认/SchemaVersion 校验）。</summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _dir;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "classing-tests", "settings", Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响测试
        }
    }

    private SettingsService CreateService() => new(_dir);

    [Fact]
    public async Task Save_ThenReload_RoundTripsAllGroups()
    {
        var svc = CreateService();
        svc.Current.Connection.AppId = "app-123";
        svc.Current.Overlays.Notice.X = 123.5;
        svc.Current.Files.MaxDiskUsageMb = 4096;
        svc.Current.Maintenance.LogLevel = "Debug";
        await svc.SaveAsync();

        var reloaded = CreateService();
        Assert.Equal("app-123", reloaded.Current.Connection.AppId);
        Assert.Equal(123.5, reloaded.Current.Overlays.Notice.X);
        Assert.Equal(4096, reloaded.Current.Files.MaxDiskUsageMb);
        Assert.Equal("Debug", reloaded.Current.Maintenance.LogLevel);
    }

    [Fact]
    public async Task Save_WritesFileAtomically_NoTempLeftBehind()
    {
        var svc = CreateService();
        await svc.SaveAsync();

        Assert.True(File.Exists(svc.ConfigFilePath));
        Assert.False(File.Exists(svc.ConfigFilePath + ".tmp"));
    }

    [Fact]
    public async Task Save_RaisesSettingsChanged()
    {
        var svc = CreateService();
        var raised = 0;
        svc.SettingsChanged += (_, _) => raised++;

        await svc.SaveAsync();
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Export_DoesNotContainSecrets()
    {
        var svc = CreateService();
        svc.Current.Connection.AppSecretProtected = svc.Protect("super-secret-appsecret");
        svc.Current.Classification.CloudApiKeyProtected = svc.Protect("super-secret-apikey");
        await svc.SaveAsync();

        var exported = await svc.ExportAsync();

        Assert.DoesNotContain("super-secret-appsecret", exported);
        Assert.DoesNotContain("super-secret-apikey", exported);
        var parsed = JsonSerializer.Deserialize<AppSettings>(exported);
        Assert.NotNull(parsed);
        Assert.Equal("", parsed!.Connection.AppSecretProtected);
        Assert.Equal("", parsed.Classification.CloudApiKeyProtected);
    }

    [Fact]
    public async Task Import_KeepsLocalSecretValues()
    {
        var svc = CreateService();
        var localSecret = svc.Protect("local-appsecret");
        var localApiKey = svc.Protect("local-apikey");
        svc.Current.Connection.AppSecretProtected = localSecret;
        svc.Current.Classification.CloudApiKeyProtected = localApiKey;

        var other = CreateService();
        other.Current.Connection.AppId = "imported-app";
        other.Current.Connection.AppSecretProtected = other.Protect("imported-secret");
        other.Current.Classification.CloudApiKeyProtected = other.Protect("imported-key");
        var importJson = await other.ExportAsync();

        await svc.ImportAsync(importJson);

        Assert.Equal("imported-app", svc.Current.Connection.AppId);
        // Secret 字段保留本地值，不被导入覆盖
        Assert.Equal(localSecret, svc.Current.Connection.AppSecretProtected);
        Assert.Equal(localApiKey, svc.Current.Classification.CloudApiKeyProtected);
        Assert.Equal("local-appsecret", svc.Unprotect(svc.Current.Connection.AppSecretProtected));
    }

    [Fact]
    public async Task Import_WithWrongSchemaVersion_Throws()
    {
        var svc = CreateService();
        const string json = """
            { "SchemaVersion": 99, "Connection": {}, "Classification": {}, "Overlays": {}, "Files": {}, "Maintenance": {} }
            """;

        await Assert.ThrowsAsync<FormatException>(() => svc.ImportAsync(json));
    }

    [Fact]
    public async Task Import_WithInvalidJson_ThrowsFormatException()
    {
        var svc = CreateService();
        await Assert.ThrowsAsync<FormatException>(() => svc.ImportAsync("not-a-json"));
    }

    [Fact]
    public async Task ResetToDefaults_RestoresDefaults_AndPersists()
    {
        var svc = CreateService();
        svc.Current.Connection.AppId = "changed";
        svc.Current.Maintenance.LogLevel = "Trace";
        await svc.SaveAsync();

        await svc.ResetToDefaultsAsync();

        Assert.Equal("", svc.Current.Connection.AppId);
        Assert.Equal("Info", svc.Current.Maintenance.LogLevel);
        var reloaded = CreateService();
        Assert.Equal("Info", reloaded.Current.Maintenance.LogLevel);
    }

    [Fact]
    public void Load_CorruptedFile_FallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ corrupted json !!!");

        var svc = CreateService();
        Assert.Equal("Info", svc.Current.Maintenance.LogLevel);
        Assert.Equal(1, svc.Current.SchemaVersion);
    }

    [Fact]
    public void ProtectUnprotect_RoundTrips()
    {
        var svc = CreateService();
        var protectedValue = svc.Protect("plain-text-secret");
        Assert.NotEqual("plain-text-secret", protectedValue);
        Assert.Equal("plain-text-secret", svc.Unprotect(protectedValue));
        Assert.Equal("", svc.Unprotect(""));
        Assert.Equal("", svc.Protect(""));
    }

    [Fact]
    public async Task Import_RaisesSettingsChanged_WithMergedInstance()
    {
        var svc = CreateService();
        var raised = 0;
        AppSettings? received = null;
        svc.SettingsChanged += (_, s) => { raised++; received = s; };

        svc.Current.Connection.GroupWhitelist = ["group-a"];
        await svc.SaveAsync();
        svc.Current.Connection.GroupWhitelist = ["group-b"];
        var json = await svc.ExportAsync();

        await svc.ImportAsync(json);

        Assert.Equal(2, raised);
        Assert.NotNull(received);
        Assert.Equal(["group-b"], received!.Connection.GroupWhitelist);
    }
}
