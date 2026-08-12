using System.Globalization;
using OpenCvSharp;

namespace HeadTrackBridge.Tracking.Hand;

/// <summary>
/// What the camera can see of the hand right now, for the on-screen panel.
/// </summary>
/// <param name="Armed">Gesture mode is on.</param>
/// <param name="Hold">How far through the palm hold that toggles it, 0 to 1.</param>
/// <param name="Pose">The shape read, or <see cref="Pose.None"/>.</param>
/// <param name="Points">21 landmarks as "x,y,..." fractions of the frame, or "-".</param>
/// <param name="Edge">Part of the hand is against the edge of the picture.</param>
public readonly record struct HandView(bool Armed, double Hold, Pose Pose, string Points, bool Edge);

/// <summary>
/// Watches the camera for hand gestures and turns them into player actions.
///
/// Two things make this usable rather than merely possible, and both are about
/// what it does *not* do:
///
///   * Gesture mode is entered and left by holding an open palm still. Outside
///     it, no gesture does anything at all — a hand reaching for a drink cannot
///     pause the film. Inside it, the shapes are read at four times the rate.
///   * While gesture mode is on, head tracking is paused (the camera loop reads
///     <see cref="Armed"/> and skips the face stage). That is not only a way to
///     stay inside one CPU budget: the head moves while the hand is being waved
///     about, and a view that swings around during that is worse than no view
///     control at all.
///
/// This class is the half that runs the models. The half that decides what the
/// shapes mean is <see cref="GestureStateMachine"/>, which is separate because
/// it can be tested and this cannot — reaching this code needs a camera.
///
/// The pipeline is MediaPipe's two-stage hand tracker. The palm detector only
/// runs while no hand is being followed; once one is, the previous frame's
/// landmarks rebuild the crop and the detector — the expensive half — is
/// skipped entirely.
/// </summary>
public sealed class GestureRecognizer : IDisposable
{
    private readonly GestureConfig _cfg;
    private readonly bool _mirrored;
    private readonly PalmDetector _palm;
    private readonly HandLandmarker _landmarker;
    private readonly GestureStateMachine _state;

    private HandLandmarks? _tracked;
    private double _lastRunAt = double.NegativeInfinity;
    private double _startedAt = double.NegativeInfinity;
    private bool _handEverFound;
    private bool _hintSent;

    public GestureRecognizer(GestureConfig cfg, string palmModelPath, string landmarkModelPath,
                             bool mirrored)
    {
        _cfg = cfg;
        _mirrored = mirrored;
        _palm = new PalmDetector(palmModelPath) { ScoreThreshold = (float)cfg.PalmScoreThreshold };
        _landmarker = new HandLandmarker(landmarkModelPath) { ScoreThreshold = (float)cfg.HandScoreThreshold };
        _state = new GestureStateMachine(cfg, mirrored);
        _state.ArmedChanged += on => ArmedChanged?.Invoke(on);
        _state.Fired += g => Fired?.Invoke(g);
    }

    /// <summary>
    /// True while gesture mode is on: gestures act, and head tracking is paused.
    /// Read by the camera loop on every frame.
    /// </summary>
    public bool Armed => _state.Armed;

    /// <summary>Raised when gesture mode is entered or left. On the camera thread.</summary>
    public event Action<bool>? ArmedChanged;

    /// <summary>Raised when a gesture fires. On the camera thread.</summary>
    public event Action<Gesture>? Fired;

    /// <summary>Raised after every pass, whether or not a hand was found.</summary>
    public event Action<HandView>? Observed;

    /// <summary>
    /// Raised once, if gesture control has been running for
    /// <see cref="GestureConfig.NoHandHintSeconds"/> and has never seen a hand.
    /// </summary>
    /// <remarks>
    /// Once, and only while nothing has ever been found. The failure it is for is
    /// a camera that cannot see the user's hands at all — pointed too high, or a
    /// room too dark — and that failure is completely silent: every gesture does
    /// nothing, exactly as if the feature were switched off, and the natural
    /// conclusion is that it is broken rather than aimed wrong.
    ///
    /// Anyone whose camera has found a hand once does not need telling, which is
    /// why a hand seen at any point retires this for good. A hint that came back
    /// every time the hands went down would be on screen through most of a film.
    /// </remarks>
    public event Action? HandNeverFound;

    /// <summary>Which action table applies. Set by the session when the file's mode changes.</summary>
    public bool Vr
    {
        get => _state.Vr;
        set => _state.Vr = value;
    }

    /// <summary>The last reading, for the diagnostic overlay. Null when no hand was found.</summary>
    public PoseReading? LastReading { get; private set; }

    /// <summary>The landmarks behind <see cref="LastReading"/>, for the overlay to draw.</summary>
    public HandLandmarks? LastHand { get; private set; }

    /// <summary>Cost of the last palm detection and landmark call, in milliseconds.</summary>
    public double LastPalmMs { get; private set; }

    public double LastLandmarkMs { get; private set; }

    /// <summary>True when the last run had to scan for a hand rather than follow one.</summary>
    public bool Scanning => _tracked is null;

    /// <summary>
    /// The furthest an open palm has travelled inside the swipe window since the
    /// last read, in palm widths. Reported so that a swipe which will not fire
    /// can be told from one that is not being attempted.
    /// </summary>
    public double TakeSwipeReach() => _state.TakeSwipeReach();

