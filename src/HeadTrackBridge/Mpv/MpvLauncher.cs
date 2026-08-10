using System.Diagnostics;

namespace HeadTrackBridge.Mpv;

/// <summary>
/// Starts mpv with the repo's own config directory, so the player setup
/// (mpv.conf, input.conf, the mpv360 script) is versioned with the project and
/// does not disturb whatever the user already has in %APPDATA%\mpv.
/// </summary>
public sealed class MpvLauncher : IDisposable
{
    private Process? _process;
    private static void WriteLog(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        // Prefixed, because the log now interleaves mpv's output with the
        // player's own diagnostics and telling them apart afterwards matters.
        SessionLog.Line($"[mpv] {e.Data}");
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// mpv's process id, or 0 if it is not running.
    ///
    /// Deliberately not MainWindowHandle: mpv owns more than one top-level
    /// window and MainWindowHandle returns the wrong one (observed handle
    /// 1B0B4A while the real video window was 2203CA), so anything comparing
    /// against it silently never matches. Callers should identify the window by
    /// process instead.
    /// </summary>
    public int ProcessId
    {
        get
        {
            if (_process is not { HasExited: false }) return 0;
            try { return _process.Id; }
            catch (InvalidOperationException) { return 0; }
        }
    }

    public string BuildCommandLinePreview(MpvConfig cfg, string configDir, string? mediaPath,
                                          IEnumerable<string>? extra = null) =>
        $"\"{ResolveMpvPath(cfg.MpvPath)}\" " + string.Join(' ', Args(cfg, configDir, mediaPath, extra));

    /// <summary>
    /// Finds mpv, preferring a copy shipped next to us.
    ///
    /// Order matters. A bundled mpv wins over anything installed on the machine
    /// so that a released build behaves identically everywhere and cannot be
    /// broken by whatever mpv version the user happens to have. Only when there
    /// is no bundled copy (i.e. a dev checkout) do we fall back to the system,
    /// and even then PATH is unreliable: the winget shinchiro package installs
    /// to Program Files without touching PATH at all.
    /// </summary>
    public static string ResolveMpvPath(string configured)
    {
        // An explicit path in the config always wins.
        if (configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar))
            return configured;

        var exe = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? configured : configured + ".exe";

        foreach (var c in BundledCandidates(exe))
            if (File.Exists(c)) return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                var p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] installed =
        [
            Path.Combine(pf, "MPV Player", exe),
            Path.Combine(pf, "mpv", exe),
            Path.Combine(local, "Programs", "mpv", exe),
        ];

        foreach (var c in installed)
            if (File.Exists(c)) return c;

        return configured;   // let Process.Start produce the error
    }

    /// <summary>Locations a shipped mpv may live in, relative to our own binary.</summary>
    private static IEnumerable<string> BundledCandidates(string exe)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "mpv", exe);
        yield return Path.Combine(baseDir, exe);

        // Dev checkout: third_party/mpv/ next to the repo root, so a developer
        // can test the shipped layout without installing anything.
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "mpv", exe);
            if (File.Exists(candidate)) { yield return candidate; break; }
            dir = dir.Parent;
        }
    }

    /// <summary>True when the resolved mpv is one we ship, for logging.</summary>
    public static bool IsBundled(string resolved) =>
        resolved.Contains(Path.Combine("third_party", "mpv"), StringComparison.OrdinalIgnoreCase) ||
        resolved.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

    /// <param name="extra">
    /// Computed at runtime (currently the resolved UI language). Kept separate
    /// from cfg.ExtraArgs so it is never written back when the user saves config.
    /// </param>
    private static List<string> Args(MpvConfig cfg, string configDir, string? mediaPath,
                                     IEnumerable<string>? extra)
    {
        var args = new List<string>
        {
            $"--config-dir={configDir}",
            $"--input-ipc-server={cfg.IpcPipe}",

            // No progress line. mpv redraws it several times a second, and
            // since its output is captured to a file rather than shown on a
            // terminal, every redraw is a new line: hundreds of kilobytes a
            // minute of "AV: 00:00:00 / 00:01:00 (1%) A-V: 0.310 Dropped: 15",
            // with the handful of lines that matter buried in it.
            //
            // Only the status display is silenced. Warnings and errors -- the
            // A-V desync notice, decoder failures, shader problems -- still go
            // to the log, and those are the reason the log exists.
            "--term-status-msg=",
        };
        if (extra is not null) args.AddRange(extra);
        args.AddRange(cfg.ExtraArgs);
        if (!string.IsNullOrWhiteSpace(mediaPath)) args.Add(mediaPath);
        return args;
    }

    /// <remarks>
    /// mpv's output is captured rather than inherited: it would otherwise print
    /// over the bridge's own status line. It goes to <see cref="SessionLog"/>,
    /// the same file the player writes its startup diagnostics to, so a report
    /// arrives as one file with both halves in the order they happened.
    /// </remarks>
    public bool Start(MpvConfig cfg, string configDir, string? mediaPath,
                      IEnumerable<string>? extra = null)
    {
        var psi = new ProcessStartInfo(ResolveMpvPath(cfg.MpvPath))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in Args(cfg, configDir, mediaPath, extra)) psi.ArgumentList.Add(a);

        try
        {
            _process = Process.Start(psi);
            if (_process is null) return false;

            _process.OutputDataReceived += WriteLog;
            _process.ErrorDataReceived += WriteLog;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"Could not start mpv ('{cfg.MpvPath}'): {ex.Message}");
            Console.Error.WriteLine("Install mpv and put it on PATH, or set mpv.mpvPath in the config file.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        _process?.Dispose();
    }
}
