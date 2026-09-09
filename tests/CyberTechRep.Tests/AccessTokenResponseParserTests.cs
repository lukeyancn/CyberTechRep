using System.Text.Json;
using CyberTechRep.Plugin.Services.MessageAccess;
using Xunit;

namespace CyberTechRep.Tests;

/// <summary>
/// AccessToken 响应兼容解析器单元测试：官方标准结构 / 常见变体（驼峰、嵌套、双编码、数字值）/
/// 失败诊断（空体、非法 JSON、数组根、协议端错误码）/ 脱敏片段（敏感字段、裸长串、截断）。
/// </summary>
public sealed class AccessTokenResponseParserTests
{
    // ============ 成功路径 ============

    [Fact]
    public void Parse_StandardBody_ReturnsTokenAndExpires()
    {
        var result = AccessTokenResponseParser.Parse("""{"access_token":"abc","expires_in":7200}""");

        Assert.True(result.Success);
        Assert.Equal("abc", result.Token);
        Assert.Equal(7200, result.ExpiresIn);
        Assert.Equal("", result.Diagnostic);
    }

    [Fact]
    public void Parse_CamelCaseFields_AreAccepted()
    {
        var result = AccessTokenResponseParser.Parse("""{"accessToken":"camel-token","expiresIn":3600}""");

        Assert.True(result.Success);
        Assert.Equal("camel-token", result.Token);
        Assert.Equal(3600, result.ExpiresIn);
    }

    [Fact]
    public void Parse_NestedDataContainer_IsAccepted()
    {
        var result = AccessTokenResponseParser.Parse(
            """{"errcode":0,"data":{"access_token":"nested-token","expires_in":1800}}""");

        Assert.True(result.Success);
        Assert.Equal("nested-token", result.Token);
        Assert.Equal(1800, result.ExpiresIn);
    }

    [Fact]
    public void Parse_NestedResultContainer_WithTokenFieldName_IsAccepted()
    {
        var result = AccessTokenResponseParser.Parse("""{"result":{"token":"result-token"}}""");

        Assert.True(result.Success);
        Assert.Equal("result-token", result.Token);
        Assert.Equal(7200, result.ExpiresIn); // 缺省有效期
    }

    [Fact]
    public void Parse_TopLevelTokenFieldName_IsAccepted()
    {
        var result = AccessTokenResponseParser.Parse("""{"token":"plain-token","expires_in":60}""");

        Assert.True(result.Success);
        Assert.Equal("plain-token", result.Token);
        Assert.Equal(60, result.ExpiresIn);
    }

    [Fact]
    public void Parse_DoubleEncodedJsonStringBody_IsUnwrapped()
    {
        var inner = """{"access_token":"double-token","expires_in":60}""";
        var body = JsonSerializer.Serialize(inner);

        var result = AccessTokenResponseParser.Parse(body);

        Assert.True(result.Success);
        Assert.Equal("double-token", result.Token);
        Assert.Equal(60, result.ExpiresIn);
    }

    [Fact]
    public void Parse_NestedContainerAsJsonString_IsUnwrapped()
    {
        var result = AccessTokenResponseParser.Parse("""{"data":"{\"access_token\":\"nested-string\"}"}""");

        Assert.True(result.Success);
        Assert.Equal("nested-string", result.Token);
    }

    [Fact]
    public void Parse_TokenValueIsNestedJson_IsUnwrapped()
    {
        var result = AccessTokenResponseParser.Parse("""{"access_token":"{\"access_token\":\"inner-token\"}"}""");

        Assert.True(result.Success);
        Assert.Equal("inner-token", result.Token);
    }

    [Fact]
    public void Parse_NumericTokenValue_IsConvertedToRawText()
    {
        var result = AccessTokenResponseParser.Parse("""{"access_token":123456789}""");

        Assert.True(result.Success);
        Assert.Equal("123456789", result.Token);
        Assert.Equal(7200, result.ExpiresIn);
    }

    [Fact]
    public void Parse_ExpiresInAsString_IsParsed()
    {
        var result = AccessTokenResponseParser.Parse("""{"access_token":"t","expires_in":"900"}""");

        Assert.True(result.Success);
        Assert.Equal(900, result.ExpiresIn);
    }

    [Fact]
    public void Parse_EmptyTokenField_FallsBackToNextCandidate()
    {
        var result = AccessTokenResponseParser.Parse("""{"access_token":"","token":"fallback-token"}""");

        Assert.True(result.Success);
        Assert.Equal("fallback-token", result.Token);
    }

    [Fact]
    public void Parse_MissingExpiresIn_DefaultsTo7200()
    {
        var result = AccessTokenResponseParser.Parse("""{"access_token":"t"}""");

        Assert.True(result.Success);
        Assert.Equal(7200, result.ExpiresIn);
    }