    /// <summary>
    /// Whether enough time has passed to run again.
    /// </summary>
    /// <remarks>
    /// Four times slower when idle than when armed, and the asymmetry is the
    /// whole cost argument for the feature. Idle, the only thing that has to be
    /// caught is a palm held still for a second, which three samples a second
    /// notices four times over; armed, the rate sets how quickly the player
    /// answers, so it goes up. Idle is also the state the player spends
    /// essentially all of its time in.
    /// </remarks>
    public bool Due(double now)
    {
        var fps = Armed ? _cfg.ArmedFps : _cfg.IdleFps;
        return fps <= 0 || now - _lastRunAt >= 1.0 / fps;
    }

    /// <summary>Run one pass over a camera frame. Call only when <see cref="Due"/>.</summary>
    public void Process(Mat frame, double now)
    {
        if (double.IsNegativeInfinity(_startedAt)) _startedAt = now;
        _lastRunAt = now;
        LastPalmMs = 0;
        LastLandmarkMs = 0;

        var hand = Track(frame);
        LastHand = hand;
        LastReading = hand is { } found ? HandPoseReader.Read(found, _mirrored) : null;

        _state.Accept(LastReading, now);

        if (hand is not null) _handEverFound = true;
        else if (!_handEverFound && !_hintSent && now - _startedAt > _cfg.NoHandHintSeconds)
        {
            _hintSent = true;
            HandNeverFound?.Invoke();
        }

        Observed?.Invoke(new HandView(
            _state.Armed, _state.HoldProgress,
            LastReading?.Pose ?? Pose.None,
            Normalise(hand, frame.Width, frame.Height),
            AtEdge(hand, frame.Width, frame.Height)));
    }

    /// <summary>
    /// Any landmark within <see cref="GestureConfig.EdgeMargin"/> of a border.
    /// </summary>
    /// <remarks>
    /// The whole hand, not the palm centre. A hand leaves the picture fingers
    /// first, and it is the fingers the pose is read from — an open palm with two
    /// fingertips outside the frame is not read as an open palm, while its centre
    /// is still comfortably inside.
    ///
    /// Deliberately a warning about a hand that is still being tracked. Once it
    /// has actually gone there is nothing left to warn from, and the panel showing
    /// no hand at all is a different message about a different problem.
    /// </remarks>
    private bool AtEdge(HandLandmarks? hand, int width, int height)
    {
        if (hand is not { } h || width <= 0 || height <= 0) return false;

        var mx = _cfg.EdgeMargin * width;
        var my = _cfg.EdgeMargin * height;
        foreach (var p in h.Points)
            if (p.X < mx || p.Y < my || p.X > width - mx || p.Y > height - my) return true;
        return false;
    }

    /// <summary>
    /// The landmarks as fractions of the frame, in one flat string, or "-".
    /// </summary>
    /// <remarks>
    /// Normalised here rather than at the far end because this is the only place
    /// that knows what size the frame was, and formatted here because the only
    /// consumer is an mpv script message, which is text either way. Three
    /// decimals is a third of a pixel at 1280 wide — far past what a 168 px
    /// panel can show.
    /// </remarks>
    private static string Normalise(HandLandmarks? hand, int width, int height)
    {
        if (hand is not { } h || width <= 0 || height <= 0) return "-";

        var parts = new string[HandLandmarks.PointCount * 2];
        for (var i = 0; i < HandLandmarks.PointCount; i++)
        {
            parts[i * 2] = (h.Points[i].X / width).ToString("F3", CultureInfo.InvariantCulture);
            parts[i * 2 + 1] = (h.Points[i].Y / height).ToString("F3", CultureInfo.InvariantCulture);
        }
        return string.Join(',', parts);
    }

    /// <summary>
    /// The hand in this frame: followed from the last one if possible, found from
    /// scratch in the same pass if following fails.
    /// </summary>
    /// <remarks>
    /// Re-detecting immediately is what makes swiping work, and the first
    /// version did the opposite. It kept the stale crop and retried it for three
    /// runs before giving up, on the theory that one bad frame is motion blur
    /// rather than a lost hand.
    ///
    /// That theory is right for a hand holding a shape and exactly backwards for
    /// a hand in motion. The crop is built from where the hand *was*; a hand
    /// moving fast enough to be a swipe has left it, so all three retries look at
    /// the same empty rectangle and fail. At ten looks a second that is 300 ms of
    /// nothing — most of a swipe — and the trail below never accumulated enough
    /// travel to fire. Reported as "上一部下一部始终没有生效": the gesture had
    /// never once been recognised, and the log agreed, showing no swipe at all in
    /// a ten-minute session.
    ///
    /// The cost of being wrong the other way is one extra detection, 12 ms, on
    /// frames where following failed — which is precisely when it is worth
    /// paying.
    /// </remarks>
    private HandLandmarks? Track(Mat frame)
    {
        if (_tracked is { } previous)
        {
            if (Read(frame, HandLandmarker.PalmFrom(previous)) is { } followed)
            {
                _tracked = followed;
                return followed;
            }
            // The crop is stale rather than the hand gone. Fall through and look
            // for it again instead of asking the same rectangle twice more.
            _tracked = null;
        }

        var started = Clock.Seconds;
        var palm = _palm.Detect(frame);
        LastPalmMs = (Clock.Seconds - started) * 1000;

        _tracked = palm is { } found ? Read(frame, found.Keypoints) : null;
        return _tracked;
    }

    private HandLandmarks? Read(Mat frame, Point2f[] palmKeypoints)
    {
        var started = Clock.Seconds;
        var hand = _landmarker.Locate(frame, palmKeypoints);
        LastLandmarkMs += (Clock.Seconds - started) * 1000;
        return hand;
    }

    public void Dispose()
    {
        _landmarker.Dispose();
        _palm.Dispose();
    }
}
