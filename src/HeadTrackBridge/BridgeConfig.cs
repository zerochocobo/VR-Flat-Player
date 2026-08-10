using System.Text.Json;
using System.Text.Json.Serialization;
using HeadTrackBridge.Mpv;

namespace HeadTrackBridge;

public sealed class BridgeConfig
{
    public SourceConfig Source { get; set; } = new();
    public FilterConfig Filter { get; set; } = new();
    public AxisConfig Yaw { get; set; } = AxisConfig.DefaultYaw();
    public AxisConfig Pitch { get; set; } = AxisConfig.DefaultPitch();
    public AxisConfig Roll { get; set; } = AxisConfig.DefaultRoll();
    public RecenterConfig Recenter { get; set; } = new();
    public VideoConfig Video { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public InputConfig Input { get; set; } = new();
    public MpvConfig Mpv { get; set; } = new();

    /// <summary>How often the mapped view is pushed to mpv, in Hz.</summary>
    public double OutputRateHz { get; set; } = 120;

    /// <summary>
    /// If no pose arrives for this long, freeze the view where it is and warn.
    /// Never snap back to centre — that is the single most disorienting thing a
    /// head-tracked player can do.
    /// </summary>
    public double TrackingTimeoutSeconds { get; set; } = 0.5;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static BridgeConfig Load(string path)
    {
        if (!File.Exists(path)) return new BridgeConfig();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BridgeConfig>(json, JsonOptions) ?? new BridgeConfig();
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

public enum SourceKind
{
    /// <summary>No head tracking at all. Mouse drag alone drives the view.</summary>
    None,
    Udp,
    Synthetic,
    Replay,

    /// <summary>A webcam, tracked in-process. No opentrack install needed.</summary>
    Camera,
}

public sealed class CameraConfig
{
    /// <summary>Which camera. 0 is the first one Windows enumerates.</summary>
    public int DeviceIndex { get; set; }

    /// <summary>
    /// 1280x720. This was 640x480, justified as "tracking needs an angle, not
    /// detail" — wrong in the way that mattered.
    ///
    /// Landmark *precision* scales with how many pixels the face covers, and
    /// precision is exactly what is short: the unit tests measure one pixel of
    /// landmark jitter becoming about one degree of pose. At 640x480 a face
    /// covers perhaps 150 px, so one pixel is a coarse step — and pitch, which
    /// leans on the single worst-localised landmark, arrives in visible jumps
    /// rather than smoothly. Doubling the linear resolution roughly halves the
    /// angular quantisation.
    ///
    /// YuNet is a 230 KB network, so the extra pixels cost much less than the
    /// old wording implied.
    /// </summary>
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    public int Fps { get; set; } = 30;

    /// <summary>
    /// Flip horizontally. On by default: a camera facing you produces a
    /// mirror image, and without the flip turning your head left moves the
    /// view right.
    /// </summary>
    public bool Mirror { get; set; } = true;

    /// <summary>Detector confidence floor. Lower finds faces in worse light and more false ones.</summary>
    public double ScoreThreshold { get; set; } = 0.7;

    /// <summary>YuNet ONNX model, relative to the install directory.</summary>
    public string ModelPath { get; set; } = "models/face_detection_yunet.onnx";

    /// <summary>
    /// 2DFAN4 68-point landmarker. Optional: without it the player falls back
    /// to the detector's five points, which track left/right fine and up/down
    /// badly.
    /// </summary>
    public string LandmarkModelPath { get; set; } = "models/face_landmark_peppa_wutz.onnx";

    /// <summary>
    /// How often per second to run the 68-point landmarker. 0 = every frame.
    ///
    /// 30, i.e. every frame the camera produces, which is what it should always
    /// have been.
    ///
    /// It was 6, and only because 2DFAN4 cost 310 ms a call on an eight-core
    /// desktop and 438 ms on the machine that complained. Throttling that hard
    /// is what made the view lurch to a new angle every second or two.
    /// peppa_wutz costs 28.6 ms, so there is nothing left to protect against.
    ///
    /// The throttle stays for slower machines and slower models: lowering it
    /// trades responsiveness for CPU, and the One Euro filter works from
    /// timestamps rather than a fixed rate, so fewer samples cost smoothness at
    /// the margin rather than correctness.
    /// </summary>
    public double LandmarkFps { get; set; } = 30;

    /// <summary>
    /// How much to steady the landmarks before solving, 0 to 0.95. 0 = off.
    ///
    /// This is the most effective place to attack the shake, because it is the
    /// earliest. PnP is non-linear, so smoothing the angles it produces cannot
    /// properly undo noise in the points it consumed — a wobbling point set
    /// does not give a wobbling angle that averages back to the truth.
    /// </summary>
    public double LandmarkSmoothing { get; set; } = 0.5;
}

public sealed class SourceConfig
{
    /// <summary>
    /// Defaults to None. Head tracking is a feature of this player, not a
    /// prerequisite for it — and in particular the `synthetic` sources move the
    /// view as the cursor moves, which makes ordinary mouse use impossible.
    /// </summary>
    public SourceKind Kind { get; set; } = SourceKind.None;

    /// <summary>Port to bind for opentrack's "UDP over network" output.</summary>
    public int UdpPort { get; set; } = 4242;

    /// <summary>sweep | mouse | still</summary>
    public Tracking.SyntheticMode SyntheticMode { get; set; } = Tracking.SyntheticMode.Mouse;

    public double SyntheticJitterDegrees { get; set; } = 0.35;

    public string ReplayFile { get; set; } = "recordings/session.csv";

    /// <summary>Write every incoming pose to this CSV. Empty = off.</summary>
    public string RecordTo { get; set; } = "";

    public CameraConfig Camera { get; set; } = new();
}

public sealed class FilterConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Hz. Lower = smoother at rest.
    ///
    /// 0.4, not the 1.0 that suited opentrack. A webcam's landmarks move a
    /// pixel or two between frames on a face that is holding still, and the
    /// unit tests measure that one pixel becomes about one degree of pose --
    /// which the gain then multiplies into very visible shake. Filtering the
    /// input hard is the only place to deal with it that does not also make
    /// real movement sluggish.
    /// </summary>
    public double MinCutoff { get; set; } = 0.4;

    /// <summary>
    /// Higher = less lag on fast turns. Typical range 0.001 - 0.05.
    ///
    /// Raised with the lower cutoff above, and they belong together: One Euro
    /// smooths hard when the head is slow and backs off as it speeds up, so
    /// beta is what stops heavy smoothing at rest from turning into lag when
    /// you actually turn.
    /// </summary>
    public double Beta { get; set; } = 0.020;

    public double DerivativeCutoff { get; set; } = 1.0;

    /// <summary>
    /// Seconds for the view to glide to a newly measured head pose. 0 = snap.
    ///
    /// Separate from the One Euro settings above and doing a different job.
    /// Those smooth a stream of samples; this covers the gaps *between* them.
    /// The 68-point landmarker costs a quarter-second per call and much more on
    /// a weak machine, so poses can land a second apart, and snapping to each
    /// one makes the view teleport — worse to watch than the jitter it replaced.
    ///
    /// 0.08 now that poses arrive every frame again. It was 0.25 when they came
    /// a second apart and the glide had a real gap to cover; leaving it there
    /// would just be lag, since the thing it was hiding is gone.
    /// </summary>
    public double GlideSeconds { get; set; } = 0.08;
}

public sealed class AxisConfig
{
    /// <summary>Head rotation, in degrees, that maps to the full output range.</summary>
    public double InputRangeDegrees { get; set; } = 25;

    /// <summary>View rotation, in degrees, produced at full input. This is the "gain".</summary>
    public double OutputRangeDegrees { get; set; } = 150;

    /// <summary>
    /// Response curve exponent. &gt;1 is soft near centre (small head movements
    /// barely move the view — comfortable) and aggressive at the edges (you can
    /// still reach behind you). &lt;1 does the opposite. 1 = linear.
    /// </summary>
    public double Curve { get; set; } = 1.8;

    /// <summary>Hard deadzone in input degrees, applied before the curve. Usually 0 — the curve already softens the centre.</summary>
    public double DeadzoneDegrees { get; set; } = 0;

    /// <summary>
    /// Slack, in head degrees, before the view follows the head at all. 0 = off.
    ///
    /// Not a deadzone. A deadzone is anchored to centre and only suppresses
    /// noise while you are looking straight ahead; hold your head 20 degrees to
    /// the left and the shimmer comes straight back, which is what "it rocks
    /// back and forth" was describing. This band travels with the head, so a
    /// head that is holding still produces a picture that is holding still
    /// *anywhere* in the range.
    ///
    /// It works like backlash in a gear train: move less than this and nothing
    /// happens, move more and the view is dragged along at full rate, trailing
    /// by exactly this much. Reversing direction therefore costs twice the band
    /// before the view responds, which is the whole price of the mechanism and
    /// why it is set in real head degrees just above the measured noise floor
    /// rather than as large as it could be.
    /// </summary>
    public double StickyDegrees { get; set; } = 0;

    public bool Invert { get; set; }

    /// <summary>Output is clamped to +/- this. Pitch must stay under 90.</summary>
    public double ClampDegrees { get; set; } = 180;

    // Calmer than they were. 25 degrees of head turn used to produce 150 of
    // view, a six-fold amplification, on a curve steep enough that most of it
    // arrived in the last few degrees -- so the view sat still and then darted.
    //
    // Two changes, because the complaint had two causes. A wider input range
    // asks for more head movement per unit of view, and a flatter curve spreads
    // the response out instead of loading it all at the extremes. The curve is
    // the one that fixes darting; the range fixes overall reach.
    public static AxisConfig DefaultYaw() => new()
    {
        InputRangeDegrees = 30,
        OutputRangeDegrees = 70,
        Curve = 1.4,
        // Was 0.6, to swallow the residual shimmer the filter leaves behind.
        // StickyDegrees does that job properly now — the deadzone only ever
        // steadied the picture while you were looking dead ahead, and the
        // complaint was that it rocked everywhere else.
        DeadzoneDegrees = 0,
        StickyDegrees = 1.0,
        ClampDegrees = 180,
    };

    public static AxisConfig DefaultPitch() => new()
    {
        // Far less head movement than yaw, on purpose. Looking up and down at a
        // monitor is done mostly with the eyes -- the head barely pitches, while
        // it genuinely turns for left and right. Asking for 22 degrees of real
        // pitch made the axis feel dead because nobody produces 22 degrees.
        InputRangeDegrees = 12,
        OutputRangeDegrees = 55,
        Curve = 1.2,
        // No deadzone on pitch, unlike yaw. The 0.6 that steadied yaw was
        // swallowing pitch outright: yaw has a strong signal with noise on top,
        // so cutting the bottom off costs nothing, while pitch's whole usable
        // range through a 5-point solve is barely larger than the deadzone.
        // Suppressing noise you cannot afford to lose is just suppression.
        DeadzoneDegrees = 0,
        // Smaller than yaw's, and for the same reason the deadzone above is
        // zero: pitch's whole usable range is a third of yaw's, so an equal
        // band would cost proportionally three times as much of it. 0.8 sits
        // above the 0.18 degree RMS the 68-point solve measures and well under
        // the 12 degree input range.
        StickyDegrees = 0.8,
        ClampDegrees = 85,
    };

    /// <summary>
    /// Roll is off by default and you should think hard before turning it on:
    /// a tilting horizon is the fastest route to motion sickness, and people
    /// tilt their head unconsciously all the time.
    /// </summary>
    public static AxisConfig DefaultRoll() => new()
    {
        InputRangeDegrees = 25,
        OutputRangeDegrees = 0,
        Curve = 1.0,
        ClampDegrees = 20,
    };
}

public sealed class RecenterConfig
{
    /// <summary>Take the first valid pose as centre instead of assuming 0,0,0.</summary>
    public bool OnFirstPose { get; set; } = true;

    /// <summary>
    /// Slowly drag the centre reference toward the current head pose, so the
    /// view does not creep as you settle into a new resting posture. Seconds to
    /// close ~63% of the gap. 0 disables.
    /// </summary>
    public double AutoDriftTimeConstant { get; set; } = 0;

    /// <summary>Auto-drift only applies while the head is within this many degrees of centre.</summary>
    public double AutoDriftMaxDegrees { get; set; } = 10;
}

public sealed class InputConfig
{
    /// <summary>Left-drag over the video pans the view.</summary>
    public bool DragToLook { get; set; } = true;

    /// <summary>
    /// Only drag while the mpv window is the active window. Keep this on: it is
    /// what stops the view panning when you drag in some other application.
    /// Off is for automated testing, where Windows refuses to let a background
    /// script bring mpv to the foreground.
    /// </summary>
    public bool RequireForeground { get; set; } = true;

    /// <summary>
    /// Fraction of window height at the bottom where a press starts no drag,
    /// reserved for uosc's timeline and control row.
    /// </summary>
    public double ControlBarBandFraction { get; set; } = 0.18;
}

public sealed class UiConfig
{
    /// <summary>
    /// "auto" follows the Windows UI language. Or force a tag: en, zh-CN,
    /// zh-TW, ja, ko, de, ... Anything without a table of our own still gets a
    /// localised uosc control bar where uosc has one, and English elsewhere.
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Whether head tracking actually drives the view.
    ///
    /// Off until asked for, and remembered once it has been. A camera quietly
    /// moving the picture is a surprise the first time, and someone who has
    /// never set up tracking should not have to discover a switch to stop it.
    /// It used to default to on and reset every launch, which was both halves
    /// of that wrong.
    /// </summary>
    public bool FaceTracking { get; set; }

    /// <summary>Reserved for gesture control, which does not exist yet.</summary>
    public bool GestureControl { get; set; }

    /// <summary>Remember the VR mode chosen for each file and restore it on reopen.</summary>
    public bool RememberPerFileMode { get; set; } = true;

    /// <summary>
    /// Run mpv inside our own window, which is what gives the player a native
    /// menu bar above the video instead of one painted over it.
    ///
    /// Turning this off puts mpv back in its own window with the bridge as a
    /// sidecar. That is the fallback if embedding ever misbehaves on a
    /// particular machine, and it is required when attaching to an mpv you
    /// started yourself.
    /// </summary>
    public bool HostWindow { get; set; } = true;
}

/// <summary>
/// The window's last position and size, so it opens where you left it.
///
/// Its own file, not a section of bridge.config.json, because it is state
/// rather than settings. It lived in the config once and shipped inside a
/// release: publishing generates a clean config, but the staged folder is also
/// the copy that gets test-run before shipping, and every run writes its window
/// back. The build went out carrying a 1391x1530 window from the machine it was
/// tested on, which on a 1080p screen opened clamped and full height.
///
/// Separating it means a test run can only create a file that was never part of
/// the release, which publishing can check for and delete — a settings file it
/// has to ship anyway cannot be checked the same way.
///
/// Zero width means nothing saved yet, which is also what a missing file gives,
/// so "never run before" and "restore this" are the same test with no separate
/// flag to keep in step.
/// </summary>
public sealed class WindowConfig
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Restored maximised. The size above is the un-maximised one, so
    /// un-maximising lands somewhere sensible rather than at a default.</summary>
    public bool Maximized { get; set; }

    public static WindowConfig Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WindowConfig>(File.ReadAllText(path), BridgeConfig.JsonOptions) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new();   // a corrupt layout file is not worth refusing to start over
        }
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, BridgeConfig.JsonOptions)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only install and no data directory: the window simply will
            // not be remembered, which is not worth a message on every resize.
        }
    }
}

