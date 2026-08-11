using System.Diagnostics;
using HeadTrackBridge.Tracking.Face;
using OpenCvSharp;

// WinForms puts System.Drawing in the implicit usings, which collides with
// OpenCvSharp's Size.
using Size = OpenCvSharp.Size;

namespace HeadTrackBridge.Tracking;

/// <summary>
/// Head poses from a webcam: capture a frame, find one face, solve its rotation.
///
/// This is the source that makes opentrack optional. It produces exactly the
/// same <see cref="HeadPose"/> the UDP source does, so everything downstream —
/// the One Euro filter, the deadzone and gain curve, recentring, the content
/// limits — is untouched and already tuned.
///
/// Runs on its own thread. Grabbing a frame blocks for as long as the camera
/// takes, and doing that on a pool thread would tie one up permanently.
/// </summary>
public sealed class CameraFaceSource : ITrackingSource
{
    private readonly CameraConfig _cfg;
    private readonly string _modelPath;
    private readonly bool _verbose;

    private Thread? _thread;
    private volatile bool _stop;

    public CameraFaceSource(CameraConfig cfg, string modelPath, bool verbose)
    {
        _cfg = cfg;
        _modelPath = modelPath;
        _verbose = verbose;

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Face detector model not found at {modelPath}. Run tools\\install-models.bat.",
                modelPath);

