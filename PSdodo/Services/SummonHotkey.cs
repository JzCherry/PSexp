using System.Text;
using Avalonia.Input;

namespace PsExporter.Services;

/// <summary>
/// 唤起热键的键串、Avalonia 按键与系统虚拟键码互转。
/// Avalonia 12 的 Key 取值不是 Windows 虚拟键码（Key.A=44），所以只认四段各自连续的区间，其余键一律拒绝而不是猜映射。
/// </summary>
public static class SummonHotkey
{
    // RegisterHotKey 的 fsModifiers 取值；与 Avalonia KeyModifiers 的位值一致，但仍逐位映射，不靠巧合。
    public const uint NativeAlt = 0x1;
    public const uint NativeControl = 0x2;
    public const uint NativeShift = 0x4;
    public const uint NativeWin = 0x8;

    public static bool TryGetVirtualKey(Key key, out uint virtualKey)
    {
        virtualKey = key switch
        {
            >= Key.A and <= Key.Z => 0x41 + (uint)(key - Key.A),
            >= Key.D0 and <= Key.D9 => 0x30 + (uint)(key - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (uint)(key - Key.NumPad0),
            >= Key.F1 and <= Key.F12 => 0x70 + (uint)(key - Key.F1),
            _ => 0u
        };
        return virtualKey != 0;
    }

    /// <summary>
    /// 裸键只放行 F1–F12：裸字母/数字/小键盘会在全系统吞掉普通输入，代价不可接受。
    /// 单用功能键确实会接管该键（如 F5 在其他程序不再刷新），这是选择它的代价，录键时提示一次。
    /// </summary>
    public static bool Supports(Key key, KeyModifiers modifiers)
        => TryGetVirtualKey(key, out _)
            && (modifiers != KeyModifiers.None || key is >= Key.F1 and <= Key.F12);

    public static uint NativeModifiers(KeyModifiers modifiers)
    {
        var result = 0u;
        if ((modifiers & KeyModifiers.Control) != 0) result |= NativeControl;
        if ((modifiers & KeyModifiers.Alt) != 0) result |= NativeAlt;
        if ((modifiers & KeyModifiers.Shift) != 0) result |= NativeShift;
        if ((modifiers & KeyModifiers.Meta) != 0) result |= NativeWin;
        return result;
    }

    public static bool TryFormat(Key key, KeyModifiers modifiers, out string text)
    {
        text = string.Empty;
        if (!Supports(key, modifiers)) return false;
        var builder = new StringBuilder(20);
        if ((modifiers & KeyModifiers.Control) != 0) builder.Append("Ctrl+");
        if ((modifiers & KeyModifiers.Alt) != 0) builder.Append("Alt+");
        if ((modifiers & KeyModifiers.Shift) != 0) builder.Append("Shift+");
        if ((modifiers & KeyModifiers.Meta) != 0) builder.Append("Win+");
        builder.Append(KeyName(key));
        text = builder.ToString();
        return true;
    }

    public static bool TryParse(string? text, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var token in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= KeyModifiers.Control; continue;
                case "ALT": modifiers |= KeyModifiers.Alt; continue;
                case "SHIFT": modifiers |= KeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS" or "META": modifiers |= KeyModifiers.Meta; continue;
            }
            // 只允许一个主键，第二个一律视为非法串。
            if (key != Key.None || !TryParseKey(token, out key)) return false;
        }
        return Supports(key, modifiers);
    }

    /// <summary>空串是合法值，表示不注册任何热键；只有读不懂的脏值才回落默认组合。</summary>
    public static string Normalize(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return TryParse(text, out var key, out var modifiers) && TryFormat(key, modifiers, out var canonical)
            ? canonical
            : fallback;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (key - Key.NumPad0),
        _ => "F" + (1 + (key - Key.F1))
    };

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        var upper = token.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z')
        {
            key = Key.A + (upper[0] - 'A');
            return true;
        }
        if (upper.Length == 1 && char.IsAsciiDigit(upper[0]))
        {
            key = Key.D0 + (upper[0] - '0');
            return true;
        }
        if (upper.Length is 2 or 3 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), out var function) && function is >= 1 and <= 12)
        {
            key = Key.F1 + (function - 1);
            return true;
        }
        if (upper.Length == 7 && upper.StartsWith("NUMPAD", StringComparison.Ordinal) && char.IsAsciiDigit(upper[6]))
        {
            key = Key.NumPad0 + (upper[6] - '0');
            return true;
        }
        return false;
    }
}
