namespace CyberTechRep.Plugin;

/// <summary>
/// 插件运行期全局信息（在插件 Initialize 时赋值；供设置页等无 DI 参数场景读取）。
/// </summary>
public static class PluginRuntime
{
    /// <summary>插件数据目录（配置、词表、重试队列等文件根目录）。</summary>
    public static string DataDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassIsland", "Plugins", "classisland.classing", "data");
}
