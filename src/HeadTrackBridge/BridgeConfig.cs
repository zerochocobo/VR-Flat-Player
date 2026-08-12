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
    ///
    /// A floor, not the whole answer: the mapper stretches it to three times the
    /// measured pose interval. Half a second is four missed poses at 30 a
    /// second and barely one and a half at three, so on a slow machine a fixed
    /// 0.5 declares the tracker dead between two perfectly ordinary samples. A
    /// real session's log is full of the result — the status line alternating
    /// live / LOST while the camera was working exactly as designed, and the
    /// view freezing each time, which reads as the stutter it was mistaken for.
    /// </summary>
    public double TrackingTimeoutSeconds { get; set; } = 0.5;

    /// <summary>
    /// Missed poses before the view is frozen, as a multiple of the measured
    /// pose interval. 0 = use <see cref="TrackingTimeoutSeconds"/> alone.
    ///
    /// Three, and the case it exists for is arithmetic rather than taste: a
    /// tracker managing 1.5 poses a second has a 667 ms gap, so a fixed 500 ms
    /// timeout declares a dropout after *every single pose* and the view never
    /// unfreezes. Below about two poses a second the fixed number stops meaning
    /// anything at all.
    ///
    /// It does not explain the dropouts in the log that prompted it. Those were
    /// real: the face went unfound for up to 3.4 seconds at a stretch, which no
    /// timeout should paper over. See the note on
    /// <see cref="CameraConfig.ScoreThreshold"/>.
    /// </summary>
    public double TrackingTimeoutPoseGaps { get; set; } = 3.0;

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
    /// Frame width the face *detector* sees, independent of <see cref="Width"/>.
    /// 0 = detect on the full frame.
    ///
    /// These are two different jobs and they want two different resolutions.
    /// The landmarker needs the pixels — its precision is what sets the pose
    /// noise, which is why the capture is 1280x720. The detector only has to
    /// return a rectangle for the landmarker to crop, and it pays for every
    /// pixel it is given. Measured here, YuNet on one OpenCV thread:
    ///
    ///     detect size   ms/call   core-seconds/call
    ///      1280x720       118.3               0.116
    ///       640x360        23.9               0.024
    ///       320x180         7.0               0.007
    ///
    /// Five times cheaper in both, for a box that moves by two full-res pixels
    /// at worst — and the crop around it carries 60% margin, so two pixels do
    /// not move a landmark. Detecting at full resolution was costing four times
    /// the landmarker's own call and was the single largest item in the loop.
    ///
    /// 320 is cheaper again, and left as an option rather than the default:
    /// there is no measurement here of what it does to detection *quality* on a
    /// real face, and a missed face costs far more than 17 ms.
    ///
    /// Reported since, and worth recording as the reason not to reach for it
    /// first: someone sitting well back from a 2K camera has a face only a few
    /// dozen pixels across before any of this, and halving them turns a slow
    /// tracker into one that finds nothing. Their log showed windows at 32% and
    /// 54% found at 640. <see cref="DetectFps"/> buys the same milliseconds
    /// without touching what the detector can see, and is the setting to try
    /// first; this one is for people whose face fills the frame.
    /// </summary>
    public int DetectWidth { get; set; } = 640;

    /// <summary>
    /// How often the face detector re-runs while the landmarker is following a
    /// face, in Hz.
    ///
    /// The detector answers "where is the face", and that answer barely changes
    /// between two frames a tenth of a second apart. The 68-point landmarker is
    /// the one that has to run every frame, because it is the measurement. Until
    /// this existed the detector ran every frame too, at 44 ms against the
    /// landmarker's 88 in a reported session — a third of the loop spent
    /// re-deriving something already known.
    ///
    /// This is only the backstop. The landmarks themselves say when the box has
    /// gone stale — the face drifting toward the edge of the crop, or changing
    /// size in it — and the detector is called back immediately when they do, so
    /// a head that moves gets a fresh box on the next frame rather than at the
    /// next tick of this. Two a second is therefore about "how long may a
    /// perfectly still head keep an old box", where the answer hardly matters.
    ///
    /// 0 restores the old behaviour of detecting on every frame. It is also what
    /// happens with no landmarker present, where the detector's five points are
    /// the pose and reusing them would freeze the view.
    /// </summary>
    public double DetectFps { get; set; } = 2;

    /// <summary>
    /// Flip horizontally. On by default: a camera facing you produces a
    /// mirror image, and without the flip turning your head left moves the
    /// view right.
    /// </summary>
    public bool Mirror { get; set; } = true;

    /// <summary>
    /// Detector confidence floor. Lower finds faces in worse light and more
    /// false ones.
    ///
    /// Worth suspecting when the log reports the face found on well under 100%
    /// of frames. A session on a slow machine showed 36% in its worst window,
    /// with runs of frames finding nothing at all and gaps between poses
    /// reaching 3.4 seconds — which freezes the view, and reads as far worse
    /// than the low pose rate it was blamed on.
    ///
    /// Untested here, and stated as a suspicion rather than a finding: this
    /// machine has no usable camera, and the same session was also the first to
    /// run with <see cref="DetectWidth"/> at 640 rather than the full frame. A
    /// smaller picture gives the detector less to be confident about, so a
    /// threshold chosen at 1280x720 may simply be too high at 640. The two can
    /// be separated in one run — set DetectWidth to 0 and compare the percentage
    /// the log now prints.
    /// </summary>
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

    /// <summary>
    /// The most wall-clock time head tracking may spend, as a fraction.
    ///
    /// Covers the whole pipeline — frame conversion, face detection, landmarks,
    /// the pose solve — not just the landmarker. It was the landmarker alone at
    /// first, which missed the expensive half: the detector runs on every
    /// camera frame, and OpenCV parallelises it across every core.
    ///
    /// A rate limit alone does nothing when the model cannot reach the rate.
    /// Asking for 30 a second from a call that costs 58 ms leaves the loop
    /// running flat out at 100% duty against a video decoder that needs the
    /// same cores — which is why switching tracking on during 4K60 playback
    /// made the whole player crawl, the close button included.
    ///
    /// 0.75, up from 0.35, and the change is a correction rather than a
    /// retuning. This multiplies the loop's period by 1/share, so it multiplies
    /// the delay before the view answers your head by the same factor. At 0.35
    /// that is 2.9x, on top of a loop that was already slow because the
    /// detector was running at full resolution: measured here, 150 ms of work
    /// became a 429 ms cycle, about 2 poses a second. The player was reported
    /// as very choppy, and this is most of why.
    ///
    /// The other part of the correction is <see cref="DetectWidth"/>. With the
    /// detector at 640 the same loop costs about 56 ms, so 0.75 gives a 75 ms
    /// cycle — around 13 poses a second at roughly one core, against 8 a second
    /// at nearly four cores before any of this was added. Better on both axes
    /// than the version nobody complained about.
    ///
    /// Lower it if tracking still costs too much on a slow machine; the cost it
    /// buys back is responsiveness, directly and proportionally.
    /// </summary>
    public double TrackingCpuShare { get; set; } = 0.75;

    /// <summary>Hand gesture control. Off unless <see cref="UiConfig.GestureControl"/> is set.</summary>
    public GestureConfig Gesture { get; set; } = new();
}