    // ============ 失败路径 ============

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyBody_FailsWithEmptyBodyDiagnostic(string? body)
    {
        var result = AccessTokenResponseParser.Parse(body);

        Assert.False(result.Success);
        Assert.Equal("", result.Token);
        Assert.Equal(7200, result.ExpiresIn);
        Assert.Contains("响应体为空", result.Diagnostic);
    }

    [Fact]
    public void Parse_InvalidJson_FailsWithParseErrorAndSnippet()
    {
        var result = AccessTokenResponseParser.Parse("{ not json");

        Assert.False(result.Success);
        Assert.Contains("不是合法 JSON", result.Diagnostic);
        Assert.Contains("原始响应片段", result.Diagnostic);
    }

    [Fact]
    public void Parse_ArrayRoot_FailsWithValueKindDiagnostic()
    {
        var result = AccessTokenResponseParser.Parse("[1,2,3]");

        Assert.False(result.Success);
        Assert.Contains("根节点是 Array 而非对象", result.Diagnostic);
    }

    [Fact]
    public void Parse_ErrorResponseWithCodeAndMessage_IncludesProtocolError()
    {
        var result = AccessTokenResponseParser.Parse("""{"code":100007,"message":"invalid appid or secret"}""");

        Assert.False(result.Success);
        Assert.Equal("", result.Token);
        Assert.Contains("未找到 access_token", result.Diagnostic);
        Assert.Contains("code=100007", result.Diagnostic);
        Assert.Contains("message=invalid appid or secret", result.Diagnostic);
    }

    [Fact]
    public void Parse_ObjectWithoutToken_FailsWithHint()
    {
        var result = AccessTokenResponseParser.Parse("""{"foo":"bar"}""");

        Assert.False(result.Success);
        Assert.Contains("未找到 access_token", result.Diagnostic);
        Assert.Contains("顶层与 data/result 嵌套", result.Diagnostic);
    }

    // ============ SanitizeSnippet 脱敏 ============

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitizeSnippet_EmptyInput_ReturnsPlaceholder(string? body)
    {
        Assert.Equal("(空)", AccessTokenResponseParser.SanitizeSnippet(body));
    }

    [Fact]
    public void SanitizeSnippet_SensitiveFieldValues_AreReplaced()
    {
        var body = """{"access_token":"secret-value","client_secret":"another-secret","note":"keep-me"}""";

        var snippet = AccessTokenResponseParser.SanitizeSnippet(body);

        Assert.Contains("\"access_token\":\"***\"", snippet);
        Assert.Contains("\"client_secret\":\"***\"", snippet);
        Assert.Contains("keep-me", snippet);
        Assert.DoesNotContain("secret-value", snippet);
        Assert.DoesNotContain("another-secret", snippet);
    }

    [Fact]
    public void SanitizeSnippet_LongBareTokenLikeString_KeepsFirstAndLastFourChars()
    {
        var snippet = AccessTokenResponseParser.SanitizeSnippet("abcdefghijklmnopqrstuvwxyz0123456789");

        Assert.Equal("abcd***6789", snippet);
    }

    [Fact]
    public void SanitizeSnippet_ShortTokenLikeString_IsUnchanged()
    {
        // 24~31 字符的长串按实现不打码（阈值 >= 32），此处固定文档口径
        var text = new string('a', 31);

        Assert.Equal(text, AccessTokenResponseParser.SanitizeSnippet(text));
    }

    [Fact]
    public void SanitizeSnippet_LongText_IsTruncatedWithSuffix()
    {
        var body = string.Join(" ", Enumerable.Repeat("abcdefgh", 100));

        var snippet = AccessTokenResponseParser.SanitizeSnippet(body);

        Assert.Equal(body[..AccessTokenResponseParser.SnippetMaxLength] + "…(已截断)", snippet);
        Assert.EndsWith("(已截断)", snippet);
    }

    [Fact]
    public void SanitizeSnippet_Newlines_AreFlattened()
    {
        Assert.Equal("line1  line2", AccessTokenResponseParser.SanitizeSnippet("line1\r\nline2"));
    }

    [Fact]
    public void SanitizeSnippet_AndParseDiagnostic_NeverContainSecret()
    {
        const string secret = "leaked-secret-value-abcdefghijklmnop";
        var body = "{\"access_token\":\"" + secret + "\",\"broken";

        var snippet = AccessTokenResponseParser.SanitizeSnippet(body);
        Assert.Contains("\"access_token\":\"***\"", snippet);
        Assert.DoesNotContain(secret, snippet);

        var result = AccessTokenResponseParser.Parse(body);
        Assert.False(result.Success);
        Assert.DoesNotContain(secret, result.Diagnostic);
    }
}
