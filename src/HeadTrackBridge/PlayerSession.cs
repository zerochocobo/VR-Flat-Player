using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HeadTrackBridge.Input;
using HeadTrackBridge.Mapping;
using HeadTrackBridge.Mpv;
using HeadTrackBridge.Tracking;

namespace HeadTrackBridge;

/// <summary>
/// Everything the player is, minus the window: tracking source, view mapper,
/// mpv process, IPC, mode controller and drag watcher, wired together.
///
/// Split out of Program so the two front ends can share it. They differ in one
/// value — the HWND mpv draws into — and in nothing else, which is exactly the
/// property that keeps the detached mode worth having: it is not a second
/// implementation to maintain, it is this one with a zero handle.
/// </summary>
public sealed class PlayerSession : IDisposable
{
    private readonly BridgeConfig _cfg;
    private readonly CommandLine _cli;
    private readonly string _configPath;

    private ITrackingSource? _source;

    /// <summary>The tracking source, when it is a camera. Only the preview needs it.</summary>
    public CameraFaceSource? Camera => _source as CameraFaceSource;
    private PoseRecorder? _recorder;
    private MpvLauncher? _launcher;
    private MouseDragWatcher? _dragWatcher;
    private ModeMemory? _memory;
    private MpvCameraDriver? _driver;
    private string? _currentMedia;

    public PlayerSession(BridgeConfig cfg, CommandLine cli, string configPath)
    {
        _cfg = cfg;
        _cli = cli;
        _configPath = configPath;
        Mapper = new ViewMapper(cfg);
    }

    public ViewMapper Mapper { get; }
    public BridgeConfig Config => _cfg;

    /// <summary>Null until <see cref="StartAsync"/> has connected.</summary>
    public MpvIpcClient? Ipc { get; private set; }

    public PlayerModeController? Mode { get; private set; }

    /// <summary>
    /// Head tracking on/off, backed by the config so the choice survives a
    /// restart. Saving on every change rather than at exit: the player is
    /// often closed by closing the window, and an exit-time save loses whatever
    /// was set in the session that mattered most — the last one.
    /// </summary>
    public bool TrackingEnabled
    {
        get => _cfg.Ui.FaceTracking;
        set
        {
            if (_cfg.Ui.FaceTracking == value) return;
            _cfg.Ui.FaceTracking = value;
            SaveConfig();
            TrackingToggled?.Invoke(value);
            _ = BroadcastTrackerStateAsync();
        }
    }

    /// <summary>Raised when tracking is switched, for the on-screen indicator.</summary>
    public event Action<bool>? TrackingToggled;

    private CancellationToken _trackingCt = CancellationToken.None;

    /// <summary>
    /// Turn head tracking on or off, starting the camera the first time.
    ///
    /// Without the lazy start there was no way in: the menu entry was greyed
    /// unless a source was already running, and nothing started one unless
    /// --source=camera was passed, so a user who never touched the command line
    /// could never switch tracking on. Opening the camera is exactly what
    /// "turn tracking on" means to them.
    ///
    /// Returns false when the camera could not be opened, so the caller can say
    /// so rather than leaving a tick box that quietly refuses to stay ticked.
    /// </summary>
    public bool SetTrackingEnabled(bool on)
    {
        if (on && _source is null && !StartCameraOnDemand()) return false;

        // Switching off releases the camera, it does not merely stop reading it.
        // Leaving the source alive kept the device open, so Test Camera stayed
        // "busy" forever and a restart could not open it either — the config now
        // said the source was a camera, so the player grabbed it again on
        // startup. A camera opens once; whatever holds it has to let go.
        if (!on && _source is CameraFaceSource camera)
        {
            camera.Dispose();
            _source = null;
            Console.WriteLine("head tracking   : off, camera released");
        }

        TrackingEnabled = on;
        return true;
    }

