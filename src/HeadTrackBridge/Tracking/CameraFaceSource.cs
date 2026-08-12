using HeadTrackBridge.Tracking.Face;
using HeadTrackBridge.Tracking.Hand;
using OpenCvSharp;

// WinForms puts System.Drawing in the implicit usings, which collides with
// OpenCvSharp's Size.
using Size = OpenCvSharp.Size;

namespace HeadTrackBridge.Tracking;

/// <summary>
/// The camera, and the two things the player does with it.
///
/// It began as head tracking alone, which is where the name comes from: capture
/// a frame, find one face, solve its rotation, and produce exactly the same
/// <see cref="HeadPose"/> the UDP source does — so everything downstream, the
/// One Euro filter, the deadzone and gain curve, recentring, the content limits,
/// is untouched and already tuned.
///
/// Gesture control lives here too, and not for tidiness. A camera can only be
/// opened once, so a second loop reading the same device would simply fail; and
/// two loops would each spend the CPU budget that 0.2.x was spent working out,
/// twice. One thread reads one stream and hands each frame to whichever stages
/// are switched on.
///
/// The two stages are never both busy. Gesture control pauses head tracking for
/// as long as gesture mode is on — see <see cref="GestureRecognizer.Armed"/> —
/// which keeps the cost inside one budget and, more importantly, stops the view
/// swinging around while the user is waving a hand at the screen.
///
/// Runs on its own thread. Grabbing a frame blocks for as long as the camera
/// takes, and doing that on a pool thread would tie one up permanently.
/// </summary>
public sealed class CameraFaceSource : ITrackingSource
{
    private readonly CameraConfig _cfg;
    private readonly bool _verbose;

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _faceEnabled;
    private volatile bool _gestureEnabled;

    // The face stage, built on first use rather than at startup: a session that
    // only ever uses gestures must not pay for an ONNX session it will not call,
    // nor be stopped by a face model it does not need.
    private FaceDetectorYN? _detector;
    private HeadPoseSolver? _solver;
    private FaceLandmarker? _landmarker;
    private bool _faceReady;
    private bool _faceFailed;

    private GestureRecognizer? _gestures;
    private bool _gestureFailed;

    private int _frameW, _frameH, _detectW, _detectH;
    private float _detectScale;

    public CameraFaceSource(CameraConfig cfg, bool face, bool gesture, bool verbose)
    {
        _cfg = cfg;
        _verbose = verbose;
        _faceEnabled = face;
        _gestureEnabled = gesture;
    }

