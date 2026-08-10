using System.Runtime.InteropServices;
using System.Windows.Forms;
using HeadTrackBridge;
using HeadTrackBridge.Host;

internal static class Program
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    /// <summary>
    /// Adopt the console we were launched from, if there was one.
    ///
    /// The project is a WinExe so that double-clicking the player does not also
    /// open a console window. That would normally throw away every diagnostic
    /// in the app, which matters here because the console carries the live
    /// tracking readout, the mpv command line and the drag/mode traces. This
    /// gets them back whenever the app is started from a terminal, and does
    /// nothing when it is not.
    ///
    /// Must run before anything touches Console: .NET binds the standard
    /// streams on first use, and a stream bound before the attach would keep
    /// writing nowhere. One quirk to know about — the shell prints its next
    /// prompt immediately rather than waiting for us, so output arrives
    /// underneath it.
    /// </summary>
    private static void AttachToParentConsole()
    {
        try
        {
            // Only when we have no output handle of our own.
            //
            // A GUI-subsystem process launched from a shell inherits no console,
            // which is the case this exists for — but one launched with its
            // output redirected (`VRFlatPlayer.exe > log.txt`, or anything that
            // captures it) does inherit that handle, and AttachConsole would
            // replace it with the console's. The redirect then silently
            // swallows everything: the file stays empty and the output goes to
            // a console the caller was not reading.
            var stdout = GetStdHandle(StdOutputHandle);
            if (stdout != IntPtr.Zero && stdout != new IntPtr(-1)) return;

            AttachConsole(AttachParentProcess);
        }
        catch (DllNotFoundException) { /* not Windows; nothing to attach to */ }
    }

    /// <summary>
    /// STA, and therefore not top-level statements and not <c>async Main</c>:
    /// OpenFileDialog and drag-and-drop require a single-threaded apartment, and
    /// an async Main resumes on the thread pool, which silently is not one.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        finally { SessionLog.Close(); }
    }

    private static int Run(string[] args)
    {
        AttachToParentConsole();

        // Without this, non-ASCII output is mangled when stdout is redirected on
        // a non-UTF8 Windows codepage — which includes any path with CJK
        // characters.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* no console */ }

        var cli = CommandLine.Parse(args);
        if (cli.ShowHelp)
        {
            CommandLine.PrintHelp();
            return 0;
        }
        if (cli.ShowVersion)
        {
            Console.WriteLine(AppInfo.NameAndVersion);
            return 0;
        }

        // Before the first diagnostic line, so the log holds all of them. Most
        // people who hit a problem started the player by double-clicking it and
        // never saw the console at all.
        SessionLog.Open(AppPaths.MpvLogFile);
        Console.SetOut(SessionLog.Tee(Console.Out));

        Console.WriteLine($"{AppInfo.NameAndVersion}");
        Console.WriteLine($"log             : {SessionLog.Path ?? "(none — could not open a file)"}");
        AppPaths.SeedUserConfig();
        var configPath = cli.ConfigPath ?? AppPaths.ConfigFile;
        var cfg = BridgeConfig.Load(configPath);
        cli.ApplyOverrides(cfg);

        // Before the session exists: PlayerSession can put a toast on screen the
        // moment it connects, and those read from UiStrings.Current.
        UiStrings.Init(Localization.ResolveOwnLanguage(cfg.Ui.Language));

        Console.WriteLine($"install         : {AppPaths.InstallRoot}");
        if (AppPaths.IsRedirected)
            Console.WriteLine($"settings        : {AppPaths.DataRoot}   (install directory is read-only)");

        if (cli.WriteDefaultConfig)
        {
            cfg.Save(configPath);
            Console.WriteLine($"Wrote config to {configPath}");
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var session = new PlayerSession(cfg, cli, configPath);
        if (!session.StartTrackingSource(cts.Token)) return 1;

        // Diagnostics, both of which run without mpv. The preview has to own
        // this thread: OpenCV's HighGui pumps its own message loop.
        if (cli.CameraPreview)
        {
            if (session.Camera is not { } camera)
            {
                Console.Error.WriteLine("Camera preview needs the camera source. Try --camera-preview on its own.");
                return 1;
            }
            HeadTrackBridge.Tracking.CameraPreview.Run(camera, cfg.Source.Camera.Mirror, cts.Token);
            return 0;
        }

        // --dump just prints packets; useful on the laptop to confirm opentrack
        // output before mpv is anywhere in the picture.
        if (cli.DumpUdp)
        {
            Console.WriteLine(session.HasTrackingSource
                ? "Dump mode — printing incoming poses. Ctrl+C to stop."
                : "Dump mode, but head tracking is off. Try --source=udp --dump.");
            WaitForCancel(cts.Token);
            return 0;
        }

        return cfg.Ui.HostWindow ? RunHosted(session, cfg, cts) : RunDetached(session, cts);
    }

    /// <summary>
    /// The player: our own window with a native menu bar, mpv drawing into a
    /// panel inside it.
    /// </summary>
    private static int RunHosted(PlayerSession session, BridgeConfig cfg, CancellationTokenSource cts)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Must precede the first window. The development machine runs at 300%,
        // where anything less than PerMonitorV2 gives a bitmap-stretched,
        // visibly blurry menu bar.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var window = new PlayerWindow(session);
        var exitCode = 0;

        session.Ended += window.RequestClose;
        window.FormClosing += (_, _) => cts.Cancel();

        // Shown, not the constructor: mpv reads the parent's size when it
        // attaches, so the panel has to exist and be laid out first.
        window.Shown += (_, _) => _ = Task.Run(async () =>
        {
            if (!await session.StartAsync(window.VideoHandle, cts))
            {
                exitCode = 1;
                cts.Cancel();
                window.RequestClose();
                return;
            }

            window.OnSessionReady();
            PlayerSession.PrintKeyHelp();
            session.StartConsoleKeyReader(cts);

            await session.RunAsync(cts.Token);
            window.RequestClose();
        });

        Application.Run(window);
        Console.WriteLine();
        Console.WriteLine("bye");
        return exitCode;
    }

    /// <summary>
    /// mpv in its own window, the bridge as a pure sidecar. Kept because it is
    /// the fallback when window embedding misbehaves, and because it is the only
    /// way to attach to an mpv the user started themselves (--no-launch).
    /// </summary>
    private static int RunDetached(PlayerSession session, CancellationTokenSource cts)
    {
        if (!session.StartAsync(IntPtr.Zero, cts).GetAwaiter().GetResult()) return 1;

        PlayerSession.PrintKeyHelp();
        session.StartConsoleKeyReader(cts);
        session.RunAsync(cts.Token).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("bye");
        return 0;
    }

    private static void WaitForCancel(CancellationToken ct)
    {
        try { Task.Delay(Timeout.Infinite, ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
    }
}