/// <summary>
/// Hand gesture control: what it costs and how eager it is.
/// </summary>
/// <remarks>
/// Almost every threshold here is a starting point rather than a measurement,
/// and that is worth saying plainly at the top rather than repeating on each
/// one. This machine has no usable camera — the device opens and returns green
/// frames — so nothing below has been checked against a real hand. What has been
/// measured is the cost of the two models, which is where the rates come from,
/// and the geometry, which the unit tests cover.
///
/// <c>--gesture-preview</c> exists so the numbers can be checked on a machine
/// that does have a camera: it draws the landmarks, the pose, and which fingers
/// were read as out.
/// </remarks>
public sealed class GestureConfig
{
    /// <summary>MediaPipe palm detector, relative to the install directory.</summary>
    public string PalmModelPath { get; set; } = "models/palm_detection_mediapipe.onnx";

    /// <summary>MediaPipe 21-point hand landmarker.</summary>
    public string LandmarkModelPath { get; set; } = "models/handpose_estimation_mediapipe.onnx";

    /// <summary>
    /// How often the hand is looked at while gesture mode is off, in Hz.
    ///
    /// This is the rent the feature charges for being switched on, and it is the
    /// number to lower first if it costs too much: the player is in this state
    /// essentially all of the time. Three a second is enough because the only
    /// thing that has to be caught here is a palm held still for a second, which
    /// this samples four times.
    ///
    /// Measured cost of one pass on this machine: 12.2 ms to find a hand plus
    /// 5.6 ms to read it, so about 53 ms a second, or 5% of one core. On the
    /// machine this project has been reported against, which runs the face
    /// models four to five times slower, budget four to five times that.
    /// </summary>
    public double IdleFps { get; set; } = 3;

