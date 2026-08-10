using System.Text;

namespace HeadTrackBridge;

/// <summary>
/// One file per run holding everything worth reading afterwards: the player's
/// own startup diagnostics and mpv's output, interleaved in the order they
/// happened.
///
/// It used to be mpv's output alone, and it was almost unusable — mpv rewrites
/// a one-line progress display several times a second, and captured to a file
/// that becomes hundreds of kilobytes of
///
///     AV: 00:00:00 / 00:01:00 (1%) A-V:  0.310 Dropped: 15
///
/// a minute, burying the few lines that matter. The status line is switched off
/// at the source now (see MpvLauncher's --term-status-msg), and what replaces
/// it is the context needed to make sense of a report: version, paths, screen
/// and window geometry, language, which VR mode was chosen and why.
///
/// Those were already printed to the console, and the console is exactly what
/// someone who double-clicked the exe never sees.
/// </summary>
public static class SessionLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>Where the log actually went, or null if none could be opened.</summary>
    public static string? Path { get; private set; }

    public static void Open(string preferred)
    {
        foreach (var candidate in Candidates(preferred))
        {
            try
            {
                // UTF-8 *with* a byte-order mark. The content is the same
                // either way, but this file exists to be opened by a user and
                // pasted into a bug report, and without the mark Windows tools
                // on a Chinese system read it as GBK: every non-ASCII
                // character, including the mode line the log is there to show,
                // comes out as mojibake.
                _file = new StreamWriter(candidate, append: false, new UTF8Encoding(true))
                {
                    AutoFlush = true,
                };
                Path = candidate;
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held open by another instance, or the directory is read-only.
                // Try the next name rather than giving up: losing the log is an
                // inconvenience, failing to start is not acceptable.
            }
        }
    }

    /// <summary>
    /// A second copy of the player must not truncate the first one's log, and a
    /// read-only install directory must not stop either of them running.
    /// </summary>
    private static IEnumerable<string> Candidates(string preferred)
    {
        yield return preferred;

        var dir = System.IO.Path.GetDirectoryName(preferred) ?? ".";
        var stem = System.IO.Path.GetFileNameWithoutExtension(preferred);
        var ext = System.IO.Path.GetExtension(preferred);
        yield return System.IO.Path.Combine(dir, $"{stem}-{Environment.ProcessId}{ext}");
        yield return System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{stem}-{Environment.ProcessId}{ext}");
    }

    public static void Line(string text)
    {
        lock (Gate)
        {
            try { _file?.WriteLine(text); }
            catch (IOException) { /* the log is never worth an exception */ }
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            try { _file?.Dispose(); } catch (IOException) { }
            _file = null;
        }
    }

    /// <summary>
    /// Wrap the console so everything printed to it is also written to the log.
    /// </summary>
    public static TextWriter Tee(TextWriter console) => new TeeWriter(console);

    /// <summary>
    /// Passes every character through to the console, and copies completed
    /// lines to the log.
    /// </summary>
    /// <remarks>
    /// Complete lines only, and that is the whole point of doing this a
    /// character at a time. The head-tracking status is redrawn in place many
    /// times a second with a leading carriage return and no newline:
    ///
    ///     \r[  drag] head y   0.0 p   0.0  ->  view y    0.0 ...
    ///
    /// On a terminal it is one line that keeps changing. Copied verbatim to a
    /// file it is the same flood that made mpv's own log useless, so a bare
    /// carriage return discards the pending text instead of emitting it.
    ///
    /// "\r\n" must still count as one ordinary line ending, which is why the
    /// carriage return only discards once something other than a newline
    /// follows it.
    /// </remarks>
    private sealed class TeeWriter(TextWriter console) : TextWriter
    {
        private readonly StringBuilder _line = new();
        private bool _pendingCarriageReturn;

        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);

            if (value == '\n')
            {
                Line(_line.ToString());
                _line.Clear();
                _pendingCarriageReturn = false;
                return;
            }

            if (value == '\r')
            {
                _pendingCarriageReturn = true;
                return;
            }

            if (_pendingCarriageReturn)
            {
                _line.Clear();               // an in-place redraw, not a line
                _pendingCarriageReturn = false;
            }

            // A runaway line without any newline must not grow without bound.
            if (_line.Length < 4096) _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void Flush() => console.Flush();
    }
}
