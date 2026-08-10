using System.Windows.Forms;

namespace HeadTrackBridge.Host;

/// <summary>
/// Translates a WinForms key press into the name mpv uses for it.
///
/// This exists because of how <c>--wid</c> embedding works: mpv's window is a
/// child owned by a *different process*, and Windows keyboard focus does not
/// cross that boundary — the host form keeps focus no matter where you click,
/// so mpv and uosc would never see a keystroke. Mouse messages do go to the
/// window under the cursor, which is why only the keyboard needs relaying.
///
/// Relaying through <c>keypress</c> also means mpv's own input.conf stays the
/// single place keys are bound: the host does not need to know that Space is
/// play/pause, it just says "Space happened".
/// </summary>
public static class KeyMap
{
    /// <summary>
    /// The mpv key name, or null for keys that should not be forwarded
    /// (modifiers on their own, and anything with no mpv name).
    /// </summary>
    public static string? ToMpvName(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var ctrl = (keyData & Keys.Control) != 0;
        var alt = (keyData & Keys.Alt) != 0;
        var shift = (keyData & Keys.Shift) != 0;

        var name = BaseName(key, shift);
        if (name is null) return null;

        // mpv writes a shifted printable character as the character itself
        // ("A", "?"), and only spells out Shift+ for named keys ("Shift+TAB").
        // Getting this wrong means the binding silently never matches.
        var prefix = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") +
                     (shift && name.Length > 1 ? "Shift+" : "");
        return prefix + name;
    }

    private static string? BaseName(Keys key, bool shift) => key switch
    {
        >= Keys.A and <= Keys.Z => shift
            ? ((char)('A' + (key - Keys.A))).ToString()
            : ((char)('a' + (key - Keys.A))).ToString(),

        // Shifted digits are layout-dependent (US assumed). Better than dropping
        // them: an unshifted digit is what seek-to-percent bindings want anyway.
        >= Keys.D0 and <= Keys.D9 when !shift => ((char)('0' + (key - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => ((char)('0' + (key - Keys.NumPad0))).ToString(),
        >= Keys.F1 and <= Keys.F24 => "F" + (key - Keys.F1 + 1),

        Keys.Space => "SPACE",
        Keys.Enter => "ENTER",
        Keys.Escape => "ESC",
        Keys.Tab => "TAB",
        Keys.Back => "BS",
        Keys.Delete => "DEL",
        Keys.Insert => "INS",
        Keys.Home => "HOME",
        Keys.End => "END",
        Keys.PageUp => "PGUP",
        Keys.PageDown => "PGDWN",
        Keys.Left => "LEFT",
        Keys.Right => "RIGHT",
        Keys.Up => "UP",
        Keys.Down => "DOWN",

        Keys.Oemplus or Keys.Add => shift ? "+" : "=",
        Keys.OemMinus or Keys.Subtract => "-",
        Keys.Multiply => "*",
        Keys.Divide => "/",
        Keys.Oemcomma => shift ? "<" : ",",
        Keys.OemPeriod => shift ? ">" : ".",
        Keys.OemQuestion => shift ? "?" : "/",
        Keys.OemOpenBrackets => shift ? "{" : "[",
        Keys.Oem6 => shift ? "}" : "]",
        Keys.Oem1 => shift ? ":" : ";",
        Keys.Oem7 => shift ? "\"" : "'",
        Keys.Oem5 => shift ? "|" : "\\",
        Keys.Oemtilde => shift ? "~" : "`",

        _ => null,
    };
}