        // A Git-LFS pointer is a ~130 byte text file that downloads with a 200,
        // and OpenCV's error for it is an unreadable ONNX parse failure. Catch
        // it here where the message can say what actually happened.
        var size = new FileInfo(modelPath).Length;
        if (size < 50_000)
            throw new InvalidDataException(
                $"{modelPath} is only {size} bytes, so it is a Git-LFS pointer rather than the " +
                "model. Re-run tools\\install-models.bat, which fetches it from media.githubusercontent.com.");
    }

    public string Name => $"camera {_cfg.DeviceIndex} ({_cfg.Width}x{_cfg.Height})";

    public event Action<HeadPose>? PoseReceived;

    /// <summary>Raised with every processed frame. Only the preview uses it.</summary>
    public event Action<Mat, FaceLandmarks?, HeadPose?>? FrameProcessed;

    /// <summary>The 68 landmarks behind the last pose, for the preview to draw.</summary>
    public event Action<Point2f[]?>? LandmarksProcessed;

    /// <summary>
    /// Raised once the capture loop has ended, for any reason.
    ///
    /// The preview needs this: without it, a camera that fails to open leaves
    /// the diagnostic sitting at an empty window forever, which is the least
    /// helpful thing a diagnostic can do.
    /// </summary>
    public event Action<string>? Stopped;

    public void Start(CancellationToken ct)
    {
        _thread = new Thread(() => Run(ct))
        {
            IsBackground = true,
            Name = "camera-face",

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
        FaceDetectorYN? detector = null;
        HeadPoseSolver? solver = null;
        FaceLandmarker? landmarker = null;
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
            var w = (int)capture.Get(VideoCaptureProperties.FrameWidth);
            var h = (int)capture.Get(VideoCaptureProperties.FrameHeight);
            if (w <= 0 || h <= 0) { w = _cfg.Width; h = _cfg.Height; }
            Console.WriteLine($"  [camera] device {_cfg.DeviceIndex} open at {w}x{h}");

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
            var detectW = _cfg.DetectWidth > 0 ? Math.Min(_cfg.DetectWidth, w) : w;
            var detectH = detectW == w ? h : (int)Math.Round(h * (double)detectW / w);
            var detectScale = (float)w / detectW;

            // Each step timed and announced. The previous freeze left a log
            // whose last line was "device 0 open", and the gap after it
            // contained three separate things -- the detector's model load, the
            // solver, and the landmarker's ONNX session -- so there was no way
            // to tell which one to look at.
            var step = Clock.Seconds;
            detector = FaceDetectorYN.Create(_modelPath, "", new Size(detectW, detectH),
                                             (float)_cfg.ScoreThreshold, 0.3f, 5000);
            Console.WriteLine($"  [camera] detector ready in {(Clock.Seconds - step) * 1000:F0} ms" +
                              (detectW == w ? $", detecting at {w}x{h}"
                                            : $", detecting at {detectW}x{detectH} of {w}x{h}"));

            step = Clock.Seconds;
            solver = new HeadPoseSolver(w, h);

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
                    // Before the first ORT type is touched: a resolver only
                    // affects loads that have not happened yet, and the load
                    // that crashes is the first one.
                    // Normally a no-op: Program does this during startup, on
                    // purpose, so that our copy is in the process before mpv can
                    // bring in a different one. Repeated here because the camera
                    // must not depend on that having happened.
                    OnnxLoader.Install();

                    // Refusing is the safe answer, not a cautious one.
                    //
                    // Going ahead would let ORT resolve "onnxruntime" by the
                    // default search, and once a video is playing there is
                    // already a mismatched System32 copy in the process for it
                    // to find. That does not fail — it takes the whole player
                    // down with an access violation that nothing can catch.
                    if (!OnnxLoader.Ready)
                        throw new InvalidOperationException(
                            $"cannot use our own onnxruntime ({OnnxLoader.Failure}), " +
                            "and using the system one would crash the player");

                    Console.WriteLine($"  [camera] loading landmarker {landmarkPath}");

                    // Last line before the danger. If the log stops here, the
                    // marker file is what tells the next run not to try again.
                    LandmarkerGuard.Begin();
                    landmarker = new FaceLandmarker(landmarkPath) { Smoothing = _cfg.LandmarkSmoothing };
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
            var windowPoses = 0;

            // The spread of the gap between poses, not just its average.
            //
            // Averages hide the thing that is actually watched. The loop waits
            // on a camera delivering frames on a 33 ms grid, so a cycle that is
            // not a multiple of 33 lands on a different frame boundary each
            // time and the interval beats between two values. At 320 ms per
            // pose that swing is a tenth of the interval; at 145 ms it is
            // closer to a quarter, which is why a *faster* loop can look worse.
            // Whether that is what is happening here is exactly what these two
            // numbers are for.
            var lastPoseAt = double.NaN;
            var gapMin = double.MaxValue;
            var gapMax = 0.0;
            var windowDetectMs = 0.0;
            var windowLandmarkMs = 0.0;
            var windowIdleMs = 0.0;

            while (!_stop && !ct.IsCancellationRequested)
            {
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
                // this, turning your head left moves the view right.
                if (_cfg.Mirror) Cv2.Flip(frame, frame, FlipMode.Y);

                var detectStart = Clock.Seconds;
                if (detectW == w)
                {
                    detector.Detect(frame, faces);
                }
                else
                {
                    Cv2.Resize(frame, small, new Size(detectW, detectH));
                    detector.Detect(small, faces);
                    ScaleFaces(faces, detectScale);
                }
                windowDetectMs += (Clock.Seconds - detectStart) * 1000;
                windowFrames++;

                var lm = Largest(faces, frame.Width, frame.Height);
                HeadPose? pose = null;
                Point2f[]? dense = null;

                if (lm is { } marks)
                {
                    // 68 points when the landmarker is available, the detector's
                    // five otherwise. Not a preference — five cannot measure
                    // pitch, so the fallback is a degraded mode, kept only so
                    // the player still tracks at all without the 93 MB model.
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

                    if (landmarker is not null && dueForLandmarks && BoxOf(faces) is { } box)
                    {
                        lastLandmarkAt = t;

                        // Timed and reported once: "the landmarker is slow on
                        // this machine" is otherwise indistinguishable from any
                        // other cause of stutter, and the fix differs.
                        var started = Clock.Seconds;
                        dense = landmarker.Locate(frame, box);
                        lastLandmarkCost = Clock.Seconds - started;
                        windowLandmarkMs += lastLandmarkCost * 1000;

                        if (dense is not null)
                            pose = ToTrackerConvention(solver.Solve68(dense, t));
                    }

                    // Only fall back to the five-point solve when there is no
                    // landmarker at all — not on the frames between its runs.
                    // Mixing the two would feed the filter a stream that jumps
                    // between a precise estimate and a coarse one, which reads
                    // as jitter even though both are behaving.
                    if (pose is null && landmarker is null)
                        pose = ToTrackerConvention(solver.Solve(marks, t));
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

                LandmarksProcessed?.Invoke(dense);
                FrameProcessed?.Invoke(frame, lm, pose);

                // Hold the whole pipeline to its share of the machine.
                //
                // The budget used to cover only the landmarker, which left the
                // detector running on every single frame with no limit at all —
                // and the detector turned out to be the expensive half. Sleeping
                // here bounds everything the loop does: capture conversion,
                // detection, landmarks, the solve.
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
                        Thread.Sleep(ms);
                    }
                }

                // One line, every five seconds, and every number in it is
                // counted rather than derived.
                //
                // The rate is the one that matters and the one that was being
                // guessed at: the view moves in visible steps below about six
                // poses a second, so this is the number to read first and the
                // only one that says whether the tracker is keeping up.
                var elapsed = Clock.Seconds - windowStart;
                if (elapsed >= 5.0 && windowFrames > 0)
                {
                    var rate = windowPoses / elapsed;
                    var cycle = elapsed * 1000 / windowFrames;
                    var detect = windowDetectMs / windowFrames;
                    var landmark = windowPoses > 0 ? windowLandmarkMs / windowPoses : 0;
                    var idle = windowIdleMs / windowFrames;
                    // The hit rate is here because it was the thing a whole
                    // round of analysis had to reconstruct by hand, by dividing
                    // the pose rate by the frame rate across fifteen log lines.
                    // It ranged from 36% to 100% in one session, and a third of
                    // frames finding no face is a completely different problem
                    // from a slow model — different cause, different fix, and
                    // invisible in every other number printed here.
                    var found = windowPoses * 100.0 / windowFrames;
                    var spread = gapMax > 0 && gapMin < double.MaxValue
                        ? $", gap {gapMin * 1000:F0}-{gapMax * 1000:F0} ms"
                        : "";
                    Console.WriteLine(
                        $"  [camera] {rate:F1} poses/s{spread} — {cycle:F0} ms per frame " +
                        $"(detect {detect:F0}, landmark {landmark:F0}, idle {idle:F0}), " +
                        $"face found on {found:F0}% of frames");

                    // Which of the three explanations it is, rather than one
                    // message that fits all of them badly. The first version of
                    // this said "that is slow enough to look like stutter" at a
                    // camera pointed at a wall, where the honest answer is that
                    // there was no face to find.
                    var waiting = cycle - detect - landmark - idle;
                    if (windowPoses == 0)
                        Console.WriteLine("  [camera] no poses at all — the face is not being found, " +
                                          "which is a lighting and framing problem, not a speed one");
                    // Runs of frames with no face are what produce the multi-
                    // second gaps that freeze the view, and no amount of speed
                    // fixes them. Worth its own line, above the speed advice,
                    // because it is the larger complaint when it happens.
                    else if (found < 80)
                        Console.WriteLine($"  [camera] the face is being lost on {100 - found:F0}% of frames — " +
                                          "try more even lighting, or source.camera.scoreThreshold lower " +
                                          "(0.5), or source.camera.detectWidth 0 to detect at full size");
                    else if (waiting > cycle * 0.5)
                        Console.WriteLine($"  [camera] {waiting:F0} ms of each frame is spent waiting for the " +
                                          "camera, so the camera is the limit here, not the tracking");
                    // Six is where the simulation of the real view mapper stops
                    // showing output frames with the picture frozen. Below it
                    // the complaint is always the same, and it is never
                    // described as slowness: the view jumps to the next angle
                    // and waits there. The glide stretches to cover it, so this
                    // is a note about cost, not a fault.
                    else if (rate < 6.0)
                        Console.WriteLine(
                            "  [camera] that is below the rate the view can hide; the glide is stretching " +
                            "to cover it. The biggest item above is the one to attack: " +
                            "source.camera.detectWidth 320 makes detection ~4x cheaper, " +
                            "source.camera.trackingCpuShare 1.0 removes the idle.");

                    windowStart = Clock.Seconds;
                    windowFrames = windowPoses = 0;
                    windowDetectMs = windowLandmarkMs = windowIdleMs = 0;
                    gapMin = double.MaxValue;
                    gapMax = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [camera] stopped: {ex.Message}");
            reason = ex.Message;
        }
        finally
        {
            landmarker?.Dispose();
            solver?.Dispose();
            detector?.Dispose();
            capture?.Dispose();
            Stopped?.Invoke(reason);
        }
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
