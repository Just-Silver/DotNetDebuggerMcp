using DotNetDebuggerMcp.Services;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// DB1 敏感脱敏助手 SensitiveValueRedactor 单元测试（纯内存，无 Collection 要求）。
/// 规则移植 microsoft/DebugMCP secretRedaction.ts 双模式全集（按名归一化 exact-match + 内容正则）。
/// 四类：名匹配 / 平凡不动 / 子串不误伤 / 内容匹配 + 表达式级。
/// </summary>
public sealed class SensitiveValueRedactorTests
{
    private const string Ph = SensitiveValueRedactor.Placeholder;

    // ---- 名匹配：归一化 exact-match（小写 + 去 _ - 空格）----

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("API_KEY")]
    [InlineData("api-key")]
    [InlineData("apiKey")]
    [InlineData("accessToken")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("connectionString")]
    [InlineData("CONNECTION_STRING")]
    [InlineData("connStr")]
    [InlineData("sessionToken")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("jwt")]
    public void Redact_SensitiveName_RedactsValue(string name)
    {
        var (text, redacted) = SensitiveValueRedactor.Redact(name, "\"some-value\"");
        Assert.Equal(Ph, text);
        Assert.True(redacted);
    }

    [Fact]
    public void Redact_ConnStringNotInSet_NotRedactedByName()
    {
        // 忠实移植 DebugMCP 全集：名单含 connstr/connectionstring，无 connstring（connString 归一化后不在名单）
        var (text, redacted) = SensitiveValueRedactor.Redact("connString", "\"some-value\"");
        Assert.Equal("\"some-value\"", text);
        Assert.False(redacted);
    }

    [Fact]
    public void IsSensitiveName_ExactFold()
    {
        Assert.True(SensitiveValueRedactor.IsSensitiveName("password"));
        Assert.True(SensitiveValueRedactor.IsSensitiveName("API_KEY"));
        Assert.True(SensitiveValueRedactor.IsSensitiveName("api-key"));
        Assert.True(SensitiveValueRedactor.IsSensitiveName("accessToken"));
        Assert.True(SensitiveValueRedactor.IsSensitiveName("connectionstring"));
        Assert.True(SensitiveValueRedactor.IsSensitiveName("bearer"));
        Assert.False(SensitiveValueRedactor.IsSensitiveName(""));
        Assert.False(SensitiveValueRedactor.IsSensitiveName("name"));
        Assert.False(SensitiveValueRedactor.IsSensitiveName("x-api-key")); // 不在全集内（非子串规则）
    }

    [Fact]
    public void IsSensitiveName_Null_ReturnsFalse()
    {
        // P1-4：上游接受 null/undefined；签名应防御，首行不得 NRE
        Assert.False(SensitiveValueRedactor.IsSensitiveName(null!));
    }

    [Theory]
    [InlineData("api\tkey")]   // P1-3：tab 与 \s 对齐
    [InlineData("api\nkey")]   // 换行
    [InlineData("api\rkey")]
    [InlineData("api key")]    // 普通空格（原本已支持，回归）
    public void IsSensitiveName_WhitespaceSeparators_Normalized(string name)
    {
        Assert.True(SensitiveValueRedactor.IsSensitiveName(name));
    }

    // ---- 平凡不动：null/空/平凡值原样返回且 Redacted==false（含敏感名 + trivial 值）----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("none")]
    [InlineData("null")]
    [InlineData("nil")]
    [InlineData("undefined")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("empty")]
    public void Redact_TrivialValue_Unchanged(string? value)
    {
        var (text, redacted) = SensitiveValueRedactor.Redact(null, value!);
        Assert.Equal(value, text);
        Assert.False(redacted);
    }

    [Fact]
    public void Redact_SensitiveNameWithTrivialValue_KeepsReadable()
    {
        // 「为什么我的 token 是 null」仍可调：trivial 优先于按名规则
        var (text, redacted) = SensitiveValueRedactor.Redact("token", "null");
        Assert.Equal("null", text);
        Assert.False(redacted);
    }

    [Fact]
    public void Redact_QuotedTrivial_UnchangedAfterUnwrap()
    {
        var (text, redacted) = SensitiveValueRedactor.Redact(null, "\"null\"");
        Assert.Equal("\"null\"", text);
        Assert.False(redacted);
    }

    [Theory]
    [InlineData("`None`")]
    [InlineData("`null`")]
    [InlineData("`nil`")]
    public void Redact_BacktickQuotedTrivial_UnchangedEvenSensitiveName(string value)
    {
        // P1-2：反引号装饰与 ' " 同等（上游 unwrap 三选）；敏感名 + 平凡反引号值仍保留可调
        var (text, redacted) = SensitiveValueRedactor.Redact("token", value);
        Assert.Equal(value, text);
        Assert.False(redacted);
    }

    [Fact]
    public void Redact_PlaceholderInput_Unchanged()
    {
        var (text, redacted) = SensitiveValueRedactor.Redact("token", Ph);
        Assert.Equal(Ph, text);
        Assert.False(redacted);
    }

    // ---- 子串不误伤：tokenCount/cookieCount/passwordReset 非名单名 → 不脱敏 ----

    [Theory]
    [InlineData("tokenCount")]
    [InlineData("cookieCount")]
    [InlineData("passwordReset")]
    [InlineData("tokensCount")]
    public void Redact_SubstringName_NotRedacted(string name)
    {
        var (text, redacted) = SensitiveValueRedactor.Redact(name, "\"3\"");
        Assert.Equal("\"3\"", text);
        Assert.False(redacted);
    }

    [Fact]
    public void Redact_PlainNameNormalValue_Unchanged()
    {
        var (text, redacted) = SensitiveValueRedactor.Redact("x", "\"hello world\"");
        Assert.Equal("\"hello world\"", text);
        Assert.False(redacted);
    }

    // ---- 内容匹配：普通名变量值含 JWT/PEM/AWS/GitHub/OpenAI/Bearer/连接串 形态 → redacted ----

    [Theory]
    [InlineData("\"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c\"")] // JWT
    [InlineData("\"Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc\"")]                        // Bearer + JWT
    [InlineData("\"-----BEGIN RSA PRIVATE KEY-----MIIEvQIBADANBgkqhkiG9w0BAQEFAASC-----END RSA PRIVATE KEY-----\"")] // PEM
    [InlineData("\"AKIAIOSFODNN7EXAMPLE\"")]                                       // AWS key id
    [InlineData("\"ghp_abcdefghijklmnopqrstuvwxyz\"")]                             // GitHub classic
    [InlineData("\"sk-proj-1234567890abcdefghijklmn\"")]                           // OpenAI/Anthropic
    [InlineData("\"AccountKey=Zm9vYmFyYmF6cXV4cXV4;Endpoint=https://x/\"")]        // 连接串 Key=
    public void Redact_SecretShapedContent_Redacted(string display)
    {
        var (text, redacted) = SensitiveValueRedactor.Redact("cfg", display);
        Assert.Equal(Ph, text);
        Assert.True(redacted);
    }

    [Fact]
    public void LooksLikeSecret_DirectChecks()
    {
        Assert.True(SensitiveValueRedactor.LooksLikeSecret("Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc"));
        Assert.True(SensitiveValueRedactor.LooksLikeSecret("-----BEGIN RSA PRIVATE KEY-----MIIEvQ"));
        Assert.True(SensitiveValueRedactor.LooksLikeSecret("x-AKIAIOSFODNN7EXAMPLE-y"));
        Assert.False(SensitiveValueRedactor.LooksLikeSecret("hello world"));
        Assert.False(SensitiveValueRedactor.LooksLikeSecret(""));
    }

    // ---- 表达式级：末段标识符归一化名单判定（防 debug_evaluate 换名绕过）----

    [Theory]
    [InlineData("cfg.Token")]
    [InlineData("b.Password")]
    [InlineData("config.API_KEY")]
    [InlineData("env[\"API_KEY\"]")]                    // 名字面量（DebugMCP 同款全局抓标识符）
    [InlineData("Environment.GetEnvironmentVariable(\"API_KEY\")")] // 方法调用形态末段 API_KEY
    public void IsSensitiveExpression_LastIdentifierSensitive_True(string expression)
    {
        Assert.True(SensitiveValueRedactor.IsSensitiveExpression(expression));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("i")]
    [InlineData("b.A")]
    [InlineData("cfg.Name")]
    [InlineData("envDict[0]")]
    [InlineData("scores[3]")]
    public void IsSensitiveExpression_Benign_False(string? expression)
    {
        Assert.False(SensitiveValueRedactor.IsSensitiveExpression(expression!));
    }

    [Fact]
    public void RedactExpression_ExpressionHit_OverridesBenignContent()
    {
        // 表达式末段 Token 敏感：值本身不像凭据也整值换占位符（防换名读同一 secret）
        var (text, redacted) = SensitiveValueRedactor.RedactExpression("cfg.Token", "\"plain\"");
        Assert.Equal(Ph, text);
        Assert.True(redacted);
    }

    [Fact]
    public void RedactExpression_ContentHit_PlainExpression()
    {
        var (text, redacted) = SensitiveValueRedactor.RedactExpression("x", "\"Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc\"");
        Assert.Equal(Ph, text);
        Assert.True(redacted);
    }

    [Fact]
    public void RedactExpression_Benign_Unchanged()
    {
        var (text, redacted) = SensitiveValueRedactor.RedactExpression("x", "\"hello\"");
        Assert.Equal("\"hello\"", text);
        Assert.False(redacted);
    }

    [Fact]
    public void RedactExpression_TrivialValue_PrecedesSensitiveExpression()
    {
        // 平凡分支先于表达式级：末段 Token 敏感但值为 null → 原样保留（「为什么 token 是 null」可调）
        var (text, redacted) = SensitiveValueRedactor.RedactExpression("cfg.Token", "null");
        Assert.Equal("null", text);
        Assert.False(redacted);
    }

    // ---- P3-3 性能护栏（对齐上游 secretRedaction.test.ts 两条 adversarial 输入，<1s）----

    [Fact]
    public void LooksLikeSecret_BackslashRun_NoCatastrophicBacktracking()
    {
        var evil = "{\"api_key\": \"" + new string('\\', 50_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SensitiveValueRedactor.LooksLikeSecret(evil);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"redaction took {sw.ElapsedMilliseconds}ms - patterns are backtracking");
    }

    [Fact]
    public void LooksLikeSecret_LongIdentifierRun_MatchingNothing_StaysFast()
    {
        // 'a_' × 40000 + "=x"（上游同款）；无任何凭据形态命中，仅验证线性
        var longRun = string.Concat(Enumerable.Repeat("a_", 40_000)) + "=x";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SensitiveValueRedactor.LooksLikeSecret(longRun);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"redaction took {sw.ElapsedMilliseconds}ms");
    }
}