    /// <summary>
    /// Check the models a set of stages needs, and throw describing the first
    /// one that is missing or broken.
    /// </summary>
    /// <remarks>
    /// Static, and called before a stage is switched on rather than from the
    /// constructor, because a stage can now be switched on while the camera is
    /// already running. The caller is on the UI thread and can put the message
    /// in front of the user; the loop, on a background thread, could only write
    /// it to a log nobody is reading.
    ///
    /// A Git-LFS pointer is a ~130 byte text file that downloads with a 200, and
    /// OpenCV's error for it is an unreadable ONNX parse failure. The size check
    /// catches it here, where the message can say what actually happened.
    /// </remarks>
    public static void CheckModels(CameraConfig cfg, bool face, bool gesture)
    {
        if (face) Check(cfg.ModelPath, 50_000, "Face detector");
        if (!gesture) return;
        Check(cfg.Gesture.PalmModelPath, 500_000, "Palm detector");
        Check(cfg.Gesture.LandmarkModelPath, 500_000, "Hand landmark model");

        static void Check(string configured, long minBytes, string what)
        {
            var path = AppPaths.Resolve(configured);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"{what} model not found at {path}. Run tools\\install-models.bat.", path);

            var size = new FileInfo(path).Length;
            if (size < minBytes)
                throw new InvalidDataException(
                    $"{path} is only {size} bytes, so it is a Git-LFS pointer rather than the " +
                    "model. Re-run tools\\install-models.bat.");
        }
    }

    /// <summary>
    /// The size the camera actually opened at, once it has. Zero before that.
    /// </summary>
    /// <remarks>
    /// A camera given a size it does not support does not fail — it quietly
    /// hands back the nearest mode it has, and every pixel-based number
    /// afterwards is about that one. Asking for 4K on a 1080p webcam and being
    /// told "camera 0 (3840x2160)" is a lie the player would then repeat in its
    /// own banner.
    /// </remarks>
    public int OpenedWidth { get; private set; }

    public int OpenedHeight { get; private set; }

    /// <summary>Raised on the camera thread once the device is open, with its real size.</summary>
    public event Action<int, int>? Opened;

    public string Name => OpenedWidth > 0
        ? $"camera {_cfg.DeviceIndex} ({OpenedWidth}x{OpenedHeight})"
        : $"camera {_cfg.DeviceIndex} ({_cfg.Width}x{_cfg.Height} requested)";

    public event Action<HeadPose>? PoseReceived;

    /// <summary>Raised with every processed frame. Only the preview uses it.</summary>
    public event Action<Mat, FaceLandmarks?, HeadPose?>? FrameProcessed;

    /// <summary>The 68 landmarks behind the last pose, for the preview to draw.</summary>
    public event Action<Point2f[]?>? LandmarksProcessed;

    /// <summary>Raised when a gesture fires, on the camera thread.</summary>
    public event Action<Gesture>? GestureFired;

    /// <summary>Raised when gesture mode is entered or left, on the camera thread.</summary>
    public event Action<bool>? GestureModeChanged;

    /// <summary>Raised after every gesture pass, for the on-screen hand panel.</summary>
    public event Action<HandView>? GestureObserved;

    /// <summary>Raised once when gesture control has run a while and never seen a hand.</summary>
    public event Action? GestureHandNeverFound;

    /// <summary>
    /// Raised once the capture loop has ended, for any reason.
    ///
    /// The preview needs this: without it, a camera that fails to open leaves
    /// the diagnostic sitting at an empty window forever, which is the least
    /// helpful thing a diagnostic can do.
    /// </summary>
    public event Action<string>? Stopped;

    /// <summary>Whether the face stage runs. Changing it does not reopen the camera.</summary>
    public bool FaceEnabled
    {
        get => _faceEnabled;
        set => _faceEnabled = value;
    }

    /// <summary>Whether the gesture stage runs. Changing it does not reopen the camera.</summary>
    public bool GestureEnabled
    {
        get => _gestureEnabled;
        set => _gestureEnabled = value;
    }

    // The face the detector last found, kept so the landmarker can be pointed at
    // it again without paying for another detection. Only ever a box the
    // detector actually produced — never one derived from the landmarks, which
    // is a different rectangle and would move every point it then produced.
    private FaceLandmarks? _lastFace;
    private Rect? _lastBox;
    private double _lastDetectAt = double.NegativeInfinity;
    private bool _redetect = true;

    /// <summary>True while gesture mode is on, which is also while head tracking is paused.</summary>
    public bool GestureArmed => _gestures?.Armed == true;

    /// <summary>The recognizer, for the diagnostic overlay to read. Null until the stage starts.</summary>
    public GestureRecognizer? Gestures => _gestures;

    /// <summary>Which action table the gestures resolve against. Set by the session.</summary>
    public bool VrMode
    {
        get => _gestures?.Vr ?? false;
        set { if (_gestures is not null) _gestures.Vr = value; }
    }

    public void Start(CancellationToken ct)
    {
        _thread = new Thread(() => Run(ct))
        {
            IsBackground = true,
            Name = "camera",

            // Below normal, because this thread must lose. It competes with
            // video decoding for the same cores, and a dropped video frame is
            // far more noticeable than a head pose arriving 30 ms late. At
            // normal priority on a laptop playing 4K60 the whole player became
            // unresponsive the moment tracking was switched on -- including the
            // close button, which is why it took several attempts to shut down.
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void Run(CancellationToken ct)
    {
        VideoCapture? capture = null;
        var reason = "stopped";

        try
        {
            // OpenCV must not fan out across the machine.
            //
            // This was the miss behind "the whole player freezes the moment
            // tracking is switched on". Everything else had been budgeted or
            // deprioritised — the landmarker got a duty cycle, this thread was
            // dropped to BelowNormal, ONNX was cut to two threads — and the
            // freeze still began before the landmarker had even loaded, which
            // is the clue that mattered: the cost was in the *detector*.
            //
            // FaceDetectorYN.Detect is a CNN run through OpenCV's DNN module,
            // on every camera frame, and OpenCV parallelises it with its own
            // internal pool. Those threads are not ours: they ignore this
            // thread's priority and every budget in this file, and there is one
            // per core. On a laptop already decoding 4K60 that is the entire
            // machine.
            //
            // One thread makes OpenCV run the work inline on the caller, so it
            // inherits BelowNormal and counts against the budget below like
            // everything else.
            //
            // It stays at one now that the detector is given a 640-wide frame
            // rather than the whole 1280x720, because at that size the threads
            // buy almost nothing. Measured here, YuNet at 640x360:
            //
            //     cv threads   ms/call   core-seconds/call
            //              1      23.9               0.024
            //              4      19.4               0.062
            //
            // 19% of the latency for 2.6x the CPU. At 1280x720 the same table
            // reads 118 ms and 0.116 against 127 ms and 0.338 — the extra
            // threads were slower *and* three times the cost, which is what a
            // memory-bound convolution does when you fan it out. Cutting the
            // resolution, not the threads, is what made this cheap.
            //
            // It covers the hand models too, which reach OpenCV only through
            // warpAffine and resize — small operations on 224x224 images, where
            // the fan-out would cost more in coordination than it saves.
            Cv2.SetNumThreads(1);

            // DirectShow rather than the default MSMF backend: MSMF takes
            // several seconds to open some cameras and ignores the requested
            // frame size on others.
            capture = new VideoCapture(_cfg.DeviceIndex, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
            {
                Console.Error.WriteLine(
                    $"  [camera] could not open device {_cfg.DeviceIndex}. " +
                    "Check that no other application is using it, and that camera access is allowed " +
                    "under Windows Settings > Privacy > Camera.");
                reason = $"device {_cfg.DeviceIndex} would not open";
                return;
            }

            capture.Set(VideoCaptureProperties.FrameWidth, _cfg.Width);
            capture.Set(VideoCaptureProperties.FrameHeight, _cfg.Height);
            capture.Set(VideoCaptureProperties.Fps, _cfg.Fps);

            // What we asked for and what we got are often different, and every
            // later number depends on the real size.
            _frameW = (int)capture.Get(VideoCaptureProperties.FrameWidth);
            _frameH = (int)capture.Get(VideoCaptureProperties.FrameHeight);
            if (_frameW <= 0 || _frameH <= 0) { _frameW = _cfg.Width; _frameH = _cfg.Height; }

            OpenedWidth = _frameW;
            OpenedHeight = _frameH;
            var asked = _frameW == _cfg.Width && _frameH == _cfg.Height
                ? ""
                : $" ({_cfg.Width}x{_cfg.Height} was asked for; the camera does not offer it)";
            Console.WriteLine($"  [camera] device {_cfg.DeviceIndex} open at {_frameW}x{_frameH}{asked}");
            Opened?.Invoke(_frameW, _frameH);

            // The detector gets a smaller frame than the landmarker does.
            //
            // Detection cost is set by the pixels handed to it, and it was being
            // handed all of them: 118 ms a call at 1280x720 on one OpenCV
            // thread, four times what the landmarker costs, on every single
            // frame. At 640x360 the same call is 24 ms and a fifth of the CPU.
            //
            // Nothing downstream sees this. The rows come back scaled to full
            // resolution below, so the crop the landmarker takes is still cut
            // from the full-resolution frame — which is the half that actually
            // needs the pixels.
            _detectW = _cfg.DetectWidth > 0 ? Math.Min(_cfg.DetectWidth, _frameW) : _frameW;
            _detectH = _detectW == _frameW ? _frameH
                                           : (int)Math.Round(_frameH * (double)_detectW / _frameW);
            _detectScale = (float)_frameW / _detectW;

            Loop(capture, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [camera] stopped: {ex.Message}");
            reason = ex.Message;
        }
        finally
        {
            _gestures?.Dispose();
            _landmarker?.Dispose();
            _solver?.Dispose();
            _detector?.Dispose();
            capture?.Dispose();
            Stopped?.Invoke(reason);
        }
    }

    private void Loop(VideoCapture capture, CancellationToken ct)
    {
        using var frame = new Mat();
        using var small = new Mat();
        using var faces = new Mat();
        var lostFrames = 0;
        var warnedNoFace = false;
        var lastLandmarkAt = double.NegativeInfinity;
        var lastLandmarkCost = 0.0;

        // Counted over a wall-clock window, and the rate is counted rather
        // than computed. The version this replaces derived a rate from the
        // landmarker's cost and its share, which ignored both the detector
        // and the idle at the end of the loop: it printed "running at 5 fps"
        // on a machine that was in fact producing under three poses a
        // second. A diagnostic that flatters the thing it measures is worse
        // than none, because it is believed.
        var windowStart = Clock.Seconds;
        var windowFrames = 0;
        var windowFaceFrames = 0;
        var windowPoses = 0;

        // The spread of the gap between poses, not just its average.
        //
        // Averages hide the thing that is actually watched. The loop waits
        // on a camera delivering frames on a 33 ms grid, so a cycle that is
        // not a multiple of 33 lands on a different frame boundary each
        // time and the interval beats between two values. At 320 ms per
        // pose that swing is a tenth of the interval; at 145 ms it is
        // closer to a quarter, which is why a *faster* loop can look worse.
        var lastPoseAt = double.NaN;
        var gapMin = double.MaxValue;
        var gapMax = 0.0;
        var windowDetectMs = 0.0;
        var windowDetectRuns = 0;
        var windowLandmarkMs = 0.0;
        var windowIdleMs = 0.0;
        var windowGestureRuns = 0;
        var windowGestureMs = 0.0;
        var windowArmed = false;
        var windowPausedSeconds = 0.0;
        var lastCycleAt = Clock.Seconds;
        var wasPaused = false;

        while (!_stop && !ct.IsCancellationRequested)
        {
            // Both stages off means the session is about to release the camera.
            // Reading frames for nobody in the meantime is pure waste.
            if (!_faceEnabled && !_gestureEnabled) { Thread.Sleep(50); continue; }

            if (!capture.Read(frame) || frame.Empty())
            {
                Thread.Sleep(10);
                continue;
            }

            // Everything from here to the end of the loop is the work being
            // budgeted. Reading the frame is not: that blocks waiting for
            // the camera and costs no CPU.
            var workStart = Clock.Seconds;

            // The camera sees a mirror image of what the viewer does. Without
            // this, turning your head left moves the view right — and a hand
            // pointing right would seek backwards.
            if (_cfg.Mirror) Cv2.Flip(frame, frame, FlipMode.Y);

            windowFrames++;

            // Gesture first, because whether it is armed decides whether the
            // face stage runs at all this frame. Doing it the other way round
            // would spend the face pipeline's 50 ms and then discard it.
            if (_gestureEnabled)
            {
                var before = Clock.Seconds;
                if (RunGestureStage(frame, before))
                {
                    windowGestureRuns++;
                    windowGestureMs += (Clock.Seconds - before) * 1000;
                }
                windowArmed |= GestureArmed;
            }

            // Time the face stage spent switched off on purpose, kept apart from
            // time it spent being slow.
            //
            // Charged a cycle late, because a cycle's length is only known once
            // the next one starts. That loses the last paused cycle of a window,
            // which is one frame out of a window of dozens.
            if (wasPaused) windowPausedSeconds += workStart - lastCycleAt;
            lastCycleAt = workStart;
            wasPaused = _faceEnabled && GestureArmed;

            // A gap that spans gesture mode is not a gap in tracking, and
            // printing it as one produced "gap 100-34415 ms" in a session where
            // nothing had gone wrong at all. Forgetting the last pose here means
            // the first pose after gesture mode starts a fresh interval.
            if (wasPaused) lastPoseAt = double.NaN;

            HeadPose? pose = null;
            Point2f[]? dense = null;
            FaceLandmarks? lm = null;

            // Head tracking yields to gesture mode. Not a cost measure alone:
            // the head moves while a hand is being waved at the screen, and a
            // view that swings around during that is worse than one that holds
            // still. The mapper freezes where it is on its own — it never snaps
            // back to centre — so the picture simply waits.
            if (_faceEnabled && !GestureArmed && EnsureFaceStage())
            {
                windowFaceFrames++;

                // The detector does not have to run on every frame, and it was.
                //
                // It is the second most expensive thing in the loop — 44 ms
                // against the landmarker's 88 in a reported session — and it is
                // answering a question whose answer barely changes: where is the
                // face. A head does not leave its own box between two frames a
                // tenth of a second apart. The 68-point model is the one that has
                // to run every frame, because *it* is the measurement.
                //
                // Skipped only when there is a landmarker to follow with. Without
                // one the detector's five points are the pose, so reusing them
                // would freeze the view rather than save anything.
                //
                // This is deliberately not the other saving available here.
                // Detecting on a smaller image is cheaper too, and it is the
                // wrong trade for the case that needs help: a face across the
                // room is already only a few dozen pixels wide, and halving them
                // is how you turn a slow tracker into one that finds no face at
                // all. Running the full-size detector less often costs the same
                // and costs it nowhere.
                var stale = _lastFace is null || _redetect ||
                            _landmarker is null ||
                            workStart - _lastDetectAt >= (_cfg.DetectFps > 0 ? 1.0 / _cfg.DetectFps : 0);

                if (stale)
                {
                    var detectStart = Clock.Seconds;
                    if (_detectW == _frameW)
                    {
                        _detector!.Detect(frame, faces);
                    }
                    else
                    {
                        Cv2.Resize(frame, small, new Size(_detectW, _detectH));
                        _detector!.Detect(small, faces);
                        ScaleFaces(faces, _detectScale);
                    }
                    windowDetectMs += (Clock.Seconds - detectStart) * 1000;
                    windowDetectRuns++;

                    _lastFace = Largest(faces, frame.Width, frame.Height);
                    _lastBox = _lastFace is not null ? BoxOf(faces) : null;
                    _lastDetectAt = workStart;
                    _redetect = false;
                }

                lm = _lastFace;

                if (lm is { } marks)
                {
                    // 68 points when the landmarker is available, the detector's
                    // five otherwise. Not a preference — five cannot measure
                    // pitch, so the fallback is a degraded mode, kept only so
                    // the player still tracks at all without the 13 MB model.
                    var t = Clock.Seconds;

                    // Two limits, and the second is the one that matters on a
                    // slow machine: a rate, and a share of wall-clock time.
                    //
                    // The rate alone does nothing when the model cannot keep up
                    // with it. Asking for 30 a second from something that costs
                    // 58 ms means the loop never idles — it runs flat out, at
                    // 100% duty, against a video decoder that needs the same
                    // cores. That is the "everything crawls the moment tracking
                    // is switched on" report: not a leak, not a deadlock, just
                    // a throttle that never engages because the target was
                    // already unreachable.
                    //
                    // So the interval also has a floor of cost/share: a 58 ms
                    // call at a 35% share may run every 166 ms, about 6 a
                    // second, and the machine gets the rest back. A 28 ms call
                    // on a fast machine needs 80 ms, under the 33 ms the rate
                    // asks for, so nothing changes there.
                    var byRate = _cfg.LandmarkFps > 0 ? 1.0 / _cfg.LandmarkFps : 0;
                    var byBudget = _cfg.TrackingCpuShare > 0 && lastLandmarkCost > 0
                        ? lastLandmarkCost / Math.Clamp(_cfg.TrackingCpuShare, 0.05, 1.0)
                        : 0;
                    var dueForLandmarks = t - lastLandmarkAt >= Math.Max(byRate, byBudget);

                    if (_landmarker is not null && dueForLandmarks && _lastBox is { } box)
                    {
                        lastLandmarkAt = t;

                        // Timed and reported once: "the landmarker is slow on
                        // this machine" is otherwise indistinguishable from any
                        // other cause of stutter, and the fix differs.
                        var started = Clock.Seconds;
                        dense = _landmarker.Locate(frame, box);
                        lastLandmarkCost = Clock.Seconds - started;
                        windowLandmarkMs += lastLandmarkCost * 1000;

                        if (dense is not null)
                            pose = ToTrackerConvention(_solver!.Solve68(dense, t));

                        // The 68 points are also the check on the box they came
                        // out of. A box the head has moved out from under still
                        // produces landmarks — the crop is 1.6 times the box, so
                        // there is room to be wrong in — and they come back
                        // quietly displaced, which is the failure this whole
                        // arrangement has to not have.
                        //
                        // So the detector is called back the moment the face is
                        // no longer sitting where the box says. The timer above
                        // is only the backstop; this is what actually decides.
                        _redetect = dense is null || !FaceGeometry.StillFits(dense, box);
                    }

                    // Only fall back to the five-point solve when there is no
                    // landmarker at all — not on the frames between its runs.
                    // Mixing the two would feed the filter a stream that jumps
                    // between a precise estimate and a coarse one, which reads
                    // as jitter even though both are behaving.
                    if (pose is null && _landmarker is null)
                        pose = ToTrackerConvention(_solver!.Solve(marks, t));
                    if (pose is { } p)
                    {
                        lostFrames = 0;
                        warnedNoFace = false;
                        windowPoses++;

                        var poseAt = Clock.Seconds;
                        if (!double.IsNaN(lastPoseAt))
                        {
                            var gap = poseAt - lastPoseAt;
                            gapMin = Math.Min(gapMin, gap);
                            gapMax = Math.Max(gapMax, gap);
                        }
                        lastPoseAt = poseAt;
                        PoseReceived?.Invoke(p);
                        if (_verbose) Console.WriteLine($"[camera] {p}");
                    }
                }
                else if (++lostFrames == 90 && !warnedNoFace)
                {
                    // Roughly three seconds. The view holds its last position on
                    // its own (CheckStale), so this is information, not an error.
                    warnedNoFace = true;
                    Console.WriteLine("  [camera] no face detected — check lighting and that you are in frame");
                }
            }

            LandmarksProcessed?.Invoke(dense);
            FrameProcessed?.Invoke(frame, lm, pose);

            // Hold the whole pipeline to its share of the machine.
            //
            // The budget used to cover only the landmarker, which left the
            // detector running on every single frame with no limit at all —
            // and the detector turned out to be the expensive half. Sleeping
            // here bounds everything the loop does: capture conversion,
            // detection, landmarks, the solve, and now the gesture stage.
            //
            // work / (work + idle) = share, so idle = work * (1/share - 1).
            //
            // Read that as a latency multiplier and not only as a CPU cap,
            // because it is both: every point of share taken away is added
            // to the gap between your head moving and the view following.
            // At 0.35 it was tripling a loop that was already slow. At 0.75
            // the idle is a third of the work — about 19 ms here, under one
            // camera frame, so the queued frame the next Read returns is
            // still a fresh one.
            var share = Math.Clamp(_cfg.TrackingCpuShare, 0.05, 1.0);
            if (share < 1.0)
            {
                var work = Clock.Seconds - workStart;
                var idle = work * (1.0 / share - 1.0);
                // Capped: a single pathological frame must not stall
                // tracking for seconds afterwards.
                if (idle > 0.001)
                {
                    var ms = (int)Math.Min(400, idle * 1000);
                    windowIdleMs += ms;
                    Idle(ms);
                }
            }

            var elapsed = Clock.Seconds - windowStart;
            if (elapsed < 5.0 || windowFrames == 0) continue;

            Report(elapsed, windowFrames, windowFaceFrames, windowPoses,
                   windowDetectMs, windowLandmarkMs, windowIdleMs,
                   windowGestureRuns, windowGestureMs, windowArmed, gapMin, gapMax,
                   windowPausedSeconds, windowDetectRuns);

            windowStart = Clock.Seconds;
            windowFrames = windowFaceFrames = windowPoses = 0;
            windowDetectMs = windowLandmarkMs = windowIdleMs = 0;
            windowDetectRuns = 0;
            windowGestureRuns = 0;
            windowGestureMs = 0;
            windowArmed = false;
            windowPausedSeconds = 0;
            gapMin = double.MaxValue;
            gapMax = 0;
        }
    }

    /// <summary>
    /// The duty-cycle wait, in slices, so that stopping does not have to sit
    /// through it.
    /// </summary>
    /// <remarks>
    /// One <c>Thread.Sleep(400)</c> is indistinguishable from a hang as far as
    /// <see cref="Dispose"/> is concerned: it joins this thread, and the join
    /// cannot complete until the sleep does. That was tolerable while the only
    /// thing waiting was process shutdown, and stopped being tolerable when the
    /// camera diagnostic started borrowing the device — it runs in another
    /// process and gets exactly one attempt to open it, so a join that has to
    /// wait out a sleep shows the user "could not open device 0", which is what
    /// a genuinely broken webcam says too.
    ///
    /// 50 ms slices, so the join is bounded by that plus whatever a frame read
    /// is still blocked in, rather than by the budget.
    /// </remarks>
    private void Idle(int milliseconds)
    {
        const int slice = 50;
        for (var left = milliseconds; left > 0 && !_stop; left -= slice)
            Thread.Sleep(Math.Min(slice, left));
    }

    // -------------------------------------------------------- face stage ---

    /// <summary>
    /// Build the face stage if it is not built yet. False when it cannot be.
    /// </summary>
    /// <remarks>
    /// Built here rather than before the loop because head tracking can now be
    /// switched on after the camera is already running — gesture control may
    /// have opened it. Attempted once: a failure is a missing or broken model
    /// file, and retrying it on every frame would fill the log at 30 lines a
    /// second with the same sentence.
    /// </remarks>
    private bool EnsureFaceStage()
    {
        if (_faceReady) return true;
        if (_faceFailed) return false;
        _faceFailed = true;   // cleared at the end if everything comes up

        try { BuildFaceStage(); }
        catch (Exception ex)
        {
            // Contained rather than allowed to unwind. The detector's model is
            // checked before this stage is switched on, so reaching here means
            // something worse — a corrupt file, a missing OpenCV dependency —
            // and letting it out of the loop would take the *camera* down with
            // it, which now means taking gesture control down as well. One
            // broken feature must not remove the other.
            Console.WriteLine($"  [camera] head tracking unavailable ({ex.Message.Split('\n')[0]})");
            return false;
        }

        _faceFailed = false;
        _faceReady = true;
        return true;
    }

    private void BuildFaceStage()
    {
        // Each step timed and announced. An earlier freeze left a log whose
        // last line was "device 0 open", and the gap after it contained three
        // separate things -- the detector's model load, the solver, and the
        // landmarker's ONNX session -- so there was no way to tell which one to
        // look at.
        var step = Clock.Seconds;
        _detector = FaceDetectorYN.Create(AppPaths.Resolve(_cfg.ModelPath), "",
                                          new Size(_detectW, _detectH),
                                          (float)_cfg.ScoreThreshold, 0.3f, 5000);
        Console.WriteLine($"  [camera] detector ready in {(Clock.Seconds - step) * 1000:F0} ms" +
                          (_detectW == _frameW ? $", detecting at {_frameW}x{_frameH}"
                                               : $", detecting at {_detectW}x{_detectH} of {_frameW}x{_frameH}"));

        step = Clock.Seconds;
        _solver = new HeadPoseSolver(_frameW, _frameH);

        // Optional: a missing landmarker degrades pitch, it does not stop
        // the player, so this must not be fatal.
        var landmarkPath = AppPaths.Resolve(_cfg.LandmarkModelPath);
        if (LandmarkerGuard.PreviousAttemptCrashed)
        {
            // Not a warning that can be dismissed by trying again: the last
            // attempt did not throw, it killed the process. Repeating it
            // would just kill this one too.
            Console.WriteLine("  [camera] skipping the 68-point landmarker — it crashed the player last time");
            Console.WriteLine($"  [camera] delete {AppPaths.LandmarkerGuardFile} to try it again");
            Console.WriteLine("  [camera] falling back to 5 points — up/down will be poor");
        }
        else
        {
            try
            {
                // This thread stays BelowNormal through the load.
                //
                // It was briefly raised to Normal, on the theory that ORT's
                // worker threads inherit the creating thread's priority and
                // a starved low-priority thread was hanging the load. The
                // crash report killed that theory — the failure is an access
                // violation inside ORT's static constructor, which no amount
                // of CPU would have prevented.
                //
                // The inheritance is real, though, and that made the raise
                // actively harmful: the pool is created once and keeps
                // whatever priority it was born with, so raising it here
                // promoted every later inference to Normal for the rest of
                // the session, permanently competing with the video decoder.
                // Reported as the player going stuttery in 0.2.4.
                RequireOwnOnnxRuntime();

                Console.WriteLine($"  [camera] loading landmarker {landmarkPath}");

                // Last line before the danger. If the log stops here, the
                // marker file is what tells the next run not to try again.
                LandmarkerGuard.Begin();
                _landmarker = new FaceLandmarker(landmarkPath) { Smoothing = _cfg.LandmarkSmoothing };
                LandmarkerGuard.Succeeded();
                Console.WriteLine($"  [camera] 68-point landmarker loaded in {(Clock.Seconds - step) * 1000:F0} ms");
                Console.WriteLine($"  [camera] onnxruntime in use: {OnnxLoader.LoadedNative()}");
            }
            catch (Exception ex)
            {
                // A thrown exception is the survivable case — a missing file
                // or a bad model. Clear the marker: this run is proof that
                // the attempt returns, so the next one should be allowed to
                // make it too.
                LandmarkerGuard.Succeeded();
                Console.WriteLine($"  [camera] no 68-point landmarker ({ex.Message.Split('\n')[0]})");
                Console.WriteLine("  [camera] falling back to 5 points — up/down will be poor");
            }
        }
    }

    // ----------------------------------------------------- gesture stage ---

    /// <summary>Run the gesture stage if it is due. True when it actually ran.</summary>
    private bool RunGestureStage(Mat frame, double now)
    {
        if (_gestureFailed) return false;

        if (_gestures is null)
        {
            try
            {
                // Same rule as the face landmarker, and for the same reason:
                // ORT's thread pool inherits the priority of whichever thread
                // built the session, so both hand models must be constructed
                // here, on the BelowNormal camera thread, and nowhere else.
                RequireOwnOnnxRuntime();

                var step = Clock.Seconds;
                _gestures = new GestureRecognizer(
                    _cfg.Gesture,
                    AppPaths.Resolve(_cfg.Gesture.PalmModelPath),
                    AppPaths.Resolve(_cfg.Gesture.LandmarkModelPath),
                    _cfg.Mirror);
                _gestures.Fired += g => GestureFired?.Invoke(g);
                _gestures.ArmedChanged += on => GestureModeChanged?.Invoke(on);
                _gestures.Observed += v => GestureObserved?.Invoke(v);
                _gestures.HandNeverFound += () => GestureHandNeverFound?.Invoke();
                Console.WriteLine($"  [camera] gesture models loaded in {(Clock.Seconds - step) * 1000:F0} ms");
            }
            catch (Exception ex)
            {
                // Attempted once. Unlike the face landmarker there is no
                // degraded mode to fall back to — without these models there
                // are no gestures — so this says so and stops.
                _gestureFailed = true;
                _gestures = null;
                Console.WriteLine($"  [camera] gesture control unavailable ({ex.Message.Split('\n')[0]})");
                return false;
            }
        }

        if (!_gestures.Due(now)) return false;
        _gestures.Process(frame, now);
        return true;
    }

    /// <summary>
    /// Refuse to touch ONNX Runtime unless it is the copy we shipped.
    /// </summary>
    /// <remarks>
    /// Refusing is the safe answer, not a cautious one. Going ahead would let
    /// ORT resolve "onnxruntime" by the default search, and once a video is
    /// playing there is already a mismatched System32 copy in the process for it
    /// to find. That does not fail — it takes the whole player down with an
    /// access violation that nothing can catch.
    ///
    /// Install() is normally a no-op: Program does it during startup, on
    /// purpose, so that our copy is in the process before mpv can bring in a
    /// different one. Repeated here because neither stage may depend on that
    /// having happened. A resolver only affects loads that have not happened
    /// yet, and the load that crashes is the first one.
    /// </remarks>
    private static void RequireOwnOnnxRuntime()
    {
        OnnxLoader.Install();
        if (!OnnxLoader.Ready)
            throw new InvalidOperationException(
                $"cannot use our own onnxruntime ({OnnxLoader.Failure}), " +
                "and using the system one would crash the player");
    }

    // ------------------------------------------------------ diagnostics ---

    /// <summary>
    /// One block, every five seconds, and every number in it is counted rather
    /// than derived.
    /// </summary>
    /// <remarks>
    /// The pose rate is the one that matters and the one that was being guessed
    /// at: the view moves in visible steps below about six poses a second, so it
    /// is the number to read first and the only one that says whether the
    /// tracker is keeping up.
    ///
    /// Each stage reports only when it ran. A face line printed during gesture
    /// mode would show zero poses and no faces found, which is true and reads
    /// exactly like the failure it is not.
    /// </remarks>
    private void Report(double elapsed, int frames, int faceFrames, int poses,
                        double detectMs, double landmarkMs, double idleMs,
                        int gestureRuns, double gestureMs, bool armedInWindow,
                        double gapMin, double gapMax, double pausedSeconds, int detectRuns)
    {
        var cycle = elapsed * 1000 / frames;

        if (faceFrames > 0)
        {
            // Against the time head tracking was actually allowed to run, not
            // against the wall clock.
            //
            // Gesture mode switches the face stage off on purpose, and a window
            // containing thirty seconds of it reported "1.9 poses/s" — a number
            // that reads as the tracker failing and is really the tracker doing
            // what it was told. The advice line already knew to stay quiet in
            // that case; the number it was staying quiet about was still wrong,
            // which is worse, because a suppressed warning is at least visibly
            // absent and a wrong rate looks like a measurement.
            var live = Math.Max(0.001, elapsed - pausedSeconds);
            var rate = poses / live;
            // Two numbers for the detector now that it does not run on every
            // frame: what one call costs, and what it contributes to a frame.
            // Printing only the first would make it look like the largest item
            // in a loop it is barely in; printing only the second would hide the
            // cost that detectWidth acts on.
            var perCall = detectRuns > 0 ? detectMs / detectRuns : 0;
            var perFrame = detectMs / faceFrames;
            var duty = detectRuns > 0 && detectRuns < faceFrames
                ? $" ({perCall:F0} ms, 1 frame in {(double)faceFrames / detectRuns:F0})"
                : "";
            var landmark = poses > 0 ? landmarkMs / poses : 0;
            var idle = idleMs / frames;
            // The hit rate is here because it was the thing a whole round of
            // analysis had to reconstruct by hand, by dividing the pose rate by
            // the frame rate across fifteen log lines. It ranged from 36% to
            // 100% in one session, and a third of frames finding no face is a
            // completely different problem from a slow model — different cause,
            // different fix, and invisible in every other number printed here.
            var found = poses * 100.0 / faceFrames;
            var spread = gapMax > 0 && gapMin < double.MaxValue
                ? $", gap {gapMin * 1000:F0}-{gapMax * 1000:F0} ms"
                : "";
            var paused = pausedSeconds > 0.2 ? $" ({pausedSeconds:F1} s of it in gesture mode)" : "";
            Console.WriteLine(
                $"  [camera] {rate:F1} poses/s{spread} — {cycle:F0} ms per frame " +
                $"(detect {perFrame:F0}{duty}, landmark {landmark:F0}, idle {idle:F0}), " +
                $"face found on {found:F0}% of frames{paused}");

            // Which of the three explanations it is, rather than one message
            // that fits all of them badly. The first version of this said "that
            // is slow enough to look like stutter" at a camera pointed at a
            // wall, where the honest answer is that there was no face to find.
            var waiting = cycle - perFrame - landmark - idle;
            if (poses == 0)
                Console.WriteLine("  [camera] no poses at all — the face is not being found, " +
                                  "which is a lighting and framing problem, not a speed one");
            // Runs of frames with no face are what produce the multi-second gaps
            // that freeze the view, and no amount of speed fixes them. Worth its
            // own line, above the speed advice, because it is the larger
            // complaint when it happens.
            else if (found < 80)
                Console.WriteLine($"  [camera] the face is being lost on {100 - found:F0}% of frames — " +
                                  "try more even lighting, or source.camera.scoreThreshold lower " +
                                  "(0.5), or source.camera.detectWidth 0 to detect at full size");
            else if (waiting > cycle * 0.5)
                Console.WriteLine($"  [camera] {waiting:F0} ms of each frame is spent waiting for the " +
                                  "camera, so the camera is the limit here, not the tracking");
            // Six is where the simulation of the real view mapper stops showing
            // output frames with the picture frozen. Below it the complaint is
            // always the same, and it is never described as slowness: the view
            // jumps to the next angle and waits there. The glide stretches to
            // cover it, so this is a note about cost, not a fault.
            //
            // Not while gesture mode has been on, though. The face stage is
            // deliberately not running then, so the rate over the window is low
            // by design — and a real log showed "0.6 poses/s" followed by three
            // lines of advice about making detection cheaper, none of which had
            // anything to do with it.
            else if (rate < 6.0 && !armedInWindow)
                Console.WriteLine(
                    "  [camera] that is below the rate the view can hide; the glide is stretching " +
                    "to cover it. " + Advice(perFrame, perCall, landmark, idle, cycle));
        }

        if (gestureRuns == 0) return;

        ReportGestures(elapsed, frames, gestureRuns, gestureMs);
    }

    /// <summary>
    /// What to do about a slow loop, chosen from the three numbers just printed.
    /// </summary>
    /// <remarks>
    /// This line used to say "the biggest item above is the one to attack" and
    /// then name a fixed pair of settings. A reported log had detect 44, landmark
    /// 88, idle 49 — the landmarker was the largest by a factor of two, and the
    /// advice talked about detection. The sentence was not merely unhelpful, it
    /// was false about its own reasoning, which is the kind of diagnostic that
    /// sends someone off changing the wrong setting and concluding the setting
    /// does nothing.
    ///
    /// The landmarker case is the awkward one and it is the common one: there is
    /// no key that makes the 68-point model cheaper. Saying so is the point.
    /// Offering the other two anyway, with the arithmetic, at least lets the
    /// reader decide whether the remaining saving is worth having.
    /// </remarks>
    private string Advice(double detect, double detectPerCall, double landmark, double idle, double cycle)
    {
        var share = Math.Clamp(_cfg.TrackingCpuShare, 0.05, 1.0);
        var idleAdvice = share < 1.0
            ? $"source.camera.trackingCpuShare 1.0 gives back {idle:F0} ms"
            : "";

        // Which detector setting to name depends on why it is expensive, and the
        // two answers pull in opposite directions. Running it less often costs
        // nothing in what it can see; running it on a smaller image costs exactly
        // that, and the people who most need the milliseconds are often the ones
        // sitting furthest from the camera, whose face is already small. So the
        // rate is named first, and the width only once the rate is already low.
        var detectAdvice = _cfg.DetectFps <= 0 || detectPerCall > 0 && detect > detectPerCall * 0.5
            ? $"source.camera.detectFps lower than {Math.Max(1, _cfg.DetectFps):0.##} runs it less often"
            : $"source.camera.detectWidth 320 makes each detection ~4x cheaper — but only if your " +
              "face is large in frame; it is the wrong trade at a distance";

        if (landmark >= detect && landmark >= idle)
        {
            var rest = string.Join(", ", new[] { idleAdvice }.Where(s => s.Length > 0));
            return $"the 68-point landmarker is the biggest at {landmark:F0} ms and no setting makes " +
                   $"it cheaper — it is the measurement, and it has to run every frame. " +
                   (rest.Length > 0
                       ? $"Of the {cycle:F0} ms cycle, {rest}."
                       : "Everything else here is already as cheap as it goes.");
        }

        if (detect >= idle)
            return $"detection is the biggest at {detect:F0} ms per frame — {detectAdvice}.";

        return $"the idle is the biggest at {idle:F0} ms, which is source.camera.trackingCpuShare " +
               $"{share:0.##} holding the loop back on purpose. Raise it to 1.0 to spend it on tracking.";
    }

    /// <summary>The gesture half of the block. Split out only for length.</summary>
    private void ReportGestures(double elapsed, int frames, int gestureRuns, double gestureMs)
    {
        var cycle = elapsed * 1000 / frames;

        // Scanning against following is the number that explains the cost. The
        // palm detector is 12 ms and the landmark model is 6, so a stage that
        // is following a hand costs a third of one that is looking for one --
        // and "looking for one" is what it does whenever a hand is out of frame,
        // which is most of the time.
        var state = _gestures is { } g
            ? g.Armed ? _faceEnabled ? "gesture mode ON, head tracking paused" : "gesture mode ON"
                      : g.Scanning ? "watching for a hand" : "following a hand"
            : "starting";
        var looks = gestureRuns / elapsed;

        // How far a sweep actually got, when one was attempted. Silent
        // otherwise: most windows contain no attempt at all, and a zero printed
        // every five seconds would bury the ones that matter.
        var reach = _gestures?.TakeSwipeReach() ?? 0;
        var swipe = reach < 0.3 ? ""
            : reach >= _cfg.Gesture.SwipeTravelPalms
                ? $", swipe reached {reach:F1} palm widths"
                : $", a sweep reached {reach:F1} of the {_cfg.Gesture.SwipeTravelPalms:F1} " +
                  "palm widths a swipe needs";

        Console.WriteLine($"  [camera] gestures: {looks:F1} looks/s, " +
                          $"{gestureMs / gestureRuns:F0} ms each — {state}{swipe}");

        // The camera's own rate, on the same line's worth of context, because
        // without it the rate above cannot be explained.
        //
        // The stage is only offered a frame when one arrives, so the achievable
        // rates are quantised to the camera's grid: asking for 12 a second on a
        // 30 fps camera means an 83 ms interval rounded up to three frames, and
        // what comes out is exactly 10.0. That reads as "it is not keeping up"
        // and it is nothing of the kind — the loop is idle most of the time.
        // Said explicitly, because the alternative is someone lowering a
        // threshold to fix a number that was never wrong.
        var wanted = _gestures is { Armed: true } ? _cfg.Gesture.ArmedFps : _cfg.Gesture.IdleFps;
        var camera = frames / elapsed;
        var reachable = camera > 0 && wanted > 0
            ? camera / Math.Max(1, Math.Ceiling(camera / wanted))
            : wanted;

        var why = wanted > 0 && looks < wanted * 0.9 && Math.Abs(looks - reachable) < reachable * 0.15
            ? $"; {wanted:F0} asked for, but the camera's {camera:F0} fps grid only allows {reachable:F1}"
            : "";
        Console.WriteLine($"  [camera] camera {camera:F0} fps, {cycle:F0} ms per frame{why}");
    }

    /// <summary>
    /// Camera geometry to the convention the rest of the player expects. Two
    /// corrections that are deliberately kept apart, because they have
    /// different causes and different conditions.
    /// </summary>
    private HeadPose? ToTrackerConvention(HeadPose? pose)
    {
        if (pose is not { } p) return null;

        // 1. Undo the mirror. The frame is flipped before detection, so the
        //    solver returns the pose of a reflection. Reflecting about a
        //    vertical plane negates yaw and roll — rotations about the vertical
        //    and the viewing axis — and leaves pitch, whose axis lies in the
        //    mirror plane. Sideways translation flips with it. Conditional,
        //    because it is undoing something we chose to do.
        if (_cfg.Mirror) p = p with { Yaw = -p.Yaw, Roll = -p.Roll, X = -p.X };

        // 2. Flip pitch. Not a mirror effect — the test in the unit suite shows
        //    reflection leaves pitch untouched, and this applies whether or not
        //    the frame is mirrored. It is a plain disagreement about which way
        //    is positive: rotating the facing vector by +pitch about X gives it
        //    a +Y component and +Y is down the image, so the solver calls
        //    looking *down* positive, while the view wants positive to mean up.
        return p with { Pitch = -p.Pitch };
    }

    /// <summary>
    /// Detection results back to full-frame coordinates.
    ///
    /// Done here, immediately after the detect call, so that it is the only
    /// place in this file that knows the detector saw a smaller picture.
    /// Everything after this — the box, the five points, the bounds check
    /// against the frame — is in the frame's own pixels, as it always was.
    ///
    /// Columns 0..13 are the rectangle and the five landmark pairs. Column 14
    /// is the confidence score and must not be touched.
    /// </summary>
    private static void ScaleFaces(Mat faces, float scale)
    {
        for (var i = 0; i < faces.Rows; i++)
            for (var k = 0; k < 14; k++)
                faces.Set(i, k, faces.At<float>(i, k) * scale);
    }

    /// <summary>The largest face's rectangle, matching what <see cref="Largest"/> picked.</summary>
    private static Rect? BoxOf(Mat faces)
    {
        if (faces.Rows == 0) return null;

        var best = -1;
        var bestArea = 0f;
        for (var i = 0; i < faces.Rows; i++)
        {
            var area = faces.At<float>(i, 2) * faces.At<float>(i, 3);
            if (area <= bestArea) continue;
            bestArea = area;
            best = i;
        }
        if (best < 0) return null;

        return new Rect((int)faces.At<float>(best, 0), (int)faces.At<float>(best, 1),
                        (int)faces.At<float>(best, 2), (int)faces.At<float>(best, 3));
    }

    /// <summary>
    /// The biggest face in the frame, as landmarks. Biggest rather than
    /// highest-scoring: with a second person in the background, the viewer is
    /// the one filling the frame, and score does not track distance.
    ///
    /// YuNet returns one row per face: x, y, w, h, then five landmark pairs,
    /// then the score. It names the first pair the "right eye" meaning the
    /// subject's right — but we hand it a mirrored frame, so that slot is the
    /// point on the image's left. FaceLandmarks is named for image position for
    /// exactly this reason; see the note there.
    /// </summary>
    private static FaceLandmarks? Largest(Mat faces, int frameW, int frameH)
    {
        if (faces.Rows == 0) return null;

        var best = -1;
        var bestArea = 0f;
        for (var i = 0; i < faces.Rows; i++)
        {
            var area = faces.At<float>(i, 2) * faces.At<float>(i, 3);
            if (area <= bestArea) continue;
            bestArea = area;
            best = i;
        }
        if (best < 0) return null;

        // A face partly outside the frame gives landmarks extrapolated beyond
        // the edge, and the pose from those swings wildly.
        for (var k = 4; k < 14; k += 2)
        {
            var x = faces.At<float>(best, k);
            var y = faces.At<float>(best, k + 1);
            if (x < 0 || y < 0 || x >= frameW || y >= frameH) return null;
        }

        return new FaceLandmarks(
            faces.At<float>(best, 4), faces.At<float>(best, 5),
            faces.At<float>(best, 6), faces.At<float>(best, 7),
            faces.At<float>(best, 8), faces.At<float>(best, 9),
            faces.At<float>(best, 10), faces.At<float>(best, 11),
            faces.At<float>(best, 12), faces.At<float>(best, 13));
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1500);
    }
}
