using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CyberTechRep.Plugin.Utils;

/// <summary>
/// 敏感信息加密存储（Windows DPAPI，CurrentUser 作用域）。
/// 用于 AppSecret / 云端 API Key 的本地持久化加密；密文绑定当前用户与机器。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretProtector
{
    private static readonly byte[] Entropy = "CyberTechRep.SecretProtector.v1"u8.ToArray();

    /// <summary>明文 → DPAPI 密文（Base64）。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return "";
        }

        var data = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>DPAPI 密文（Base64） → 明文；空串/损坏返回空串（不抛异常）。</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return "";
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedBase64);
            var data = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            // 兼容：历史明文值（首次升级场景），按原值返回
            return protectedBase64;
        }
        catch (CryptographicException)
        {
            return "";
        }
    }
}
