using System.Globalization;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.MacOS.Desktop;

internal static class MacOSHotKeyKeyMap
{
    private const GlobalHotKeyModifiers UserModifiers =
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Meta;

    private static readonly Dictionary<string, KeyDefinition> NamedKeys =
        new Dictionary<string, KeyDefinition>(StringComparer.Ordinal)
        {
            ["Return"] = new(0x24, "Return"),
            ["Enter"] = new(0x24, "Return"),
            ["Tab"] = new(0x30, "Tab"),
            ["Space"] = new(0x31, "Space"),
            ["Back"] = new(0x33, "Delete"),
            ["Escape"] = new(0x35, "Esc"),
            ["Clear"] = new(0x47, "Clear"),
            ["Help"] = new(0x72, "Help"),
            ["Home"] = new(0x73, "Home"),
            ["PageUp"] = new(0x74, "Page Up"),
            ["Prior"] = new(0x74, "Page Up"),
            ["Delete"] = new(0x75, "Forward Delete"),
            ["End"] = new(0x77, "End"),
            ["PageDown"] = new(0x79, "Page Down"),
            ["Next"] = new(0x79, "Page Down"),
            ["Left"] = new(0x7B, "Left"),
            ["Right"] = new(0x7C, "Right"),
            ["Down"] = new(0x7D, "Down"),
            ["Up"] = new(0x7E, "Up"),
            ["Multiply"] = new(0x43, "Keypad *"),
            ["Add"] = new(0x45, "Keypad +"),
            ["Subtract"] = new(0x4E, "Keypad -"),
            ["Decimal"] = new(0x41, "Keypad ."),
            ["Divide"] = new(0x4B, "Keypad /"),
            ["VolumeUp"] = new(0x48, "Volume Up"),
            ["VolumeDown"] = new(0x49, "Volume Down"),
            ["VolumeMute"] = new(0x4A, "Mute"),
            ["OemSemicolon"] = new(0x29, ";"),
            ["Oem1"] = new(0x29, ";"),
            ["OemPlus"] = new(0x18, "="),
            ["OemComma"] = new(0x2B, ","),
            ["OemMinus"] = new(0x1B, "-"),
            ["OemPeriod"] = new(0x2F, "."),
            ["OemQuestion"] = new(0x2C, "/"),
            ["Oem2"] = new(0x2C, "/"),
            ["OemTilde"] = new(0x32, "`"),
            ["Oem3"] = new(0x32, "`"),
            ["OemOpenBrackets"] = new(0x21, "["),
            ["Oem4"] = new(0x21, "["),
            ["OemPipe"] = new(0x2A, "\\"),
            ["Oem5"] = new(0x2A, "\\"),
            ["OemCloseBrackets"] = new(0x1E, "]"),
            ["Oem6"] = new(0x1E, "]"),
            ["OemQuotes"] = new(0x27, "'"),
            ["Oem7"] = new(0x27, "'"),
        };

    private static readonly uint[] LetterKeyCodes =
    [
        0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22,
        0x26, 0x28, 0x25, 0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F,
        0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06,
    ];

    private static readonly uint[] DigitKeyCodes =
    [0x1D, 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19];

    private static readonly uint[] FunctionKeyCodes =
    [
        0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D,
        0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A,
    ];

    private static readonly uint[] KeypadDigitKeyCodes =
    [0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C];