public sealed class VideoConfig
{
    /// <summary>
    /// "auto", a mpv360 projection index (0-7), or a name such as
    /// "dual-half-equirect" / "vr180" / "equirectangular".
    /// A mono 360 file and a VR180 SBS file are both 2:1, so auto-detection
    /// leans on the filename and falls back to 360 — override here when wrong.
    /// </summary>
    public string Projection { get; set; } = "auto";

    /// <summary>
    /// What to open a file as when nothing else can decide: no remembered
    /// choice, no recognised tag in the filename, and no unambiguous aspect
    /// ratio. That case is almost entirely 2:1 files, where a mono 360 and a
    /// VR180 side-by-side are pixel-for-pixel indistinguishable.
    ///
    /// VR180 side-by-side, because that is what most of a VR library is. The
    /// old behaviour assumed mono 360 and was therefore wrong about half the
    /// time — and wrong in the more confusing direction, since a VR180 file
    /// shown as 360 looks like a warped duplicate rather than an obvious error.
    /// </summary>
    public string Fallback { get; set; } = "vr180";

    /// <summary>
    /// Carry the last mode the user picked over to the next unrecognised file.
    /// Libraries are encoded consistently, so one correction usually applies to
    /// everything that follows. Detection and per-file memory both still win
    /// over it — this only fills the gap where there was no answer at all.
    /// </summary>
    public bool StickyMode { get; set; } = true;

