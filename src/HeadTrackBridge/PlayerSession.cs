using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HeadTrackBridge.Input;
using HeadTrackBridge.Mapping;
using HeadTrackBridge.Mpv;
using HeadTrackBridge.Tracking;
using HeadTrackBridge.Tracking.Hand;

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

    /// <summary>What drives head poses: a camera, opentrack over UDP, a replay, or nothing.</summary>
    private ITrackingSource? _source;

    /// <summary>
    /// The camera, when one is open. Usually the same object as
    /// <see cref="_source"/>, and deliberately a separate field.
    /// </summary>
    /// <remarks>
    /// The two came apart when gesture control arrived. Head poses may come from
    /// opentrack while gestures still need a camera, so "the thing producing
    /// poses" and "the thing holding the device" stopped being the same
    /// question — and only one of them may open the camera, because a camera
    /// opens once.
    /// </remarks>
    private CameraFaceSource? _camera;

    /// <summary>The camera, for the preview and the gesture overlay to read.</summary>
    public CameraFaceSource? Camera => _camera;
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

    /// <summary>
    /// A click on the video that was not a drag. Raised on the watcher's thread.
    ///
    /// The window decides what it means, and today it means one thing: with no
    /// file open, the black rectangle is the most obvious place to click and it
    /// did nothing at all. It stays silent during playback — a click there is
    /// how you stop a drag, and pausing on it is a different feature with
    /// different opinions attached.
    /// </summary>
    public event Action? VideoClicked;

    /// <summary>Null until <see cref="StartAsync"/> has connected.</summary>
    public MpvIpcClient? Ipc { get; private set; }

    public PlayerModeController? Mode { get; private set; }

    /// <summary>
    /// Head tracking on/off, backed by the config so the choice survives a
    /// restart. Saving on every change rather than at exit: the player is
    /// often closed by closing the window, and an exit-time save loses whatever
    /// was set in the session that mattered most — the last one.
    /// </summary>
    public bool TrackingEnabled => _cfg.Ui.FaceTracking;

    /// <summary>Gesture control on/off, backed by the config for the same reason.</summary>
    public bool GestureEnabled => _cfg.Ui.GestureControl;

    /// <summary>
    /// True while gesture mode is on — which is also while head tracking is
    /// paused, because the two are the same state seen from either side.
    /// </summary>
    public bool GestureArmed => _camera?.GestureArmed == true;

    /// <summary>Raised when tracking is switched, for the on-screen indicator.</summary>
    public event Action<bool>? TrackingToggled;

    private CancellationToken _trackingCt = CancellationToken.None;

    /// <summary>
    /// Stages the preview modes want regardless of the saved settings, held
    /// apart from the config so a diagnostic run cannot persist them.
    /// </summary>
    private bool _forceFace;
    private bool _forceGesture;

    /// <summary>
    /// Set while the camera has been handed to another process.
    /// </summary>
    /// <remarks>
    /// Separate from the two settings rather than switching them off, because
    /// they must survive: lending the device to the diagnostic and taking it
    /// back has to leave the user's configuration exactly as it was, including
    /// if the player is closed while the diagnostic is still open.
    /// </remarks>
    private bool _suspended;

    /// <summary>
    /// Turn head tracking on or off, opening the camera the first time.
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
    public bool SetTrackingEnabled(bool on) => SetStages(on, GestureEnabled);

    /// <summary>Turn gesture control on or off. Same contract as above.</summary>
    public bool SetGestureEnabled(bool on) => SetStages(TrackingEnabled, on);

    /// <summary>
    /// The one place either stage is switched, and the one place that decides
    /// whether the camera should be open.
    /// </summary>
    /// <remarks>
    /// One method for both because the answer depends on both. The logic used to
    /// be spread across "turn tracking on" and "start the source at launch", and
    /// with a second stage wanting the same device that shape has a hole in it
    /// in each direction: switching head tracking off would close a camera
    /// gesture control was still using, and switching gesture control on while
    /// head tracking already held the device would try to open it twice.
    /// </remarks>
    private bool SetStages(bool face, bool gesture)
    {
        if (face == TrackingEnabled && gesture == GestureEnabled) return true;

        // Only what is being newly switched on is checked. Re-checking a stage
        // that is already running would fail a user who deleted a model file
        // mid-session out of the *other* feature, which they did not touch.
        try
        {
            CameraFaceSource.CheckModels(_cfg.Source.Camera,
                                         face && !TrackingEnabled,
                                         gesture && !GestureEnabled);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not start the camera: {ex.Message}");
            CameraStartFailed?.Invoke(ex.Message);
            return false;
        }

        var (wasFace, wasGesture, wasKind) =
            (_cfg.Ui.FaceTracking, _cfg.Ui.GestureControl, _cfg.Source.Kind);

        _cfg.Ui.FaceTracking = face;
        _cfg.Ui.GestureControl = gesture;

        // Remembered, so the next launch brings the camera up without going
        // through the menu again. Only from None: with opentrack or a replay
        // configured, "turn head tracking on" means that source, not a webcam.
        if (face && _cfg.Source.Kind == SourceKind.None) _cfg.Source.Kind = SourceKind.Camera;

        if (!UpdateCamera())
        {
            (_cfg.Ui.FaceTracking, _cfg.Ui.GestureControl, _cfg.Source.Kind) =
                (wasFace, wasGesture, wasKind);
            return false;
        }

        SaveConfig();
        if (face != wasFace) TrackingToggled?.Invoke(face);
        _ = BroadcastTrackerStateAsync();
        return true;
    }

    /// <summary>The size the camera is actually running at, or the configured one before it opens.</summary>
    public (int Width, int Height) CameraSize =>
        _camera is { OpenedWidth: > 0 } c ? (c.OpenedWidth, c.OpenedHeight)
                                          : (_cfg.Source.Camera.Width, _cfg.Source.Camera.Height);

    /// <summary>
    /// Change the capture resolution, reopening the device if it is running.
    /// </summary>
    /// <remarks>
    /// A reopen, because a resolution is fixed when the device is opened — this
    /// is the one camera setting that cannot be flipped on a running loop the way
    /// the two stages can. It costs the best part of a second, which is why it is
    /// a menu item and not something a slider does continuously.
    ///
    /// The size is saved whether or not the camera takes it. A webcam handed a
    /// mode it does not have substitutes its nearest one silently, and if the
    /// config were rewritten to match, the choice would appear to have been
    /// ignored *and* forgotten — reconnecting a camera that does support it would
    /// then still open small. What the device actually gave is reported instead,
    /// by <see cref="CameraSize"/> and in the log.
    /// </remarks>
    public bool SetCameraResolution(int width, int height)
    {
        var cam = _cfg.Source.Camera;
        if (cam.Width == width && cam.Height == height) return true;

        var (wasW, wasH) = (cam.Width, cam.Height);
        cam.Width = width;
        cam.Height = height;

        if (_camera is { } running)
        {
            _ = Ipc?.SendAsync("script-message-to", "vrmenu", "hand-preview", "off", "0", "", "-");
            running.Dispose();
            if (ReferenceEquals(_source, running)) _source = null;
            _camera = null;

            if (!UpdateCamera())
            {
                (cam.Width, cam.Height) = (wasW, wasH);
                UpdateCamera();
                return false;
            }
        }

        SaveConfig();
        return true;
    }

    /// <summary>
    /// Open, reconfigure or release the camera so that it matches what the two
    /// stages currently want. False only when opening it failed.
    /// </summary>
    private bool UpdateCamera()
    {
        // Head tracking only drives the face stage when the camera is what
        // produces poses. With opentrack configured, head tracking is on and the
        // face stage is not — but gesture control may still want the device.
        var wantFace = !_suspended && (TrackingEnabled || _forceFace)
                       && _cfg.Source.Kind == SourceKind.Camera;
        var wantGesture = !_suspended && (GestureEnabled || _forceGesture);

        if (!wantFace && !wantGesture)
        {
            // Releasing, not merely ignoring. Leaving the source alive kept the
            // device open, so Test Camera stayed "busy" forever and a restart
            // could not open it either — the config now said the source was a
            // camera, so the player grabbed it again on startup. A camera opens
            // once; whatever holds it has to let go.
            if (_camera is null) return true;
            _ = Ipc?.SendAsync("script-message-to", "vrmenu", "hand-preview", "off", "0", "", "-");
            _camera.Dispose();
            if (ReferenceEquals(_source, _camera)) _source = null;
            _camera = null;
            Console.WriteLine("camera          : released — nothing is using it");
            return true;
        }

        // Already open: flip the stages rather than reopening. Reopening a
        // DirectShow device takes the best part of a second, and doing it to
        // switch on gestures would blank head tracking while it happened.
        if (_camera is { } running)
        {
            running.FaceEnabled = wantFace;
            // The panel is fed by the stage, so switching the stage off has to take
            // it down explicitly — nothing else will ever send again.
            if (!wantGesture)
                _ = Ipc?.SendAsync("script-message-to", "vrmenu", "hand-preview", "off", "0", "", "-");
            running.GestureEnabled = wantGesture;
            Console.WriteLine($"camera          : face stage {(wantFace ? "on" : "off")}, " +
                              $"gesture stage {(wantGesture ? "on" : "off")}");
            return true;
        }

        try
        {
            var camera = new CameraFaceSource(_cfg.Source.Camera, wantFace, wantGesture, _cli.DumpUdp);
            camera.PoseReceived += p =>
            {
                _recorder?.Write(p);
                // The one place head tracking is switched on and off. Recording
                // still happens either way, so a session can be captured while
                // the view is being driven by hand.
                if (TrackingEnabled) Mapper.Accept(p);
            };
            camera.GestureFired += OnGestureFired;
            camera.GestureModeChanged += OnGestureModeChanged;
            camera.GestureObserved += OnGestureObserved;
            camera.GestureHandNeverFound += OnHandNeverFound;

            // Said out loud when it differs, because it differs silently. A
            // camera handed a mode it does not have substitutes its nearest one
            // and reports success, so "I selected 4K" and "I am running at 4K"
            // are separate facts and only one of them is on screen.
            camera.Opened += (w, h) =>
            {
                var cam = _cfg.Source.Camera;
                if (w == cam.Width && h == cam.Height) return;
                _ = Ipc?.ShowTextAsync(
                    UiStrings.Current.F("osd.cameraSizeDiffers", cam.Width, cam.Height, w, h), 4000);
            };
            camera.VrMode = Mode is { } m && m.Geometry != Geometry.Flat;
            camera.Start(_trackingCt);

            _camera = camera;
            if (_cfg.Source.Kind == SourceKind.Camera) _source = camera;
            Console.WriteLine($"camera          : {camera.Name}, " +
                              $"face stage {(wantFace ? "on" : "off")}, " +
                              $"gesture stage {(wantGesture ? "on" : "off")}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not start the camera: {ex.Message}");
            CameraStartFailed?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>Raised when switching a stage on could not open the camera.</summary>
    public event Action<string>? CameraStartFailed;

    /// <summary>
    /// Let go of the camera so another process can open it, and report which
    /// stages were running so the caller knows what to ask that process to show.
    /// </summary>
    /// <remarks>
    /// A camera opens once, and the diagnostic is a separate process on purpose
    /// — OpenCV's window pumps its own message loop, and a driver that hangs
    /// should take the diagnostic down rather than the player. Those two facts
    /// together used to make the diagnostic unreachable exactly when it was
    /// wanted: switching gesture control on opened the camera, which made "Test
    /// Camera" refuse with "the camera is busy", and switching gesture control
    /// off to free it meant the diagnostic then had no gesture stage to show.
    ///
    /// Lending the device and taking it back afterwards is the way out, and it
    /// has to be done without touching the settings — see <see cref="_suspended"/>.
    /// </remarks>
    public async Task<(bool Face, bool Gesture)> SuspendCameraAsync()
    {
        var was = (Face: TrackingEnabled && _cfg.Source.Kind == SourceKind.Camera,
                   Gesture: GestureEnabled);
        if (_suspended) return was;

        _suspended = true;

        var camera = _camera;
        if (ReferenceEquals(_source, camera)) _source = null;
        _camera = null;

        if (camera is not null)
        {
            // Off the UI thread. Disposing joins the camera thread, and that
            // thread may be anywhere: blocked inside a frame read, or asleep for
            // up to 400 ms in the duty-cycle budget. Doing it inline freezes the
            // window for as long as that takes.
            await Task.Run(camera.Dispose);

            // A short guard on top, and a small one because it was measured
            // rather than guessed. Closing a DirectShow handle and reopening the
            // same device on this machine works with no wait at all — three runs
            // at 0, 100 and 400 ms all succeeded, and the reopen itself costs
            // about 310 ms whatever the gap. So the settle is not what makes
            // this work; releasing the thread promptly is (see
            // CameraFaceSource.Idle).
            //
            // Kept at 150 anyway: this machine is not the user's, the cost is
            // invisible next to the 310 ms the open takes regardless, and losing
            // the race shows "could not open device 0" — which is also what a
            // genuinely broken webcam says.
            await Task.Delay(150);
        }

        Console.WriteLine("camera          : released for the diagnostic");
        return was;
    }

    /// <summary>Take the camera back, restoring whatever was running before.</summary>
    public void ResumeCamera()
    {
        if (!_suspended) return;
        _suspended = false;
        UpdateCamera();
        _ = BroadcastTrackerStateAsync();
    }

    /// <summary>
    /// A gesture was recognised and resolved to an action. Raised whether or not
    /// there is a player to send it to, which is what makes it useful to the
    /// preview.
    /// </summary>
    public event Action<GestureAction>? GestureRecognised;

    // ------------------------------------------------------------ gestures ---

    /// <summary>
    /// Gesture mode has been entered or left, on the camera thread.
    ///
    /// Announced rather than left to be noticed. Entering it stops head tracking
    /// driving the view, and a picture that quietly stops following your head is
    /// indistinguishable from one that has broken.
    /// </summary>
    private void OnGestureModeChanged(bool on)
    {
        // Only mention head tracking when there is head tracking to mention.
        //
        // The message was unconditional at first, so switching gesture control
        // on by itself announced that head tracking had been paused — to someone
        // who had never turned it on, and whose face indicator was dark. It
        // named a feature they were not using as the thing that had just
        // changed, which is worse than saying nothing.
        var pausesTracking = _camera is { FaceEnabled: true };

        Console.WriteLine("\n  [gesture] mode " +
                          (on ? pausesTracking ? "on — head tracking paused" : "on" : "off"));
        _ = Ipc?.ShowTextAsync(
            UiStrings.Current[on ? pausesTracking ? "osd.gestureOn" : "osd.gestureOnAlone"
                                 : "osd.gestureOff"],
            on ? 2000 : 1200);
        _ = BroadcastTrackerStateAsync();
    }

    /// <summary>
    /// A recognised gesture, on the camera thread.
    /// </summary>
    /// <remarks>
    /// Every action toasts what it did. Without that, "the gesture was not
    /// recognised" and "it was recognised and does something else than you
    /// expected" look identical from the sofa, and they need opposite fixes.
    ///
    /// The steps match the menu entries they stand in for — 5 units of volume, 5
    /// degrees of view — so that reaching for a gesture and reaching for the menu
    /// do the same thing. Seeking is the exception, and
    /// <see cref="SeekForwardSeconds"/> says why.
    /// </remarks>
    private void OnGestureFired(Gesture gesture) => _ = ApplyGestureAsync(gesture);

    /// <summary>
    /// Seconds a seek gesture moves.
    /// </summary>
    /// <remarks>
    /// Ten each way, which is where the gesture stops matching the menu entry it
    /// stands in for — that one still jumps 30 s forward, and so does the control
    /// bar button and the arrow key.
    ///
    /// They are not the same act. A menu entry is chosen once; a gesture is held,
    /// and repeats while it is. Forward at 30 s a step covered 75 seconds of film
    /// for every second the finger was out, which is past the point where the
    /// picture can be read at all — so the step has to fall as the gesture starts
    /// repeating, or the two multiply. With
    /// <see cref="GestureConfig.SeekRepeatSeconds"/> at 0.8 the gesture now moves
    /// 12.5 seconds of film a second, and the two directions are symmetric, which
    /// they have to be for a hand: nothing about holding a finger out to the right
    /// says "three times further than to the left".
    /// </remarks>
    private const int SeekBackSeconds = 10;
    private const int SeekForwardSeconds = 10;

    /// <summary>Volume units a thumb gesture moves, matching the Audio menu.</summary>
    private const int VolumeStep = 5;

    private async Task ApplyGestureAsync(Gesture gesture)
    {
        var vr = Mode is { } mode && mode.Geometry != Geometry.Flat;
        var action = GestureMap.Resolve(gesture, vr);
        if (action == GestureAction.None) return;

        // Logged before anything is dispatched, and before the check for mpv.
        //
        // Both orderings were wrong in the first version. The IPC check came
        // first, so under --gesture-preview — where there is no mpv at all, and
        // which exists precisely to find out whether gestures are recognised —
        // a gesture fired and printed nothing. A session's whole log could show
        // gesture mode being entered and left repeatedly with no way to tell
        // whether a single gesture had ever been read.
        Console.WriteLine($"\n  [gesture] {gesture.Pose} {gesture.Direction}" +
                          $"{(gesture.Swipe ? " swipe" : "")} -> {action}" +
                          (Ipc is null ? "  (no player attached; nothing was sent)" : ""));

        GestureRecognised?.Invoke(action);
        if (Ipc is not { } ipc) return;

        try
        {
            // Null means the action announced itself, which the field of view does
            // because the wheel and the keys need the same message and never
            // pass through here.
            if (await PerformAsync(ipc, action) is { } said) await ipc.ShowTextAsync(said, 1400);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // mpv went away between the gesture and the command. Nothing to say.
        }
    }

    /// <summary>
    /// Carry out an action and describe what it did, with the number in it.
    /// </summary>
    /// <remarks>
    /// The value, not just the verb. "Volume up" leaves you no idea whether you
    /// are at 20 or at 100, and after three repeats of a gesture that is the
    /// only thing you want to know; "back 10 s" does not say where you landed,
    /// which is the entire reason for seeking. This is the same argument the
    /// tracking-sensitivity menu already makes for showing its own number —
    /// "Less Sensitive" with nothing to compare against gives no sense of how
    /// far you have moved or how far is left — and gestures shipped without it.
    ///
    /// Read back from mpv rather than predicted. Volume clamps at 100 and
    /// seeking clamps at both ends of the file, so the number we would have
    /// computed is wrong exactly when the user most needs to know it: at the
    /// limit, where nothing appears to be happening any more.
    /// </remarks>
    private async Task<string?> PerformAsync(MpvIpcClient ipc, GestureAction action)
    {
        var t = UiStrings.Current;

        switch (action)
        {
            case GestureAction.PlayPause:
                await ipc.SendAsync("cycle", "pause");
                return t[await ipc.GetBoolPropertyAsync("pause") == true
                    ? "gesture.paused" : "gesture.playing"];

            case GestureAction.SeekBack:
            case GestureAction.SeekForward:
            {
                var back = action == GestureAction.SeekBack;
                await ipc.SendAsync("seek", back ? -SeekBackSeconds : SeekForwardSeconds);
                var at = await ipc.GetDoublePropertyAsync("time-pos") ?? 0;
                var total = await ipc.GetDoublePropertyAsync("duration") ?? 0;
                return t.F(back ? "gesture.seekBack" : "gesture.seekForward",
                           back ? SeekBackSeconds : SeekForwardSeconds,
                           Timecode(at), Timecode(total));
            }

            case GestureAction.VolumeUp:
            case GestureAction.VolumeDown:
                await ipc.SendAsync("add", "volume",
                                    action == GestureAction.VolumeUp ? VolumeStep : -VolumeStep);
                return t.F("gesture.volumeAt",
                           (await ipc.GetDoublePropertyAsync("volume") ?? 0).ToString("F0", CultureInfo.CurrentCulture));

            case GestureAction.FovNarrower:
            case GestureAction.FovWider:
            {
                if (Mode is not { } mode) return t[GestureMap.Key(action)];
                var step = action == GestureAction.FovNarrower
                    ? -PlayerModeController.FovStepDegrees : PlayerModeController.FovStepDegrees;
                await mode.AdjustFovAsync(step);
                // Null, because the controller announces the new angle itself —
                // it has to, for the wheel and the keys, which do not come
                // through here at all.
                return null;
            }

            case GestureAction.PreviousFile:
            case GestureAction.NextFile:
                // No number worth showing, and no point reading the title back:
                // the next file has not loaded yet when this returns, so it
                // would name the one being left.
                await ipc.SendAsync(action == GestureAction.PreviousFile
                    ? "playlist-prev" : "playlist-next", "weak");
                return t[GestureMap.Key(action)];

            default:
                return t[GestureMap.Key(action)];
        }
    }

    /// <summary>
    /// Seconds as h:mm:ss, dropping the hours for anything under one.
    /// </summary>
    private static string Timecode(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>
    /// Feed the on-screen hand panel: what the camera can see, right now.
    /// </summary>
    /// <remarks>
    /// The reason this exists at all is that gestures, unlike head tracking,
    /// have no feedback until something fires. Turn your head and the picture
    /// moves; hold up a hand and, until a gesture completes, nothing on screen
    /// changes whether the camera is seeing a perfect hand or is pointed at the
    /// ceiling. The three failures — pose not recognised, hand out of frame,
    /// camera never opened — are indistinguishable, and the instinctive
    /// response to all three is to gesture harder.
    /// </remarks>
    private void OnGestureObserved(HandView view)
    {
        _ = Ipc?.SendAsync("script-message-to", "vrmenu", "hand-preview",
                           view.Armed ? "armed" : "looking",
                           view.Hold.ToString("F2", CultureInfo.InvariantCulture),
                           PoseKey(view.Pose),
                           view.Points,
                           view.Edge ? "edge" : "",
                           Mode is { } mode && mode.Geometry != Geometry.Flat ? "vr" : "flat");
    }

    /// <summary>
    /// Gesture control has been on a while and the camera has never found a hand.
    /// </summary>
    /// <remarks>
    /// Said out loud rather than left to be inferred. Everything else about this
    /// feature reports a hand it can see; nothing reports the case where it has
    /// never seen one, which is the case where the user is most certain the
    /// software is at fault. The two things that actually cause it — a camera
    /// aimed above where hands are held, and a room too dark for the detector —
    /// are both fixed in seconds by someone who knows to look.
    /// </remarks>
    private void OnHandNeverFound()
    {
        Console.WriteLine("\n  [gesture] no hand found since gesture control was switched on — " +
                          "check the camera angle and the light");
        _ = Ipc?.SendAsync("script-message-to", "vrmenu", "hand-warning", "no camera hand");
    }

    /// <summary>The label the OSD script knows this pose by. Empty when there is none.</summary>
    private static string PoseKey(Pose pose) => pose switch
    {
        Pose.OpenPalm => "open palm",
        Pose.Fist => "fist",
        Pose.Point => "point",
        Pose.Thumb => "thumb",
        _ => "",
    };

    /// <summary>
    /// Tell the OSD script which indicators to light.
    ///
    /// Three states for the face rather than two, because "you switched it off"
    /// and "it is standing aside while you gesture" are different things and the
    /// second one ends by itself. An icon that simply went dark during every
    /// gesture would read as the feature failing.
    /// </summary>
    public Task BroadcastTrackerStateAsync() =>
        Ipc?.SendAsync("script-message-to", "vrmenu", "tracker-state",
                       !TrackingEnabled ? "off" : GestureArmed ? "paused" : "on",
                       !GestureEnabled ? "off" : GestureArmed ? "on" : "idle")
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
            // The camera is deliberately absent from this switch. Two stages now
            // want it and only one of them is a pose source, so the decision to
            // open it belongs to UpdateCamera below, which can see both.
            _source = _cfg.Source.Kind switch
            {
                SourceKind.None or SourceKind.Camera => null,
                SourceKind.Udp => new OpenTrackUdpSource(_cfg.Source.UdpPort, _cli.DumpUdp),
                SourceKind.Replay => new ReplaySource(AppPaths.Resolve(_cfg.Source.ReplayFile)),
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

        // The remembered kind must not mean seizing the camera at launch for
        // someone who has turned both stages off — that is what made Test
        // Camera unusable until the whole install was replaced. The two preview
        // modes are the exception, since there is nothing else for them to show.
        //
        // Held as overrides rather than written into the config. Setting
        // _cfg.Ui.GestureControl here would be one SaveConfig away from a
        // diagnostic run leaving the feature switched on for good — the same
        // shape as the bug that shipped a development machine's window size.
        _forceFace = _cli.CameraPreview;
        _forceGesture = _cli.GesturePreview;
        UpdateCamera();

        // Read back off the camera rather than off the settings.
        //
        // The first version printed the config, and under --gesture-preview it
        // contradicted the line above it in two directions at once: "head
        // tracking: camera 0" beside "face stage off", and "gesture control:
        // off" beside "gesture stage on". Both were true statements about the
        // saved settings and both were false about what was running, because
        // the preview modes override the settings without writing them.
        //
        // AGENTS.md rule 2, applied to a diagnostic: report what is happening,
        // not what was asked for.
        Console.WriteLine("head tracking   : " +
            (_camera is { FaceEnabled: true } ? _camera.Name
             : _source is { } s ? s.Name
             : "off (mouse drag only) — enable with --source=camera"));
        Console.WriteLine("gesture control : " +
            (_camera is { GestureEnabled: true } ? "on — hold an open palm to the camera" : "off"));
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

            // Which gesture table applies. A flat file has no field of view to
            // adjust, so the thumb means volume there and the view here — and
            // the file can change under a running camera.
            if (_camera is not null) _camera.VrMode = mode.Geometry != Geometry.Flat;

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
        // Not logged here. A click on the video is ordinary during playback —
        // it is how a drag ends — so a line at this level would print on almost
        // every interaction. Whoever acts on it logs that instead.
        watcher.Click += () => VideoClicked?.Invoke();

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
            // SetTrackingEnabled, not the TrackingEnabled property.
            //
            // The property only flips the config flag, saves it, ticks the menu
            // and broadcasts the on-screen icon. It never opens the camera —
            // that lives in the method. So Ctrl+Shift+H turned the face icon
            // green while the camera stayed dark and nothing was tracked, and
            // the state then disagreed with reality: the next menu click was
            // spent switching a camera off that had never been on, so it took
            // two clicks to get a picture. Reported as "I have to toggle it
            // several times before the light comes on", and that is exactly
            // what it was.
            //
            // Three entry points, one of them complete. The other two are here
            // and in the console key below.
            case "toggle":
            {
                var want = !TrackingEnabled;
                var ok = SetTrackingEnabled(want);
                _ = ipc.ShowTextAsync(
                    !ok ? UiStrings.Current["cam.startFailedBody"]
                        : UiStrings.Current[want ? "osd.trackingOn" : "osd.trackingOff"],
                    ok ? 1200 : 3000);
                break;
            }
            // Switches the feature on, which is not the same as entering gesture
            // mode: that takes an open palm held at the camera. Two steps on
            // purpose — the switch is a setting that persists, and gesture mode
            // is a thing you are doing right now.
            case "toggle-gestures":
            {
                var want = !GestureEnabled;
                var ok = SetGestureEnabled(want);
                _ = ipc.ShowTextAsync(
                    !ok ? UiStrings.Current["cam.startFailedBody"]
                        : UiStrings.Current[want ? "osd.gestureEnabled" : "osd.gestureDisabled"],
                    ok ? 2500 : 3000);
                break;
            }
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

    /// <summary>
    /// Writes the settings file now. For the window, which changes settings the
    /// session does not otherwise hear about — the UI language is the first.
    /// </summary>
    public void SaveSettings() => SaveConfig();

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

        // Toast state, deliberately separate from the mapper's own stale flag.
        // See the announcement block below for why the two must not be the same
        // thing.
        var toldStale = false;
        var pendingStale = false;
        var pendingSince = 0.0;
        var sawPose = false;

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

            Mapper.CheckStale(now);

            // Announce a change only once it has held. The mapper freezes the
            // view after half a second without a pose, which is right — the
            // view must not drift on stale data — but half a second is far
            // shorter than the gap between poses when the tracker is running on
            // its CPU budget and the face is only intermittently found. Wired
            // straight to the toast, that produced an endless "signal lost /
            // signal back" flicker over the video while tracking was in fact
            // working as designed.
            //
            // With the hold, a flapping signal says nothing at all and a face
            // that really leaves says it once.
            const double announceAfterSeconds = 2.0;

            // Gesture mode counts as "not tracking" here, and that is the whole
            // point of naming it: while it is on, no pose is meant to arrive,
            // so every one of the states below would otherwise conclude the
            // tracker had died and say so over the video. It is a deliberate
            // pause, and OnGestureModeChanged has already announced it.
            var tracking = _source is not null && TrackingEnabled && !GestureArmed;
            if (!tracking)
            {
                // Switching tracking off is not signal loss, and must not toast.
                sawPose = toldStale = pendingStale = false;
            }
            else
            {
                if (Mapper.HasSignal) sawPose = true;

                // Nothing to lose before the first pose ever arrives: the camera
                // takes a moment to open, and that silence is not a fault.
                var stale = sawPose && !Mapper.HasSignal;
                if (stale != pendingStale)
                {
                    pendingStale = stale;
                    pendingSince = now;
                }
                else if (stale != toldStale && now - pendingSince >= announceAfterSeconds)
                {
                    toldStale = stale;
                    await Ipc.ShowTextAsync(
                        UiStrings.Current[stale ? "osd.trackingLost" : "osd.trackingBack"],
                        stale ? 2000 : 1200);
                }
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
                var state = GestureArmed ? "GESTURE"
                          : _source is null ? "   drag"
                          : !TrackingEnabled ? " PAUSED"
                          : Mapper.HasSignal ? "   live"
                          : "   LOST";
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
        Console.WriteLine("  Ctrl+e  toggle 360        v  reset view    g  gestures on/off");
        Console.WriteLine("  Ctrl+t  mpv360 help       [ ]  gain down/up   f  filter on/off");
        Console.WriteLine("                            s  save tuning    q  quit");
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
                    // Through the method, for the same reason as the hotkey
                    // above: the property does not open the camera.
                    case 't':
                        Report(SetTrackingEnabled(!TrackingEnabled)
                            ? $"tracking {(TrackingEnabled ? "on" : "off")}"
                            : "tracking: the camera would not open");
                        break;
                    case 'g':
                        Report(SetGestureEnabled(!GestureEnabled)
                            ? $"gesture control {(GestureEnabled ? "on — hold an open palm to the camera" : "off")}"
                            : "gestures: the camera would not open");
                        break;
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
        // Usually the same object, so the reference check is what stops the
        // camera thread being joined twice.
        if (!ReferenceEquals(_source, _camera)) _source?.Dispose();
        _camera?.Dispose();
    }
}