    private bool StartCameraOnDemand()
    {
        try
        {
            var camera = new CameraFaceSource(
                _cfg.Source.Camera, AppPaths.Resolve(_cfg.Source.Camera.ModelPath), verbose: false);
            camera.PoseReceived += p =>
            {
                _recorder?.Write(p);
                if (TrackingEnabled) Mapper.Accept(p);
            };
            camera.Start(_trackingCt);
            _source = camera;

            // Remembered too, so the next launch brings it up without going
            // through the menu again.
            _cfg.Source.Kind = SourceKind.Camera;
            SaveConfig();
            Console.WriteLine($"head tracking   : started on demand — {camera.Name}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not start the camera: {ex.Message}");
            CameraStartFailed?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>Raised when switching tracking on could not open the camera.</summary>
    public event Action<string>? CameraStartFailed;

    /// <summary>
    /// Tell the OSD script which indicators to light.
    ///
    /// "na" for the hand rather than "off": gesture control does not exist yet,
    /// and an icon that looks merely switched off invites people to hunt for
    /// the switch. The script draws that state dimmer than off.
    /// </summary>
    public Task BroadcastTrackerStateAsync() =>
        Ipc?.SendAsync("script-message-to", "vrmenu", "tracker-state",
                       TrackingEnabled ? "on" : "off",
                       "na")
        ?? Task.CompletedTask;

    /// <summary>True once mpv is up and the mode controller exists.</summary>
    public bool IsReady => Ipc is not null && Mode is not null;

    /// <summary>Raised on a background thread once the session is usable.</summary>
    public event Action? Ready;

    /// <summary>Raised whenever the VR mode changes, from any origin.</summary>
    public event Action<string>? ModeChanged;

    /// <summary>Raised when mpv goes away, so a host window can close itself.</summary>
    public event Action? Ended;

    /// <summary>
    /// Raised after the renderer choice changes. The host window uses it to
    /// offer a restart, which is the only way the change takes effect.
    /// </summary>
    public event Action<VideoBackends.Renderer>? RendererChanged;

    /// <summary>The file currently open, so a restart can reopen it.</summary>
    public string? CurrentMedia => _currentMedia;

    /// <summary>
    /// Suppress drag-to-look, e.g. while a menu is open. The window sets this;
    /// so does the in-video mode panel over IPC.
    /// </summary>
    public bool SuppressDrag
    {
        get => _dragWatcher?.Suppressed ?? false;
        set { if (_dragWatcher is not null) _dragWatcher.Suppressed = value; }
    }

    // ------------------------------------------------------------ startup ---

    /// <summary>
    /// Starts the tracking source. Separate from <see cref="StartAsync"/>
    /// because <c>--dump</c> needs a source and nothing else.
    /// </summary>
    public bool StartTrackingSource(CancellationToken ct)
    {
        _trackingCt = ct;
        try
        {
            _source = _cfg.Source.Kind switch
            {
                SourceKind.None => null,
                SourceKind.Udp => new OpenTrackUdpSource(_cfg.Source.UdpPort, _cli.DumpUdp),
                SourceKind.Replay => new ReplaySource(AppPaths.Resolve(_cfg.Source.ReplayFile)),
                // Only when tracking is actually on. The kind is remembered so
                // the choice survives a restart, but remembering it must not
                // mean seizing the camera at launch for someone who has turned
                // tracking off — that is what made Test Camera unusable until
                // the whole install was replaced.
                SourceKind.Camera when _cfg.Ui.FaceTracking || _cli.DumpUdp || _cli.CameraPreview =>
                    new CameraFaceSource(
                        _cfg.Source.Camera, AppPaths.Resolve(_cfg.Source.Camera.ModelPath), _cli.DumpUdp),
                SourceKind.Camera => null,
                _ => new SyntheticSource(_cfg.Source.SyntheticMode, 60, _cfg.Source.SyntheticJitterDegrees),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not open tracking source: {ex.Message}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_cfg.Source.RecordTo))
            _recorder = new PoseRecorder(AppPaths.Resolve(_cfg.Source.RecordTo));

        if (_source is not null)
        {
            _source.PoseReceived += p =>
            {
                _recorder?.Write(p);
                // The one place head tracking is switched on and off. Recording
                // still happens either way, so a session can be captured while
                // the view is being driven by hand.
                if (TrackingEnabled) Mapper.Accept(p);
            };

            // The UDP source prints its own packets (including malformed ones),
            // and the camera source prints its own when verbose, so only the
            // rest need a dump hook. Attach before Start so nothing is missed.
            if (_cli.DumpUdp && _cfg.Source.Kind is not (SourceKind.Udp or SourceKind.Camera))
                _source.PoseReceived += p => Console.WriteLine($"[{_source.Name}] {p}");

            _source.Start(ct);
        }

        Console.WriteLine($"head tracking   : {_source?.Name ?? "off (mouse drag only) — enable with --source=camera"}");
        if (_recorder is not null) Console.WriteLine($"recording to    : {_recorder.Path_}");
        return true;
    }

    public bool HasTrackingSource => _source is not null;

    /// <param name="hostVideoWindow">
    /// Window for mpv to draw into, or <see cref="IntPtr.Zero"/> to let mpv open
    /// its own. Must already exist and be visible: mpv reads the parent's size
    /// when it attaches.
    /// </param>
    public async Task<bool> StartAsync(IntPtr hostVideoWindow, CancellationTokenSource cts)
    {
        _memory = new ModeMemory(AppPaths.ModeMemoryFile);
        _currentMedia = _cli.MediaPath;

        Console.WriteLine($"ui language     : {Localization.Describe(_cfg.Ui.Language)}");
        if (_cfg.Ui.RememberPerFileMode)
            Console.WriteLine($"mode memory     : {_memory.Count} file(s) remembered");

        var mpvConfigDir = AppPaths.MpvConfigDir;
        _launcher = new MpvLauncher();

        if (_cfg.Mpv.AutoLaunch)
        {
            var extra = Localization.MpvArgs(_cfg.Ui.Language).ToList();
            extra.AddRange(BackendArgs(_cfg.Mpv));
            if (hostVideoWindow != IntPtr.Zero)
                extra.AddRange(HostedMpvArgs(hostVideoWindow));

            Console.WriteLine($"launching mpv   : {_launcher.BuildCommandLinePreview(_cfg.Mpv, mpvConfigDir, _cli.MediaPath, extra)}");
            if (!_launcher.Start(_cfg.Mpv, mpvConfigDir, _cli.MediaPath, extra)) return false;
        }
        else
        {
            Console.WriteLine($"waiting for mpv on {_cfg.Mpv.IpcPipe} (start it with --input-ipc-server={_cfg.Mpv.IpcPipe})");
        }

        var ipc = new MpvIpcClient(_cfg.Mpv.IpcPipe);
        if (!await ipc.ConnectAsync(TimeSpan.FromSeconds(15), cts.Token))
        {
            Console.Error.WriteLine("Timed out connecting to mpv's IPC pipe.");
            ipc.Dispose();
            return false;
        }
        Ipc = ipc;
        Console.WriteLine($"mpv ipc         : connected on {_cfg.Mpv.IpcPipe}");

        _driver = new MpvCameraDriver(ipc, _cfg.Mpv);
        var mode = new PlayerModeController(ipc, _driver, _cfg.Video);
        Mode = mode;
        mode.Changed += (d, origin) =>
        {
            Console.WriteLine($"\n  [mode] {d}");
            ModeChanged?.Invoke(d);

            // Geometry and FOV both move the edge of the picture.
            UpdateViewLimits();

            // Only the user's own corrections are remembered — see ModeOrigin.
            // Subscribed here rather than after the first layout pass, so the
            // first file of a session behaves the same as every later one.
            if (origin != ModeOrigin.User) return;

            var chosen = new RememberedMode(mode.Geometry, mode.Stereo, mode.Eye, mode.FovDegrees);
            if (_cfg.Ui.RememberPerFileMode) _memory!.Set(_currentMedia, chosen);

            // Recorded even for a stream or a test source, which have no
            // per-file entry: the point of the sticky mode is to carry a
            // correction forward, and where it was made does not matter.
            //
            // Flat is the exception. It does not mean "I prefer flat", it means
            // "this particular file is not VR" — a fact about that file, decided
            // from its shape. Carrying it forward would make one ordinary video
            // turn VR off for the next genuine VR file.
            if (_cfg.Video.StickyMode && mode.Geometry != Geometry.Flat)
                _memory!.SetLastUsed(chosen);

            _memory!.Save();
        };

        StartDragWatcher(hostVideoWindow, mode, cts.Token);

        ipc.Disconnected += () => { cts.Cancel(); Ended?.Invoke(); };
        ipc.ClientMessage += a => HandleClientMessage(a, mode, ipc);

        // The window title and the menu checkmarks need to follow whatever mpv
        // is doing, including changes the host did not make (uosc's own menu,
        // a key handled inside mpv, the end of a playlist entry).
        // osd-dimensions is here for the view limits: how far you may look up
        // and down depends on the vertical field of view, which depends on the
        // shape of the window, so resizing has to recompute it.
        ipc.PropertyChanged += (name, value) =>
        {
            if (name != "osd-dimensions" || value is not { } v) return;
            if (!v.TryGetProperty("w", out var w) || !v.TryGetProperty("h", out var h)) return;
            var (width, height) = (w.GetDouble(), h.GetDouble());
            if (height <= 0) return;

            var aspect = width / height;
            if (Math.Abs(aspect - _viewportAspect) < 0.01) return;
            _viewportAspect = aspect;
            UpdateViewLimits();
        };

        // Before the observe loop, not after. Two reasons, and the second one
        // cost a bug:
        //
        //   * The layout pass below waits up to 4s for a file to report its
        //     dimensions, and with an empty player it always waits the full 4s.
        //     Nothing the menu bar touches needs that, so it should not be
        //     greyed out for it.
        //
        //   * observe_property makes mpv send the property's *current* value
        //     immediately. Subscribing after the loop meant every one of those
        //     first values was delivered to nobody. Most properties hid it by
        //     changing again a moment later when the file loaded; `playlist`
        //     does not change again until you switch files, so the folder list
        //     stayed empty and Previous/Next stayed greyed out until you used
        //     `<` or `>` once -- which worked, because those go straight to mpv.
        Ready?.Invoke();

        // The icons are drawn by the OSD script, which starts with no idea what
        // the bridge's state is. Without this the face icon stays dark until
        // the first toggle, which looks like the feature is missing.
        _ = BroadcastTrackerStateAsync();

        foreach (var p in new[]
                 {
                     "media-title", "path", "pause", "mute", "volume", "speed",
                     "fullscreen", "loop-file", "aid", "sid", "track-list",
                     "osd-dimensions", "playlist", "playlist-pos",
                 })
            await ipc.ObserveAsync(p);

        // Subscribed before the first detection, not after.
        //
        // The initial call polls for up to four seconds waiting for a file to
        // report its dimensions, and when the player was started with no file
        // it spends all four. Anything opened during that window used to arrive
        // before this handler existed, so the event went nowhere and the file
        // played in whatever mode the player had never applied.
        //
        // Serialised so two quick file changes cannot interleave and leave the
        // mode half-applied.
        var layoutGate = new SemaphoreSlim(1, 1);
        ipc.FileLoaded += () => _ = Task.Run(async () =>
        {
            await layoutGate.WaitAsync();
            try
            {
                var path = await ipc.GetStringPropertyAsync("path");
                Console.WriteLine($"file loaded     : {path ?? "(path unknown)"}");

                if (string.Equals(path, _currentMedia, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("                  same file as before; keeping the current mode");
                    return;
                }
                await ApplyLayoutAsync(ipc, mode);
            }
            catch (Exception e)
            {
                // Nothing observes a fire-and-forget task, so without this an
                // exception here is silent: the file opens in the wrong mode and
                // the log shows no reason at all, which is exactly how this was
                // reported.
                Console.WriteLine($"                  [layout] could not set the mode: {e}");
            }
            finally { layoutGate.Release(); }
        });

        // Through the same gate as the handler, so a file that loads while this
        // is still polling cannot be detected twice at once.
        await layoutGate.WaitAsync();
        try { await ApplyLayoutAsync(ipc, mode); }
        finally { layoutGate.Release(); }

        return true;
    }

    /// <summary>
    /// Decoder/renderer overrides chosen from the menu. Each is only emitted
    /// when set, so an untouched install runs exactly on mpv.conf's tuned
    /// defaults and there is one place those live rather than two.
    /// </summary>
    private static IEnumerable<string> BackendArgs(MpvConfig cfg)
    {
        if (cfg.Hwdec.Length > 0) yield return $"--hwdec={cfg.Hwdec}";
        if (cfg.Vo.Length > 0) yield return $"--vo={cfg.Vo}";
        if (cfg.GpuApi.Length > 0) yield return $"--gpu-api={cfg.GpuApi}";
        if (cfg.GpuContext.Length > 0) yield return $"--gpu-context={cfg.GpuContext}";
    }

    /// <summary>mpv arguments that only make sense when we own the window.</summary>
    private static IEnumerable<string> HostedMpvArgs(IntPtr hostVideoWindow)
    {
        // --wid takes a plain integer. On 64-bit it must be the full handle
        // value; formatting it as anything but a decimal number is silently
        // ignored by mpv and you get a second, detached window instead.
        yield return $"--wid={hostVideoWindow.ToInt64().ToString(CultureInfo.InvariantCulture)}";

        // The host owns the frame and the fullscreen state, so mpv must not
        // decorate its child or resize it to the video's aspect ratio.
        yield return "--no-border";
        yield return "--no-keepaspect-window";

        // mpv inherits our stdin. Without this it competes with the bridge's own
        // console key reader for every keystroke typed at the terminal, and the
        // two silently split them.
        yield return "--no-input-terminal";
    }

    private void StartDragWatcher(IntPtr hostVideoWindow, PlayerModeController mode, CancellationToken ct)
    {
        // Left-drag to look. Watched at the Win32 level rather than through mpv,
        // because uosc holds MBTN_LEFT with a forced binding and does not pass
        // it on.
        Func<IntPtr> target = hostVideoWindow != IntPtr.Zero
            ? () => hostVideoWindow
            : () => MouseDragWatcher.FindLargestVisibleWindow(_launcher?.ProcessId ?? 0);

        var watcher = new MouseDragWatcher(target,
                                           _cfg.Input.ControlBarBandFraction,
                                           _cfg.Input.RequireForeground);
        _dragWatcher = watcher;

        // A leading '~' marks the routine cases (you are simply using another
        // app); those would otherwise print on every click anywhere on the desktop.
        watcher.Rejected += why =>
        {
            if (!why.StartsWith('~')) Console.WriteLine($"\n  [drag] press ignored: {why}");
        };
        watcher.DragBegin += () =>
        {
            Console.WriteLine("\n  [drag] begin");
            Mapper.BeginManualOverride();
        };
        watcher.DragDelta += (dx, dy) =>
            // Deltas are a fraction of the client width, so a full-width drag is
            // exactly one FOV on any window at any display scaling. Vertical uses
            // the same scale deliberately: angular density per pixel is uniform
            // in a rectilinear projection with square pixels.
            Mapper.ApplyManualDelta(dx * mode.FovDegrees, dy * mode.FovDegrees);
        watcher.DragEnd += () => Mapper.EndManualOverride();

        if (_cfg.Input.DragToLook) watcher.Start(ct);
    }

    private void HandleClientMessage(string[] a, PlayerModeController mode, MpvIpcClient ipc)
    {
        if (a.Length < 2 || a[0] != "headtrack") return;
        Console.WriteLine($"\n  [mpv] {string.Join(' ', a)}");
        switch (a[1])
        {
            case "recenter":
                Mapper.RequestRecenter();
                _ = ipc.ShowTextAsync(UiStrings.Current["osd.recentred"]);
                break;
            case "reset-view":
                Mapper.ResetView();
                _ = ipc.ShowTextAsync(UiStrings.Current["osd.viewReset"]);
                break;

            // Nudge the view by a fixed number of degrees, positive right and up.
            //
            // The yaw sign is flipped on the way in because ApplyManualDelta
            // speaks "grab the world": a drag to the right turns the view left.
            // That is the correct feel for a mouse holding onto the picture and
            // the wrong one for an arrow key, where right means look right.
            //
            // No BeginManualOverride: that freezes head tracking until the
            // matching End, which is right for a drag that owns the mouse and
            // wrong for a keypress. The nudge lands in the same manual offset a
            // drag uses, so the two compose instead of fighting.
            case "look" when a.Length > 3 &&
                             double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lyaw) &&
                             double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lpitch):
                Mapper.ApplyManualDelta(-lyaw, lpitch);
                break;
            case "toggle":
                TrackingEnabled = !TrackingEnabled;
                _ = ipc.ShowTextAsync(UiStrings.Current[TrackingEnabled ? "osd.trackingOn" : "osd.trackingOff"]);
                break;
            // ---- player mode, driven by the OSD menu -----------------------
            case "set-geometry" when a.Length > 2 && PlayerModeController.ParseGeometry(a[2]) is { } g:
                _ = mode.SetGeometryAsync(g);
                break;
            case "set-stereo" when a.Length > 2 && PlayerModeController.ParseStereo(a[2]) is { } st:
                _ = mode.SetStereoAsync(st);
                break;
            case "set-eye" when a.Length > 2 && PlayerModeController.ParseEye(a[2]) is { } ey:
                _ = mode.SetEyeAsync(ey);
                break;
            // Reachable from a key binding as well as the menu: see CycleRenderer.
            //
            // Two statements, not `RendererChanged?.Invoke(CycleRenderer())`.
            // Null-conditional short-circuits the *entire* invocation
            // expression, arguments included, so with no subscriber attached
            // the cycle silently never ran — no error, no effect, nothing in
            // the log to explain it.
            case "cycle-renderer":
                var picked = CycleRenderer();
                RendererChanged?.Invoke(picked);
                break;
            case "set-decoder" when a.Length > 2 &&
                 VideoBackends.Decoders.FirstOrDefault(d => d.MpvValue == a[2]) is { Key: not null } dec:
                _ = SetDecoderAsync(dec);
                break;

            case "cycle-geometry": _ = mode.CycleGeometryAsync(); break;
            case "cycle-stereo": _ = mode.CycleStereoAsync(); break;
            case "cycle-eye": _ = mode.CycleEyeAsync(); break;
            case "request-state":
                _ = mode.BroadcastAsync();
                _ = BroadcastTrackerStateAsync();
                break;

            case "set-fov" when a.Length > 2 && double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fv):
                _ = mode.SetFovAsync(fv);
                break;
            case "adjust-fov" when a.Length > 2 && double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dfv):
                _ = mode.AdjustFovAsync(dfv);
                break;

            // The mode panel owns the mouse while it is open, so its buttons are
            // not also draggable.
            case "menu-open": SuppressDrag = true; break;
            case "menu-closed": SuppressDrag = false; break;

            case "gain-up":
            case "gain-down":
                AdjustGain(a[1] == "gain-up" ? 1.15 : 1 / 1.15);
                break;
        }
    }

    // ------------------------------------------------------------- gain ---

    /// <summary>How many view degrees a full head turn produces, for the menu label.</summary>
    public double YawGain => _cfg.Yaw.OutputRangeDegrees;

    /// <summary>
    /// Scale the response of both axes together.
    ///
    /// Together rather than separately because what people are adjusting is one
    /// thing — how twitchy the view feels — and two independent numbers that
    /// have to be kept in proportion is a worse control than one.
    ///
    /// Saved immediately: this is tuned by feel across sessions, and the
    /// keyboard version of it used to change the running config and throw the
    /// result away on exit.
    /// </summary>
    public void AdjustGain(double factor)
    {
        _cfg.Yaw.OutputRangeDegrees = Math.Clamp(_cfg.Yaw.OutputRangeDegrees * factor, 10, 360);
        _cfg.Pitch.OutputRangeDegrees = Math.Clamp(_cfg.Pitch.OutputRangeDegrees * factor, 5, 85);
        AnnounceGain();
    }

    public void ResetGain()
    {
        _cfg.Yaw.OutputRangeDegrees = AxisConfig.DefaultYaw().OutputRangeDegrees;
        _cfg.Pitch.OutputRangeDegrees = AxisConfig.DefaultPitch().OutputRangeDegrees;
        AnnounceGain();
    }

    private void AnnounceGain()
    {
        SaveConfig();
        _ = Ipc?.ShowTextAsync(UiStrings.Current.F("osd.gain",
            _cfg.Yaw.OutputRangeDegrees.ToString("F0", CultureInfo.CurrentCulture),
            _cfg.Pitch.OutputRangeDegrees.ToString("F0", CultureInfo.CurrentCulture)));
    }

    // ------------------------------------------------- decoder / renderer ---

    /// <summary>
    /// Switch hardware decoding and report what mpv is *actually* doing.
    ///
    /// Reading the value back is the whole point. Setting `hwdec` always
    /// succeeds — mpv accepts the name, then falls back to software if the
    /// decoder cannot be initialised, and the only place that shows is
    /// `hwdec-current`. Measured here: setting nvdec on the d3d11 backend
    /// returns success, `hwdec` reads back "nvdec", and `hwdec-current` reads
    /// "no". Trusting the write would report a 3x speedup that did not happen.
    /// </summary>
    public async Task<string> SetDecoderAsync(VideoBackends.Decoder decoder)
    {
        if (Ipc is not { } ipc) return "";

        // Empty means "stop overriding" — hand control back to mpv.conf, which
        // needs the value it configured putting back explicitly, since the
        // running mpv still has whatever was set last.
        var mpvValue = decoder.MpvValue.Length > 0 ? decoder.MpvValue : DefaultHwdec;
        await ipc.SetPropertyAsync("hwdec", mpvValue);
        await Task.Delay(400);   // the decoder reinitialises on the next frame
        var actual = await ipc.GetStringPropertyAsync("hwdec-current") ?? "no";

        _cfg.Mpv.Hwdec = decoder.MpvValue;
        SaveConfig();

        var wantedHardware = !mpvValue.Equals("no", StringComparison.OrdinalIgnoreCase);
        var gotHardware = !actual.Equals("no", StringComparison.OrdinalIgnoreCase);

        if (wantedHardware && !gotHardware)
        {
            // Name the renderer it needs rather than just reporting failure.
            // Without that the user is told "not available here" about a
            // decoder their GPU fully supports, and has no way to know the
            // renderer is what stands in the way.
            var fix = VideoBackends.RendererFor(decoder);
            var advice = fix is { } r && !VideoBackends.Supports(decoder, CurrentGpuApi)
                ? UiStrings.Current.F("osd.decoderNeedsRenderer", mpvValue, r.GpuApi)
                : UiStrings.Current.F("osd.decoderUnavailable", mpvValue);
            await ipc.ShowTextAsync(advice, 4000);
            if (fix is { } need) DecoderNeedsRenderer?.Invoke(decoder, need);
        }
        else
        {
            await ipc.ShowTextAsync(UiStrings.Current.F("osd.decoder", actual), 2500);
        }

        Console.WriteLine($"\n  [video] hwdec={mpvValue} -> actually {actual}");
        return actual;
    }

    // ------------------------------------------------------- view limits ---

    private double _viewportAspect = 16.0 / 9.0;

    /// <summary>
    /// Stop the view at the edge of the picture.
    ///
    /// The limit is how far the centre of the view can turn before the edge of
    /// the source enters the viewport, so it depends on three things: how much
    /// of the sphere the source covers, how wide the field of view is, and — for
    /// the vertical axis — the shape of the window. A wider FOV leaves less room
    /// to turn, and once FOV reaches the coverage the limit is zero and the view
    /// is pinned, which is correct: the picture already fills the screen.
    /// </summary>
    private void UpdateViewLimits()
    {
        if (Mode is not { } mode) return;

        var (coverH, coverV) = Coverage(mode.Geometry);
        var vFov = VerticalFov(mode.FovDegrees, _viewportAspect);

        Mapper.SetContentLimits(
            double.IsInfinity(coverH) ? double.PositiveInfinity : Math.Max(0, (coverH - mode.FovDegrees) / 2),
            double.IsInfinity(coverV) ? double.PositiveInfinity : Math.Max(0, (coverV - vFov) / 2));
    }

    /// <summary>Degrees of sphere the source covers, horizontally and vertically.</summary>
    private static (double H, double V) Coverage(Geometry g) => g switch
    {
        // Closes on itself horizontally, so there is no horizontal edge to hit.
        Geometry.Deg360 or Geometry.Eac or Geometry.Cylindrical => (double.PositiveInfinity, 180),

        // Half a sphere. Fisheye lenses are 180 or a little more; treating the
        // extra as zero errs towards stopping slightly early, which is far less
        // objectionable than showing the void.
        Geometry.Deg180 or Geometry.Fisheye => (180, 180),

        // The 360 pipeline is off; there is no camera to constrain.
        _ => (double.PositiveInfinity, double.PositiveInfinity),
    };

    private static double VerticalFov(double horizontalFovDegrees, double aspect)
    {
        var h = horizontalFovDegrees * Math.PI / 180.0;
        return 2 * Math.Atan(Math.Tan(h / 2) / Math.Max(0.1, aspect)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// What mpv.conf pins. Kept in sync by hand with the one line in mpv.conf,
    /// because mpv gives no way to ask "what would this option be without my
    /// override" once the override has been applied.
    /// </summary>
    private const string DefaultHwdec = "d3d11va";

    /// <summary>The GPU API in force: the override if set, otherwise mpv.conf's.</summary>
    public string CurrentGpuApi => _cfg.Mpv.GpuApi.Length > 0 ? _cfg.Mpv.GpuApi : "d3d11";

    /// <summary>
    /// Raised when a chosen decoder cannot work with the current renderer, so
    /// the window can offer to change both at once instead of leaving the user
    /// to connect the two menus themselves.
    /// </summary>
    public event Action<VideoBackends.Decoder, VideoBackends.Renderer>? DecoderNeedsRenderer;

    /// <summary>
    /// Record the renderer to use. Takes effect the next time mpv starts.
    ///
    /// Not applied live, even though `vo` alone can be changed at runtime. The
    /// GPU API and context underneath it cannot: mpv creates the graphics
    /// context when the video output initialises, and setting `gpu-api`
    /// afterwards is accepted without re-creating anything — measured here,
    /// switching to opengl left the log still reporting `[vo/gpu/d3d11]`.
    ///
    /// Applying only the half that works would be worse than not applying it:
    /// three of the five entries below differ from each other *only* in the API,
    /// so they would all appear to succeed while changing nothing, and the user
    /// hunting a working renderer would conclude none of them help. Restarting
    /// is slower and honest.
    /// </summary>
    public bool SetRenderer(VideoBackends.Renderer renderer)
    {
        _cfg.Mpv.Vo = renderer.Vo;
        _cfg.Mpv.GpuApi = renderer.GpuApi;
        _cfg.Mpv.GpuContext = renderer.GpuContext;
        SaveConfig();

        var described = renderer.Vo + (renderer.GpuApi.Length > 0 ? $" / {renderer.GpuApi}" : "")
                                    + (renderer.GpuContext.Length > 0 ? $" / {renderer.GpuContext}" : "");
        Console.WriteLine($"\n  [video] renderer set to {described}; applies on restart");
        _ = Ipc?.ShowTextAsync(UiStrings.Current.F("osd.renderer", described), 3500);
        return true;
    }

    /// <summary>Which entry the config currently selects; the first one when nothing is set.</summary>
    public int CurrentRendererIndex
    {
        get
        {
            if (_cfg.Mpv.Vo.Length == 0) return 0;

            // GpuContext has to be part of the comparison: "gpu / opengl" and
            // "gpu / opengl / angle" are different entries that differ in
            // nothing else, and matching on two fields made cycling stick
            // between them forever instead of reaching Direct3D 9.
            var i = Array.FindIndex(VideoBackends.Renderers, r =>
                r.Vo.Equals(_cfg.Mpv.Vo, StringComparison.OrdinalIgnoreCase) &&
                r.GpuApi.Equals(_cfg.Mpv.GpuApi, StringComparison.OrdinalIgnoreCase) &&
                r.GpuContext.Equals(_cfg.Mpv.GpuContext, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? 0 : i;
        }
    }

    /// <summary>
    /// Step to the next renderer in the ladder.
    ///
    /// Bound to a key as well as sitting in the menu, because the situation
    /// this exists for is "the video is black". A menu you cannot see is no
    /// use; a keystroke still works, and the OSD message that follows names
    /// what it landed on.
    /// </summary>
    public VideoBackends.Renderer CycleRenderer()
    {
        var list = VideoBackends.Renderers;
        var next = list[(CurrentRendererIndex + 1) % list.Length];
        SetRenderer(next);
        return next;
    }

    /// <summary>Current decoder as mpv reports it, for the menu label.</summary>
    public Task<string?> GetActiveDecoderAsync() =>
        Ipc?.GetStringPropertyAsync("hwdec-current") ?? Task.FromResult<string?>(null);

    public Task<string?> GetActiveRendererAsync() =>
        Ipc?.GetStringPropertyAsync("current-vo") ?? Task.FromResult<string?>(null);

    private WindowConfig? _window;

    /// <summary>Where the window was last left. Zero width = never saved.</summary>
    public WindowConfig WindowPlacement => _window ??= WindowConfig.Load(AppPaths.WindowStateFile);

    /// <summary>
    /// Remember the window's placement. Written immediately rather than at
    /// exit, for the same reason the tracking toggle is: the player is usually
    /// closed by closing the window, and anything deferred to shutdown is the
    /// first thing lost when it does not shut down cleanly.
    /// </summary>
    public void SaveWindowPlacement(int x, int y, int width, int height, bool maximized)
    {
        var w = WindowPlacement;
        if (w.X == x && w.Y == y && w.Width == width &&
            w.Height == height && w.Maximized == maximized) return;

        (w.X, w.Y, w.Width, w.Height, w.Maximized) = (x, y, width, height, maximized);
        w.Save(AppPaths.WindowStateFile);
    }

    private void SaveConfig()
    {
        try { _cfg.Save(_configPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"\n  [video] could not save the setting to {_configPath}: {e.Message}");
        }
    }

    // -------------------------------------------------------- output loop ---

    /// <summary>Pushes the mapped view to mpv until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Ipc is null || _driver is null) return;

        var lastStatus = 0.0;
        var lastTick = 0.0;
        var wasStale = false;

        using var outputTimer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, _cfg.OutputRateHz)));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await outputTimer.WaitForNextTickAsync(ct)) break;
            }
            catch (OperationCanceledException) { break; }

            var now = Clock.Seconds;

            // Here rather than on pose arrival: this loop ticks steadily at
            // OutputRateHz whatever the tracker is doing, which is exactly what
            // is needed to fill the gaps between sparse poses.
            var dt = lastTick > 0 ? now - lastTick : 0;
            lastTick = now;
            Mapper.Advance(dt);

            if (Mapper.CheckStale(now) && !wasStale)
            {
                wasStale = true;
                await Ipc.ShowTextAsync(UiStrings.Current["osd.trackingLost"], 2000);
            }
            else if (Mapper.HasSignal && wasStale)
            {
                wasStale = false;
                await Ipc.ShowTextAsync(UiStrings.Current["osd.trackingBack"]);
            }

            // Unconditional. This pushes whatever the mapper currently holds,
            // and the mouse drag writes to the same mapper — gating it on
            // TrackingEnabled killed drag-to-look the moment head tracking
            // defaulted to off. What TrackingEnabled controls is whether head
            // poses get *into* the mapper, which is done where they arrive.
            await _driver.PushAsync(Mapper.Current);

            if (now - lastStatus > 0.5)
            {
                lastStatus = now;
                var rel = Mapper.CurrentRelative;
                var v = Mapper.Current;
                var state = _source is null ? "  drag"
                          : !TrackingEnabled ? "PAUSED"
                          : Mapper.HasSignal ? "  live"
                          : "  LOST";
                Console.Write(string.Create(CultureInfo.InvariantCulture,
                    $"\r[{state}] head y{rel.YawDegrees,6:F1} p{rel.PitchDegrees,6:F1}  ->  view y{v.YawDegrees,7:F1} p{v.PitchDegrees,6:F1}  writes {_driver.WriteCount,8}   "));
            }
        }
    }

    // ------------------------------------------------------ video layout ---

    /// <summary>
    /// Decide the mode for the loaded file and push it to mpv360 once. Waits for
    /// the file to load, because width/height are unavailable until then.
    ///
    /// <see cref="_currentMedia"/> is set as soon as the path is known and
    /// before any mode is applied. That ordering matters: applying a mode fires
    /// Changed, which saves to the mode memory, and if _currentMedia still
    /// pointed at the previous file the new file's mode would be written to the
    /// old file's entry.
    /// </summary>
    private async Task ApplyLayoutAsync(MpvIpcClient ipc, PlayerModeController mode)
    {
        var forced = VideoFormat.Parse(_cfg.Video.Projection);

        // Four seconds when a file was asked for, half a second when one was
        // not. Waiting the full time for a player started with no file only
        // delays the point at which it admits there is nothing to detect — and
        // during that wait the mode on screen is one nothing has applied.
        var attempts = string.IsNullOrWhiteSpace(_cli.MediaPath) ? 5 : 40;

        int? w = null, h = null;
        for (var i = 0; i < attempts && w is null; i++)
        {
            w = await ipc.GetIntPropertyAsync("width");
            h = await ipc.GetIntPropertyAsync("height");
            if (w is null) await Task.Delay(100);
        }

        var path = await ipc.GetStringPropertyAsync("path");
        _currentMedia = path;

        if (w is null || h is null)
        {
            Console.WriteLine("video layout    : no file loaded yet; mode will be set when one opens");
            return;
        }

        // Precedence, strongest first. Each step only runs because the one
        // above it had nothing to say, so the weakest answer — a blanket
        // default — can never override something that actually knows.
        //
        //   1. --projection / config   the user, explicitly, for this run
        //   2. this file's memory      the user, previously, for this file
        //   3. confident detection     the filename or an unambiguous shape
        //   4. sticky last-used        the user, previously, for some other file
        //   5. configured fallback     VR180 SBS
        Geometry geometry;
        Stereo stereo;
        Eye eye;
        string reason;
        double? fov = null;

        if (forced is { } p)
        {
            (geometry, stereo) = ViewMode.FromProjection(p);
            eye = _cfg.Video.Eye;
            reason = $"forced by config/CLI, {w}x{h}";
        }
        else if (_cfg.Ui.RememberPerFileMode && _memory!.Get(path) is { } saved)
        {
            (geometry, stereo, eye, fov) = (saved.Geometry, saved.Stereo, saved.Eye, saved.FovDegrees);
            reason = $"remembered for this file ({w}x{h})";
        }
        else
        {
            var detected = VideoFormat.Detect(w.Value, h.Value, path);

            if (detected.IsConfident)
            {
                (geometry, stereo) = (detected.Geometry, detected.Stereo);
                eye = _cfg.Video.AutoEye ? detected.Eye : _cfg.Video.Eye;
                reason = detected.Reason;
            }
            else if (_cfg.Video.StickyMode && _memory!.LastUsed is { } last)
            {
                (geometry, stereo, eye, fov) = (last.Geometry, last.Stereo, last.Eye, last.FovDegrees);
                reason = $"{detected.Reason}; reusing the mode you last chose";
            }
            else
            {
                var fallback = VideoFormat.Parse(_cfg.Video.Fallback) ?? Projection.DualHalfEquirectangular;
                (geometry, stereo) = ViewMode.FromProjection(fallback);
                eye = _cfg.Video.Eye;
                reason = $"{detected.Reason}; using the default ({_cfg.Video.Fallback})";
            }
        }

        await mode.SetAsync(geometry, stereo, eye, ModeOrigin.Automatic);
        if (fov is { } f) await mode.SetFovAsync(f, ModeOrigin.Automatic);
        await AnnounceAsync(ipc, mode);
        Console.WriteLine($"video layout    : {mode.Describe()}");
        Console.WriteLine($"                  {reason}");

        await DescribeSourceAsync(ipc, mode).ConfigureAwait(false);
    }

    /// <summary>
    /// Dump what the file is and what mpv was actually left set to.
    /// </summary>
    /// <remarks>
    /// Both halves, and the second is the point. This player has now been wrong
    /// three separate times in a way where its own state said one thing and mpv
    /// was doing another — the shader silently off while the menu read "180 3D",
    /// a mode never pushed because a toggle went the wrong way, a picture
    /// stretched because mpv still held the file's aspect ratio. In every case
    /// the report was "it says X but I see Y" and nothing in the log could
    /// settle it.
    ///
    /// So this reads the answers back out of mpv rather than printing what we
    /// believe we set: whether the shader is loaded at all, the projection and
    /// eye numbers it is running with, whether the render fills the window.
    /// Read <c>projection</c> against <c>mode</c> on the line above — if they
    /// disagree, the bug is between here and mpv, and if they agree it is not.
    /// </remarks>
    private static async Task DescribeSourceAsync(MpvIpcClient ipc, PlayerModeController mode)
    {
        async Task<string> P(string name)
        {
            var v = await ipc.GetPropertyAsync(name).ConfigureAwait(false);
            if (v is not { } e) return "-";
            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString() ?? "-",
                JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(x => x.ToString())),
                _ => e.ToString(),
            };
        }

        // Let the video pipeline finish coming up first.
        //
        // Mode detection only needs width and height, which mpv reports as soon
        // as the file is parsed, so this runs well before the decoder and the
        // output are configured. Read immediately it produced "decode -",
        // "pixfmt -" and a display size of 960x540 for a 4096x2048 file — all
        // three simply not decided yet. A diagnostic that reports placeholders
        // as facts is worse than one that waits a moment.
        for (var i = 0; i < 20 && await ipc.GetStringPropertyAsync("hwdec-current") is null; i++)
            await Task.Delay(100).ConfigureAwait(false);

        var w = await P("width");
        var h = await P("height");
        var dw = await P("dwidth");
        var dh = await P("dheight");

        var aspect = double.TryParse(w, out var wv) && double.TryParse(h, out var hv) && hv > 0
            ? (wv / hv).ToString("F3", CultureInfo.InvariantCulture)
            : "-";

        Console.WriteLine($"  source        : {await P("filename")}");
        Console.WriteLine($"                  {w}x{h} (display {dw}x{dh}, aspect {aspect})  " +
                          $"{await P("container-fps")} fps  {await P("duration")} s");
        Console.WriteLine($"                  video {await P("video-format")} / {await P("video-codec")}, " +
                          $"pixfmt {await P("video-params/pixelformat")}, " +
                          $"audio {await P("audio-codec-name")}");
        Console.WriteLine($"                  decode {await P("hwdec-current")}, vo {await P("current-vo")}, " +
                          $"file-size {await P("file-size")}");

        // What mpv ended up with, not what we asked for.
        var shaders = await P("glsl-shaders");
        Console.WriteLine($"  mpv360 state  : shader {(shaders.Contains("mpv360", StringComparison.OrdinalIgnoreCase) ? "LOADED" : "NOT LOADED")}  " +
                          $"({(shaders == "-" || shaders.Length == 0 ? "none" : shaders)})");
        Console.WriteLine($"                  opts {await P("glsl-shader-opts")}");
        Console.WriteLine($"                  keepaspect {await P("keepaspect")}, " +
                          $"osd-dimensions {await P("osd-dimensions")}");
        Console.WriteLine($"                  expected projection " +
                          $"{(ViewMode.ToProjection(mode.Geometry, mode.Stereo) is { } p ? $"{(int)p} ({p})" : "none — shader off")}, " +
                          $"eye {(int)mode.Eye} ({mode.Eye})");
    }

    /// <summary>
    /// One OSD line saying what mode the file was opened in.
    ///
    /// The mode bar used to open itself on every file to convey this, which is
    /// how you learn that a 2:1 file was guessed as 360 rather than VR180. That
    /// was too much: the bar takes the mouse, so you had to wait it out before
    /// you could touch anything. A line that fades on its own says the same
    /// thing and blocks nothing — and there are now three ways to reach the
    /// switches if the guess was wrong.
    /// </summary>
    private static Task AnnounceAsync(MpvIpcClient ipc, PlayerModeController mode) =>
        ipc.ShowTextAsync(UiStrings.Current.F("osd.vrMode", mode.Describe()), 2500);

    // ------------------------------------------------------------- console ---

    public static void PrintKeyHelp()
    {
        Console.WriteLine();
        Console.WriteLine("keys (in player window)   keys (in this console)");
        Console.WriteLine("  Tab     mode panel        r  recentre      t  tracking on/off");
        Console.WriteLine("  Ctrl+e  toggle 360        v  reset view    q  quit");
        Console.WriteLine("  Ctrl+t  mpv360 help       [ ]  gain down/up   f  filter on/off");
        Console.WriteLine("                            s  save current tuning to config");
        Console.WriteLine();
    }

    public void StartConsoleKeyReader(CancellationTokenSource cts)
    {
        if (Console.IsInputRedirected) return;

        _ = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                ConsoleKeyInfo k;
                try { k = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { return; }

                switch (char.ToLowerInvariant(k.KeyChar))
                {
                    case 'r': Mapper.RequestRecenter(); Report("recentred"); break;
                    case 'v': Mapper.ResetView(); Report("view reset"); break;
                    case 't': TrackingEnabled = !TrackingEnabled; Report($"tracking {(TrackingEnabled ? "on" : "off")}"); break;
                    case 'f':
                        _cfg.Filter.Enabled = !_cfg.Filter.Enabled;
                        Report($"filter {(_cfg.Filter.Enabled ? "on" : "off")}");
                        break;
                    case '[':
                        _cfg.Yaw.OutputRangeDegrees /= 1.15; _cfg.Pitch.OutputRangeDegrees /= 1.15;
                        Report($"gain yaw {_cfg.Yaw.OutputRangeDegrees:F0} pitch {_cfg.Pitch.OutputRangeDegrees:F0}");
                        break;
                    case ']':
                        _cfg.Yaw.OutputRangeDegrees *= 1.15; _cfg.Pitch.OutputRangeDegrees *= 1.15;
                        Report($"gain yaw {_cfg.Yaw.OutputRangeDegrees:F0} pitch {_cfg.Pitch.OutputRangeDegrees:F0}");
                        break;
                    case 's': _cfg.Save(_configPath); Report($"saved {_configPath}"); break;
                    case 'q': cts.Cancel(); return;
                }
            }
        });

        static void Report(string msg) => Console.WriteLine($"\n  {msg}");
    }

    public void Dispose()
    {
        _dragWatcher?.Dispose();
        Ipc?.Dispose();
        _launcher?.Dispose();
        _recorder?.Dispose();
        _source?.Dispose();
    }
}
