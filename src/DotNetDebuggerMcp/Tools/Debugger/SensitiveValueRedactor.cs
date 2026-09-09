using System.Text.RegularExpressions;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// DB1 变量敏感脱敏助手（宿主渲染层，纯函数）：读值出口展示文本按 变量/字段名（归一化 exact-match）+
/// 内容形态（凭据正则）双模式过滤，null/平凡值不动；debug_evaluate 表达式级绕过一并覆盖。
/// 规则移植 microsoft/DebugMCP secretRedaction.ts 全集（只借鉴设计逐条翻译，非启发式）；只脱敏渲染文本，
/// 绝不改 Engine/Session 数据。命中返回占位符 <see cref="Placeholder"/>，判定见 Redact/RedactExpression。
/// </summary>
internal static class SensitiveValueRedactor
{
    /// <summary>脱敏占位符：命中疑似凭据后替换原值。</summary>
    internal const string Placeholder = "[已脱敏:疑似凭据]";

    /// <summary>提示后半句（占位符含义 + 判读建议）；各出口拼前缀：debug_variables 头部计数 / debug_evaluate 行内单次提示。</summary>
    internal const string Notice = "疑似凭据已脱敏——用类型/长度/null 判断，勿读原始值";

    /// <summary>按名规则全集（DebugMCP SENSITIVE_NAMES 逐条；已归一化为小写无分隔符，比较前对 name 做同款归一化）。</summary>
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.Ordinal)
    {
        // Keys
        "apikey", "apikeys", "apisecret", "apisecretkey", "accesskey", "accesskeyid",
        "secretkey", "secretaccesskey", "privatekey", "publicprivatekey", "encryptionkey",
        "signingkey", "sessionkey", "masterkey", "clientkey", "sshkey", "gpgkey", "saskey",
        // Secrets
        "secret", "secrets", "clientsecret", "consumersecret",
        // Passwords
        "password", "passwords", "passwd", "pwd", "pass", "passphrase",
        "dbpassword", "dbpasswd", "dbpass", "rootpassword", "adminpassword", "userpassword",
        // Tokens
        "token", "tokens", "accesstoken", "refreshtoken", "idtoken", "authtoken", "apitoken",
        "sessiontoken", "bearertoken", "bearer", "oauthtoken", "personalaccesstoken",
        "csrftoken", "xsrftoken", "sastoken", "jwt",
        // Credentials / auth
        "credential", "credentials", "authorization", "auth", "otp",
        // Sessions and cookies
        "cookie", "cookies", "sessionid",
        // Connection strings
        "connectionstring", "connstr", "accountkey", "sasurl",
        // Common environment-variable spellings
        "openaiapikey", "anthropicapikey", "awssecretaccesskey", "awsaccesskeyid",
        "awssessiontoken", "githubtoken", "ghtoken", "gitlabtoken", "npmtoken", "slacktoken",
        "azurestoragekey", "googleapikey",
    };

    /// <summary>内容规则全集（DebugMCP SECRET_VALUE_PATTERNS 逐条翻译；不区分变量名）。</summary>
    private static readonly Regex[] SecretPatterns =
    {
        // PEM 块：正文用 base64/空白字符类（非 [\s\S]*?），匹配线性无歧义；未闭合块也吞掉密钥材料
        new(@"-----BEGIN[A-Z ]*PRIVATE KEY-----[A-Za-z0-9+/=\s]*(?:-----END[A-Z ]*PRIVATE KEY-----)?", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]+", RegexOptions.Compiled),      // JWT
        new(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA)[0-9A-Z]{12,}\b", RegexOptions.Compiled),             // AWS key id
        new(@"\bgh[pousr]_[A-Za-z0-9]{16,}\b", RegexOptions.Compiled),                                 // GitHub tokens
        new(@"\bgithub_pat_[A-Za-z0-9_]{20,}\b", RegexOptions.Compiled),
        new(@"\bxox[abopsr]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled),                              // Slack tokens
        new(@"\bAIza[0-9A-Za-z_-]{30,}\b", RegexOptions.Compiled),                                     // Google API keys
        new(@"\bsk-(?:[A-Za-z0-9_-]+-)?[A-Za-z0-9]{16,}\b", RegexOptions.Compiled),                    // OpenAI / Anthropic style
        new(@"\b[sr]k_(?:live|test)_[A-Za-z0-9]{10,}\b", RegexOptions.Compiled),                       // Stripe
        new(@"\bnpm_[A-Za-z0-9]{30,}\b", RegexOptions.Compiled),
        new(@"\bglpat-[A-Za-z0-9_-]{16,}\b", RegexOptions.Compiled),                                   // GitLab
        new(@"\bBearer\s+[A-Za-z0-9._~+/=-]{12,}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:AccountKey|SharedAccessSignature|Password|Pwd)\s*=\s*[^;\s'""]+", RegexOptions.IgnoreCase | RegexOptions.Compiled), // connection strings
    };

    /// <summary>不可能携带秘密的平凡值（null/空/平凡不动，保「为什么 token 是 null」可调）。</summary>
    private static readonly string[] TrivialValues =
        { "", "none", "null", "nil", "undefined", "nan", "true", "false", "0", "-1", "[]", "{}", "()", "empty", "<empty>" };

    /// <summary>按名 exact-match：归一化（小写 + 去 `_`/`-`/空格）后在名单内即敏感；非子串规则。</summary>
    internal static bool IsSensitiveName(string name)
        => name.Length > 0 && SensitiveNames.Contains(Normalize(name));

    /// <summary>归一化比较形：大小写与 `_`/`-`/空格分隔符无意义（API_KEY/api-key/apiKey → apikey）。</summary>
    internal static string Normalize(string name)
        => string.Concat(name.ToLowerInvariant().Where(c => c is not ('_' or '-' or ' ')));

    /// <summary>内容形态判定：任一凭据正则命中即疑似秘密（与变量名无关）。</summary>
    internal static bool LooksLikeSecret(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var p in SecretPatterns)
            if (p.IsMatch(text)) return true;
        return false;
    }

    /// <summary>平凡值判定：剥引号/装饰后（'None' 与 None 同等）小写比对平凡清单。</summary>
    internal static bool IsTrivial(string text) => TrivialValues.Contains(Unwrap(text).ToLowerInvariant());

    /// <summary>剥成对引号装饰（反复剥嵌套：`'x'`→x）。</summary>
    private static string Unwrap(string text)
    {
        var t = text.Trim();
        while (t.Length >= 2 && ((t[0] == '\'' && t[^1] == '\'') || (t[0] == '"' && t[^1] == '"')))
            t = t[1..^1].Trim();
        return t;
    }

    /// <summary>
    /// 单变量脱敏判定（DebugMCP redactVariableValue 同语义）：值自身名 + 自身值决定，
    /// 不下钻结构（对象 children 每字段行按其自身名在 RenderVariable 递归中独立判定）。
    /// text 空/平凡/已是占位符 → 原样返回 false；命中按名或按内容 → 占位符 true。
    /// </summary>
    internal static (string Text, bool Redacted) Redact(string? name, string text)
    {
        if (text == Placeholder) return (text, false);
        if (string.IsNullOrEmpty(text) || IsTrivial(text)) return (text, false);
        if (!string.IsNullOrEmpty(name) && IsSensitiveName(name)) return (Placeholder, true);
        if (LooksLikeSecret(text)) return (Placeholder, true);
        return (text, false);
    }

    /// <summary>
    /// 表达式级绕过判定（DebugMCP isSensitiveExpression 同语义）：表达式文本最后一个标识符
    /// 归一化后在名单内 → 敏感。防「换个变量名读同一 secret」（cfg.Token 末段 Token）。
    /// </summary>
    internal static bool IsSensitiveExpression(string expression)
    {
        if (string.IsNullOrEmpty(expression)) return false;
        // 标识符体含 `-`（对齐 DebugMCP [A-Za-z_][A-Za-z0-9_-]*）；抓全部标识符取最后一个
        string? last = null;
        foreach (Match m in Regex.Matches(expression, "[A-Za-z_][A-Za-z0-9_-]*"))
            last = m.Value;
        return last is not null && IsSensitiveName(last);
    }

    /// <summary>表达式求值结果脱敏（DebugMCP redactExpressionResult 同语义）：表达式敏感优先于值内容判定。</summary>
    internal static (string Text, bool Redacted) RedactExpression(string expression, string text)
    {
        if (text == Placeholder) return (text, false);
        if (string.IsNullOrEmpty(text) || IsTrivial(text)) return (text, false);
        if (IsSensitiveExpression(expression)) return (Placeholder, true);
        if (LooksLikeSecret(text)) return (Placeholder, true);
        return (text, false);
    }
}
