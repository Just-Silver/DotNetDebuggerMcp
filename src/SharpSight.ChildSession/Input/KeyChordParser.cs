using System.Globalization;

namespace SharpSight.ChildSession.Input;

public static class KeyChordParser
{
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkShift = 0x10;
    private const ushort VkLeftWindows = 0x5B;

    private const string LegalKeysHint =
        "Ctrl、Alt、Shift、Win、A-Z、0-9、F1-F24、Enter、Tab、Escape、Space、Backspace、Delete、Insert、Home、End、PageUp、PageDown、Up、Down、Left、Right";

    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = VkControl,
        ["Alt"] = VkMenu,
        ["Shift"] = VkShift,
        ["Win"] = VkLeftWindows,
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D,
        ["Tab"] = 0x09,
        ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["Backspace"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
    };

    public static IReadOnlyList<ushort> Parse(string keys)
    {
        var tokens = Tokenize(keys);
        var result = new List<ushort>();
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!TryParseModifier(tokens[i], out var modifier))
                throw new ArgumentException(
                    $"「{tokens[i]}」必须为修饰键（Ctrl/Alt/Shift/Win）且只能位于主键之前。合法键：{LegalKeysHint}。");
            if (result.Contains(modifier))
                throw new ArgumentException($"修饰键「{tokens[i]}」重复。合法键：{LegalKeysHint}。");
            result.Add(modifier);
        }

        var mainToken = tokens[^1];
        if (TryParseModifier(mainToken, out var trailingModifier))
        {
            if (result.Contains(trailingModifier))
                throw new ArgumentException($"修饰键「{mainToken}」重复。合法键：{LegalKeysHint}。");
            throw new ArgumentException($"组合键缺少主键，修饰键之后需且只需一个非修饰键。合法键：{LegalKeysHint}。");
        }
        if (!TryParseMainKey(mainToken, out var mainKey))
            throw new ArgumentException($"未知键「{mainToken}」。合法键：{LegalKeysHint}。");
        result.Add(mainKey);
        return result;
    }

    public static ushort ParseKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException($"键名不能为空。合法键：{LegalKeysHint}。");
        var token = key.Trim();
        if (token.Contains('+'))
            throw new ArgumentException($"单键不接受组合写法「{token}」，组合键请使用组合键解析。合法键：{LegalKeysHint}。");
        if (TryParseModifier(token, out var modifier)) return modifier;
        if (TryParseMainKey(token, out var mainKey)) return mainKey;
        throw new ArgumentException($"未知键「{token}」。合法键：{LegalKeysHint}。");
    }

    private static string[] Tokenize(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
            throw new ArgumentException($"组合键不能为空，需形如Ctrl+Shift+K。合法键：{LegalKeysHint}。");
        var tokens = keys.Split('+');
        for (var i = 0; i < tokens.Length; i++) tokens[i] = tokens[i].Trim();
        var empty = Array.FindIndex(tokens, token => token.Length == 0);
        if (empty >= 0)
            throw new ArgumentException(
                $"组合键第{empty + 1}段为空，请勿省略键名或重复加号。合法键：{LegalKeysHint}。");
        return tokens;
    }

    private static bool TryParseModifier(string token, out ushort key) => Modifiers.TryGetValue(token, out key);

    // 枚举可能接受额外别名，显式白名单避免扩大可提交的键名。
    private static bool TryParseMainKey(string token, out ushort key)
    {
        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            key = char.ToUpperInvariant(token[0]);
            return true;
        }
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = token[0];
            return true;
        }
        if (NamedKeys.TryGetValue(token, out key)) return true;
        return TryParseFunctionKey(token, out key);
    }

    private static bool TryParseFunctionKey(string token, out ushort key)
    {
        key = 0;
        if (token.Length is < 2 or > 3 || (token[0] != 'F' && token[0] != 'f')) return false;
        var digits = token.AsSpan(1);
        if (digits[0] == '0' || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return false;
        if (number is < 1 or > 24) return false;
        key = (ushort)(0x70 + number - 1);
        return true;
    }
}