    /// <summary>left | right | both. On a flat monitor you want one eye.</summary>
    public Eye Eye { get; set; } = Eye.Left;

    /// <summary>Let auto-detection pick the eye too (only meaningful when projection is auto).</summary>
    public bool AutoEye { get; set; } = true;
}

public sealed class MpvConfig
{
    /// <summary>Windows named pipe name. Must match mpv's --input-ipc-server.</summary>
    public string IpcPipe { get; set; } = @"\\.\pipe\vrheadtrack";

    /// <summary>Start mpv ourselves. Set false if you launch mpv separately.</summary>
    public bool AutoLaunch { get; set; } = true;

    public string MpvPath { get; set; } = "mpv";

    /// <summary>Extra arguments appended to the mpv command line.</summary>
    public string[] ExtraArgs { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Decoder and renderer overrides, picked from the Playback menu.
    ///
    /// Empty means "whatever mpv.conf says", which is the tuned default and
    /// where the reasoning for it lives. These exist only so a machine whose
    /// GPU cannot do the default has a way out that does not involve editing a
    /// config file — the likeliest reason someone cannot play a video here is
    /// a driver that will not do D3D11, not anything about this player.
    ///
    /// Written only after the change has been applied and verified against
    /// mpv, so a setting that does not work cannot be persisted into the next
    /// launch and lock the user out.
    /// </summary>
    public string Hwdec { get; set; } = "";

    public string Vo { get; set; } = "";
    public string GpuApi { get; set; } = "";
    public string GpuContext { get; set; } = "";

    /// <summary>
    /// mpv360 stores yaw/pitch/roll in radians. If your build differs, flip this.
    /// </summary>
    public bool AnglesInRadians { get; set; } = true;

    /// <summary>Skip the IPC write when no angle moved more than this many degrees.</summary>
    public double MinDeltaDegrees { get; set; } = 0.02;
}