    /// <summary>
    /// How often the hand is looked at inside gesture mode, in Hz.
    ///
    /// Four times the idle rate, and affordable because head tracking is paused
    /// for exactly as long as this applies: the face pipeline costs about 50 ms
    /// a frame and this costs 5.6, since the palm detector does not run while a
    /// hand is being followed. Gesture mode is cheaper than the state it
    /// replaces.
    ///
    /// It sets how quickly the player answers. A gesture must be held for
    /// <see cref="HoldRuns"/> passes, so 12 Hz means a quarter of a second.
    /// </summary>
    public double ArmedFps { get; set; } = 12;

    /// <summary>Palm detector confidence floor. Lower finds hands in worse light and more false ones.</summary>
    public double PalmScoreThreshold { get; set; } = 0.55;

    /// <summary>
    /// Landmark model confidence floor.
    ///
    /// Higher than the detector's on purpose. A weak detection costs one wasted
    /// landmark call; a weak set of landmarks is a hand shape read off noise,
    /// and that reaches the gesture table.
    /// </summary>
    public double HandScoreThreshold { get; set; } = 0.75;

    /// <summary>
    /// Seconds an open palm must be held still to enter or leave gesture mode.
    ///
    /// Long enough that it cannot happen while reaching for something, short
    /// enough to not feel like waiting. It is the one gesture that must never
    /// fire by accident, because firing it switches off head tracking.
    /// </summary>
    public double ToggleSeconds { get; set; } = 1.0;

    /// <summary>
    /// How far the palm may drift during that hold, in palm widths.
    ///
    /// In palm widths rather than pixels so it means the same thing at a desk
    /// and at arm's length. Well under <see cref="SwipeTravelPalms"/>, which is
    /// what keeps "held still" and "swiped" from ever both being true.
    /// </summary>
    public double TogglePalms { get; set; } = 0.6;

    /// <summary>
    /// Leave gesture mode after this many seconds with no hand in frame.
    ///
    /// Not a replacement for the palm toggle — it is the accident case. Without
    /// it, walking away from a player in gesture mode leaves head tracking
    /// switched off with nothing on screen to say why.
    /// </summary>
    public double IdleTimeoutSeconds { get; set; } = 5.0;

    /// <summary>
    /// Passes a pose must survive before it fires. 3 at 12 Hz is a quarter second.
    ///
    /// The hand passes through shapes on its way between them — a hand closing
    /// into a fist is briefly a point — and this is what stops those transits
    /// from counting.
    /// </summary>
    public int HoldRuns { get; set; } = 3;

    /// <summary>Seconds between repeats while a volume or field-of-view pose is held.</summary>
    public double RepeatSeconds { get; set; } = 0.4;

