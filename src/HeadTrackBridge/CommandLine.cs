using System.Globalization;

namespace HeadTrackBridge;

public sealed class CommandLine
{
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool WriteDefaultConfig { get; private set; }
    public bool DumpUdp { get; private set; }
    public bool CameraPreview { get; private set; }
    public bool GesturePreview { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? MediaPath { get; private set; }

    private SourceKind? _kind;
    private int? _port;
    private int? _cameraIndex;
    private Tracking.SyntheticMode? _synthMode;
    private string? _recordTo;
    private string? _replayFile;
    private double? _yawGain;
    private double? _pitchGain;
    private bool? _autoLaunch;
    private bool? _hostWindow;
    private string? _projection;
    private Mpv.Eye? _eye;

    public static CommandLine Parse(string[] args)
    {
        var c = new CommandLine();
        foreach (var raw in args)
        {
            var (key, value) = Split(raw);
            switch (key)
            {
                case "-h" or "--help": c.ShowHelp = true; break;
                case "-v" or "--version": c.ShowVersion = true; break;
                case "--write-config": c.WriteDefaultConfig = true; break;
                case "--dump": c.DumpUdp = true; break;
                // Implies the camera: there is nothing to preview otherwise,
                // and asking for the preview and then being told to also pass
                // --source would be a pointless extra step.
                case "--camera-preview": c.CameraPreview = true; c._kind = SourceKind.Camera; break;
                // The same window, watching the other stage. It deliberately
                // does not imply the camera *source*: gesture control does not
                // need one, and setting it would turn head tracking on for
                // someone who only wanted to check their hand.
                case "--gesture-preview": c.GesturePreview = true; break;
                case "--camera": c._cameraIndex = int.Parse(value!, CultureInfo.InvariantCulture);
                                 c._kind = SourceKind.Camera; break;
                case "--config": c.ConfigPath = value; break;
                case "--source": c._kind = ParseEnum<SourceKind>(value); break;
                case "--port": c._port = int.Parse(value!, CultureInfo.InvariantCulture); break;
                case "--synthetic": c._synthMode = ParseEnum<Tracking.SyntheticMode>(value); break;
                case "--record": c._recordTo = value; break;
                case "--replay": c._replayFile = value; c._kind = SourceKind.Replay; break;
                case "--yaw-gain": c._yawGain = D(value); break;
                case "--pitch-gain": c._pitchGain = D(value); break;
                // --no-launch implies detached: there is no window to embed
                // into an mpv that was already started by someone else.
                case "--no-launch": c._autoLaunch = false; c._hostWindow = false; break;
                case "--detached": c._hostWindow = false; break;
                case "--projection": c._projection = value; break;
                case "--eye": c._eye = ParseEnum<Mpv.Eye>(value); break;
                default:
                    if (key.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option '{key}'.");
                        c.ShowHelp = true;
                    }
                    else
                    {
                        c.MediaPath = raw;
                    }
                    break;
            }
        }
        return c;
    }

    private static (string Key, string? Value) Split(string arg)
    {
        var i = arg.IndexOf('=');
        return i < 0 ? (arg, null) : (arg[..i], arg[(i + 1)..]);
    }

    private static double D(string? s) => double.Parse(s!, CultureInfo.InvariantCulture);

    private static T ParseEnum<T>(string? s) where T : struct, Enum =>
        Enum.Parse<T>(s ?? "", ignoreCase: true);

    public void ApplyOverrides(BridgeConfig cfg)
    {
        if (_kind is { } k) cfg.Source.Kind = k;
        if (_port is { } p) cfg.Source.UdpPort = p;
        if (_cameraIndex is { } ci) cfg.Source.Camera.DeviceIndex = ci;
        if (_synthMode is { } m) { cfg.Source.SyntheticMode = m; cfg.Source.Kind = SourceKind.Synthetic; }
        if (_recordTo is not null) cfg.Source.RecordTo = _recordTo;
        if (_replayFile is not null) cfg.Source.ReplayFile = _replayFile;
        if (_yawGain is { } yg) cfg.Yaw.OutputRangeDegrees = yg;
        if (_pitchGain is { } pg) cfg.Pitch.OutputRangeDegrees = pg;
        if (_autoLaunch is { } al) cfg.Mpv.AutoLaunch = al;
        if (_hostWindow is { } hw) cfg.Ui.HostWindow = hw;
        if (_projection is not null) cfg.Video.Projection = _projection;
        if (_eye is { } e) { cfg.Video.Eye = e; cfg.Video.AutoEye = false; }
    }

    public static void PrintHelp() => Console.WriteLine("""
        VR Flat Player — watch 180/360 VR video on a flat monitor.

        usage: VRFlatPlayer [options] [video-file]

        head tracking (off by default — the player works on mouse drag alone)
          --source=none|camera|udp|synthetic|replay
                                          none    no head tracking (default)
                                          camera  webcam face tracking, built in
                                          udp     opentrack, if you already run it
                                          synthetic  FAKE tracker for a machine
                                            with no webcam. `mouse` mode maps the
                                            CURSOR to head angles, so the view
                                            follows the pointer and you cannot
                                            use the UI — testing only.
                                          replay  a recorded session
          --camera=0                      which webcam (implies --source=camera)
          --camera-preview                show what the tracker sees, with the
                                          landmarks and head direction drawn on
                                          top. Use this first — it separates
                                          "camera will not open" from "no face
                                          found" from "pose is wrong", which the
                                          numbers alone cannot. Esc to close.
          --gesture-preview               the same window for hand gestures: the
                                          21 landmarks, the pose that was read,
                                          and which fingers it counted as out.
                                          When a gesture will not register, that
                                          last one is what says why. Esc to close.
          --port=4242                     UDP port for opentrack output
          --synthetic=mouse|sweep|still   fake source, for a machine with no webcam
          --replay=recordings/a.csv       replay a recorded session (implies --source=replay)
          --record=recordings/a.csv       write every incoming pose to CSV

        video layout
          --projection=auto|vr180|360|...  a mono 360 file and a VR180 side-by-side
                                           file are both 2:1, so auto-detect uses the
                                           filename and falls back to 360. Override here.
                                           names: equirectangular, dual-half-equirect
                                           (= vr180), half-equirect, dual-fisheye,
                                           cylindrical, eac, or an index 0-7
          --eye=left|right|both            flat monitor wants one eye (default: left)

        tuning
          --yaw-gain=150                  view degrees at full head turn
          --pitch-gain=70

        window
          --detached                      put mpv back in its own window and run
                                          the bridge as a sidecar. The default is
                                          to host mpv inside our window, which is
                                          what provides the native menu bar.

        other
          --config=path.json              config file. Default: bridge.config.json next
                                          to the exe, or in %APPDATA%\VR Flat Player
                                          when the install directory is read-only
          --write-config                  write the effective config and exit
          --no-launch                     do not start mpv; attach to a running one
                                          (implies --detached)
          --dump                          print incoming poses and exit on Ctrl+C (no mpv)
          -h, --help
          -v, --version

        typical use
          just watch a video (no head tracking):
            VRFlatPlayer "E:\\vr\\clip8k.mp4"
          check the webcam tracker before trusting it:
            VRFlatPlayer --camera-preview
          check hand gesture recognition:
            VRFlatPlayer --gesture-preview
          watch with webcam head tracking:
            VRFlatPlayer --source=camera "E:\\vr\\clip8k.mp4"
          laptop, verify opentrack first:
            VRFlatPlayer --source=udp --dump
          laptop, 4K + real webcam:
            VRFlatPlayer --source=udp "D:\\clips\\test4k.mp4"
          laptop, capture a session for the desktop:
            VRFlatPlayer --source=udp --record=recordings/lookaround.csv "D:\\clips\\test4k.mp4"
          desktop (no webcam), 8K with a replayed session:
            VRFlatPlayer --replay=recordings/lookaround.csv "E:\\vr\\clip8k.mp4"
          desktop, simulate head movement with the cursor (view follows the
          pointer — you will not be able to click anything):
            VRFlatPlayer --synthetic=mouse "E:\\vr\\clip8k.mp4"
        """);
}
