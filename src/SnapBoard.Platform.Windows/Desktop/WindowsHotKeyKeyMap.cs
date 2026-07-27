using System.Globalization;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.Windows.Desktop;

internal static class WindowsHotKeyKeyMap
{
    private const GlobalHotKeyModifiers UserModifiers =
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Windows;

    public static GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeyModifiers modifiers,
        string keyName)
    {
        GlobalHotKeyModifiers normalizedModifiers = modifiers & UserModifiers;
        if (normalizedModifiers == GlobalHotKeyModifiers.None)
        {
            return new GlobalHotKeyGestureCreationResult(
                GlobalHotKeyGestureCreationStatus.MissingModifier);
        }

        if (!TryResolveVirtualKey(keyName, out uint virtualKey, out string displayName))
        {
            return new GlobalHotKeyGestureCreationResult(
                GlobalHotKeyGestureCreationStatus.UnsupportedKey);
        }

        normalizedModifiers |= GlobalHotKeyModifiers.NoRepeat;
        return new GlobalHotKeyGestureCreationResult(
            GlobalHotKeyGestureCreationStatus.Created,
            new GlobalHotKeyGesture(
                normalizedModifiers,
                virtualKey,
                FormatDisplayName(normalizedModifiers, displayName)));
    }

    private static bool TryResolveVirtualKey(
        string keyName,
        out uint virtualKey,
        out string displayName)
    {
        if (keyName.Length == 1)
        {
            char key = char.ToUpperInvariant(keyName[0]);
            if (key is >= 'A' and <= 'Z')
            {
                virtualKey = key;
                displayName = key.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }

        if (keyName.Length == 2 &&
            keyName[0] == 'D' &&
            keyName[1] is >= '0' and <= '9')
        {
            virtualKey = keyName[1];
            displayName = keyName[1].ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (TryResolveNumberedKey(
                keyName,
                "NumPad",
                0,
                9,
                0x60,
                "Num ",
                out virtualKey,
                out displayName) ||
            TryResolveNumberedKey(
                keyName,
                "F",
                1,
                24,
                0x70,
                "F",
                out virtualKey,
                out displayName))
        {
            return true;
        }

        (int resolvedVirtualKey, displayName) = keyName switch
        {
            "Back" => (0x08, "Backspace"),
            "Tab" => (0x09, "Tab"),
            "Clear" => (0x0C, "Clear"),
            "Return" or "Enter" => (0x0D, "Enter"),
            "Pause" => (0x13, "Pause"),
            "CapsLock" or "Capital" => (0x14, "Caps Lock"),
            "Escape" => (0x1B, "Esc"),
            "Space" => (0x20, "Space"),
            "PageUp" or "Prior" => (0x21, "Page Up"),
            "PageDown" or "Next" => (0x22, "Page Down"),
            "End" => (0x23, "End"),
            "Home" => (0x24, "Home"),
            "Left" => (0x25, "Left"),
            "Up" => (0x26, "Up"),
            "Right" => (0x27, "Right"),
            "Down" => (0x28, "Down"),
            "Print" => (0x2A, "Print"),
            "Snapshot" or "PrintScreen" => (0x2C, "Print Screen"),
            "Insert" => (0x2D, "Insert"),
            "Delete" => (0x2E, "Delete"),
            "Help" => (0x2F, "Help"),
            "Apps" => (0x5D, "Menu"),
            "Sleep" => (0x5F, "Sleep"),
            "Multiply" => (0x6A, "Num *"),
            "Add" => (0x6B, "Num +"),
            "Separator" => (0x6C, "Num Separator"),
            "Subtract" => (0x6D, "Num -"),
            "Decimal" => (0x6E, "Num ."),
            "Divide" => (0x6F, "Num /"),
            "NumLock" => (0x90, "Num Lock"),
            "Scroll" => (0x91, "Scroll Lock"),
            "BrowserBack" => (0xA6, "Browser Back"),
            "BrowserForward" => (0xA7, "Browser Forward"),
            "BrowserRefresh" => (0xA8, "Browser Refresh"),
            "BrowserStop" => (0xA9, "Browser Stop"),
            "BrowserSearch" => (0xAA, "Browser Search"),
            "BrowserFavorites" => (0xAB, "Browser Favorites"),
            "BrowserHome" => (0xAC, "Browser Home"),
            "VolumeMute" => (0xAD, "Volume Mute"),
            "VolumeDown" => (0xAE, "Volume Down"),
            "VolumeUp" => (0xAF, "Volume Up"),
            "MediaNextTrack" => (0xB0, "Media Next"),
            "MediaPreviousTrack" => (0xB1, "Media Previous"),
            "MediaStop" => (0xB2, "Media Stop"),
            "MediaPlayPause" => (0xB3, "Media Play/Pause"),
            "LaunchMail" => (0xB4, "Mail"),
            "SelectMedia" => (0xB5, "Media"),
            "LaunchApplication1" => (0xB6, "App 1"),
            "LaunchApplication2" => (0xB7, "App 2"),
            "OemSemicolon" or "Oem1" => (0xBA, ";"),
            "OemPlus" => (0xBB, "+"),
            "OemComma" => (0xBC, ","),
            "OemMinus" => (0xBD, "-"),
            "OemPeriod" => (0xBE, "."),
            "OemQuestion" or "Oem2" => (0xBF, "/"),
            "OemTilde" or "Oem3" => (0xC0, "`"),
            "OemOpenBrackets" or "Oem4" => (0xDB, "["),
            "OemPipe" or "Oem5" => (0xDC, "\\"),
            "OemCloseBrackets" or "Oem6" => (0xDD, "]"),
            "OemQuotes" or "Oem7" => (0xDE, "'"),
            "Oem8" => (0xDF, "OEM 8"),
            "OemBackslash" or "Oem102" => (0xE2, "OEM \\"),
            _ => (0, string.Empty),
        };

        virtualKey = (uint)resolvedVirtualKey;
        return virtualKey != 0;
    }

    private static bool TryResolveNumberedKey(
        string keyName,
        string prefix,
        int minimum,
        int maximum,
        uint firstVirtualKey,
        string displayPrefix,
        out uint virtualKey,
        out string displayName)
    {
        if (keyName.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(
                keyName.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number) &&
            number >= minimum &&
            number <= maximum)
        {
            virtualKey = firstVirtualKey + (uint)(number - minimum);
            displayName = string.Create(
                CultureInfo.InvariantCulture,
                $"{displayPrefix}{number}");
            return true;
        }

        virtualKey = 0;
        displayName = string.Empty;
        return false;
    }

    private static string FormatDisplayName(
        GlobalHotKeyModifiers modifiers,
        string keyDisplayName)
    {
        List<string> parts = new(5);
        if (modifiers.HasFlag(GlobalHotKeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(keyDisplayName);
        return string.Join('+', parts);
    }
}