    /// <summary>
    /// Seconds between repeats while a seek pose is held.
    ///
    /// Its own key because a repeat interval is only half of a rate, and the
    /// other half is the step. Volume moves 5 units and view 5 degrees; a seek
    /// moves ten seconds of film, so the same interval scrubs far faster than
    /// anyone can read the picture. At 0.4 s the first version ran forward at 75
    /// seconds of film a second and overshot every time.
    /// </summary>
    public double SeekRepeatSeconds { get; set; } = 0.8;

    /// <summary>
    /// Say once that gesture control is on but no hand has ever been seen, after
    /// this many seconds.
    ///
    /// At most once, and only while no hand has been found since the stage
    /// started: someone whose camera can see their hand does not need telling,
    /// and a hint that returns every time the hands go down would be noise for
    /// the whole film. The one it is for is the person whose camera points at the
    /// ceiling, for whom every gesture silently does nothing.
    /// </summary>
    public double NoHandHintSeconds { get; set; } = 25;

    /// <summary>
    /// How close to the frame edge counts as about to leave it, as a fraction of
    /// the frame.
    ///
    /// The hand is still being tracked at this point — this is a warning, not a
    /// failure. It exists because the failure that follows is silent: the hand
    /// crosses the edge, tracking stops, and nothing on screen distinguishes that
    /// from a pose that was not recognised.
    /// </summary>
    public double EdgeMargin { get; set; } = 0.06;

    /// <summary>
    /// Seconds after a gesture fires during which nothing else can.
    ///
    /// The hand is still in the shape that fired, and on its way somewhere else,
    /// for a moment afterwards. A swipe in particular will re-trigger off its own
    /// follow-through without this.
    /// </summary>
    public double CooldownSeconds { get; set; } = 0.5;

    /// <summary>
    /// How far an open palm must travel sideways to count as a swipe, in palm
    /// widths.
    ///
    /// 1.0, down from 1.5, down from 2.5 — and this time from a measurement
    /// rather than an argument. A reported session logged the furthest reach in
    /// every reporting window for ten minutes of deliberate swiping: 0.3, 0.8,
    /// 0.9, 0.8, 0.3, 0.6, 1.0, 1.0. The threshold sat above every attempt made,
    /// which is why it almost never fired.
    ///
    /// Part of that ceiling was a bug rather than the hand — travel was thrown
    /// away whenever the landmarker lost the hand for a frame, which is most
    /// likely mid-sweep — so these numbers understate the motion that was
    /// actually made. 1.0 is set at the top of what was measured *with* that bug
    /// present, which is deliberately conservative: with it fixed the same sweeps
    /// should clear it comfortably.
    ///
    /// Against <see cref="SwipeRestPalms"/> at 0.35 that is a factor of three,
    /// which is the separation that matters: a swipe has to be three times
    /// further than the stillness that starts one.
    ///
    /// The log still prints the furthest reach measured, so this can be set from
    /// what your own hand does rather than from this note.
    /// </summary>
    public double SwipeTravelPalms { get; set; } = 1.0;

    /// <summary>
    /// How far the palm may wander and still count as the place a swipe starts
    /// from, in palm widths.
    ///
    /// Swipe travel is measured from where the hand last sat still, and this is
    /// half of what "still" means — the other half is
    /// <see cref="SwipeRestSeconds"/>. While the palm stays inside this circle
    /// for that long, the anchor simply follows it, so a slow drift can never
    /// accumulate into a swipe however long it goes on.
    ///
    /// It is also the only thing that clears a spent swipe, which is what puts a
    /// deliberate pause between one file change and the next.
    /// </summary>
    public double SwipeRestPalms { get; set; } = 0.35;