    public static GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeyModifiers modifiers,
        string keyName)
    {
        if (!TryResolveKey(keyName, out KeyDefinition definition))
        {
            return new GlobalHotKeyGestureCreationResult(
                GlobalHotKeyGestureCreationStatus.UnsupportedKey);
        }

        GlobalHotKeyModifiers mainKeyModifier = GetMainKeyModifier(keyName);
        GlobalHotKeyModifiers normalizedModifiers =
            (modifiers & UserModifiers) |
            mainKeyModifier |
            GlobalHotKeyModifiers.NoRepeat;
        GlobalHotKeyModifiers displayModifiers = normalizedModifiers & ~mainKeyModifier;
        return new GlobalHotKeyGestureCreationResult(
            GlobalHotKeyGestureCreationStatus.Created,
            new GlobalHotKeyGesture(
                normalizedModifiers,
                definition.VirtualKey,
                FormatDisplayName(displayModifiers, definition.DisplayName)));
    }

    internal static GlobalHotKeyModifiers GetRequiredMainKeyModifier(
        uint virtualKey) => virtualKey switch
        {
            0x38 or 0x3C => GlobalHotKeyModifiers.Shift,
            0x3B or 0x3E => GlobalHotKeyModifiers.Control,
            0x3A or 0x3D => GlobalHotKeyModifiers.Alt,
            0x36 or 0x37 => GlobalHotKeyModifiers.Meta,
            _ => GlobalHotKeyModifiers.None,
        };

    private static bool TryResolveKey(string keyName, out KeyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        if (keyName.Length == 1)
        {
            char key = char.ToUpperInvariant(keyName[0]);
            if (key is >= 'A' and <= 'Z')
            {
                definition = new KeyDefinition(
                    LetterKeyCodes[key - 'A'],
                    key.ToString(CultureInfo.InvariantCulture));
                return true;
            }
        }

        if (keyName.Length == 2 && keyName[0] == 'D' && keyName[1] is >= '0' and <= '9')
        {
            int digit = keyName[1] - '0';
            definition = new KeyDefinition(
                DigitKeyCodes[digit],
                digit.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        if (TryResolveNumberedKey(keyName, "F", FunctionKeyCodes, 1, "F", out definition) ||
            TryResolveNumberedKey(keyName, "NumPad", KeypadDigitKeyCodes, 0, "Keypad ", out definition))
        {
            return true;
        }

        if (NamedKeys.TryGetValue(keyName, out definition))
        {
            return true;
        }

        (uint virtualKey, string displayName) = keyName switch
        {
            "LeftShift" => (0x38u, "Shift"),
            "RightShift" => (0x3Cu, "Right Shift"),
            "LeftCtrl" => (0x3Bu, "Control"),
            "RightCtrl" => (0x3Eu, "Right Control"),
            "LeftAlt" or "System" => (0x3Au, "Option"),
            "RightAlt" => (0x3Du, "Right Option"),
            "LWin" => (0x37u, "Command"),
            "RWin" => (0x36u, "Right Command"),
            _ => (uint.MaxValue, string.Empty),
        };
        definition = new KeyDefinition(virtualKey, displayName);
        return virtualKey != uint.MaxValue;
    }

    private static GlobalHotKeyModifiers GetMainKeyModifier(string keyName) => keyName switch
    {
        "LeftShift" or "RightShift" => GlobalHotKeyModifiers.Shift,
        "LeftCtrl" or "RightCtrl" => GlobalHotKeyModifiers.Control,
        "LeftAlt" or "RightAlt" or "System" => GlobalHotKeyModifiers.Alt,
        "LWin" or "RWin" => GlobalHotKeyModifiers.Meta,
        _ => GlobalHotKeyModifiers.None,
    };

    private static bool TryResolveNumberedKey(
        string keyName,
        string prefix,
        uint[] keyCodes,
        int firstNumber,
        string displayPrefix,
        out KeyDefinition definition)
    {
        if (keyName.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(
                keyName.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number) &&
            number >= firstNumber &&
            number < firstNumber + keyCodes.Length)
        {
            definition = new KeyDefinition(
                keyCodes[number - firstNumber],
                string.Create(CultureInfo.InvariantCulture, $"{displayPrefix}{number}"));
            return true;
        }

        definition = default;
        return false;
    }

    private static string FormatDisplayName(
        GlobalHotKeyModifiers modifiers,
        string keyDisplayName)
    {
        List<string> parts = new(5);
        if (modifiers.HasFlag(GlobalHotKeyModifiers.Meta))
        {
            parts.Add("Command");
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Alt))
        {
            parts.Add("Option");
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Control))
        {
            parts.Add("Control");
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(keyDisplayName);
        return string.Join('+', parts);
    }

    private readonly record struct KeyDefinition(uint VirtualKey, string DisplayName);
}