    /// <summary>
    /// How long the palm must stay inside that circle to count as still.
    ///
    /// The pair sets the slowest hand that still reads as a swipe: 0.35 palm
    /// widths in 0.25 s is about 1.4 palm widths a second, or a hand crossing its
    /// own width in three quarters of a second. Anything slower is a drift and
    /// never starts a stroke at all.
    ///
    /// It must be a span of time and not the gap between two readings. Compared
    /// frame to frame at ten looks a second, a brisk sweep moves only about a
    /// third of a palm width between two of them, so every sweep would read as
    /// still and no swipe could ever fire.
    /// </summary>
    public double SwipeRestSeconds { get; set; } = 0.25;

    /// <summary>
    /// How long one stroke may take, in seconds.
    ///
    /// 0.9, and it no longer has to hold both ends. It used to: travel was
    /// measured across a sliding window of this length, so the window was the
    /// only thing standing between a slow drift and a file change, and it could
    /// not be lengthened without letting drift through. Rest detection took that
    /// job — a drift never leaves <see cref="SwipeRestSeconds"/> and so never
    /// starts a stroke — which frees this to be what its name says and to be
    /// generous about it.
    ///
    /// Generous on purpose. At 1.0 palm widths of travel, 0.9 s asks for 1.1 palm
    /// widths a second while the rest test already demands about 1.4 to begin at
    /// all. Any sweep quick enough to start is therefore quick enough to finish,
    /// which is the arrangement to keep: two limits that can both bind are two
    /// ways for the gesture to fail silently.
    ///
    /// It also bounds the damage when a stroke does not complete. The anchor is
    /// re-placed at the hand after this long, so an abandoned sweep costs one
    /// window rather than sitting there waiting to combine with the next motion.
    /// </summary>
    public double SwipeSeconds { get; set; } = 0.9;
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

    /// <summary>
    /// Upper end of the glide, used when poses arrive more slowly than
    /// <see cref="GlideSeconds"/> can cover. Set equal to it to go back to a
    /// fixed glide.
    ///
    /// A fixed glide is a constant tuned for a pose rate the tracker may not
    /// reach, and getting it wrong does not read as lag — it reads as stutter.
    /// The glide finishes in about 80 ms whatever the gap, so on a machine
    /// producing three poses a second the view completes its move and then
    /// stands still for a quarter of a second before the next one lands. That
    /// is the "it jumps to the next angle instead of moving smoothly" report,
    /// and it is why the same code felt right at 0.2.0 and wrong later: the
    /// mapper never changed, the pose rate did.
    ///
    /// Measured by driving the real mapper with a steady 4 deg/s turn at a
    /// 120 Hz output rate. Share of output frames with the picture frozen, and
    /// how much faster the fastest moment is than the average:
    ///
    ///     poses/s   fixed 0.08        glide = the pose gap
    ///        13      0%   x1.8         0%   x1.8
    ///         8      0%   x2.3         0%   x1.9
    ///         6      0%   x2.8         0%   x1.9
    ///         4      0%   x3.8         0%   x1.9
    ///       2.3     27%   x6.3         0%   x2.1
    ///
    /// Tracking the gap costs lag — 3.0 degrees against 0.7 at 2.3 poses a
    /// second — and that is the right trade. A view that is smoothly a little
    /// behind is watchable; one that teleports every 400 ms is not. On a
    /// machine that keeps up, the gap is under GlideSeconds and this does
    /// nothing at all.
    /// </summary>
    public double GlideMaxSeconds { get; set; } = 0.30;
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

    /// <summary>
    /// Whether the camera watches for hand gestures.
    ///
    /// Off until asked for, and remembered once it has been, for the same reason
    /// <see cref="FaceTracking"/> is: a camera that opens itself is a surprise,
    /// and this one can also pause head tracking, which would be a second
    /// surprise with no visible cause.
    ///
    /// Independent of <see cref="FaceTracking"/>. Either can run without the
    /// other, and with both on the camera is still opened exactly once — see
    /// <c>CameraFaceSource</c>, which owns the device and runs both stages.
    /// </summary>
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
