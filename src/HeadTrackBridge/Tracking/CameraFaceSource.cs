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
        _thread = new Thread(() => Run(ct)) { IsBackground = true, Name = "camera-face" };
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

            detector = FaceDetectorYN.Create(_modelPath, "", new Size(w, h),
                                             (float)_cfg.ScoreThreshold, 0.3f, 5000);
            solver = new HeadPoseSolver(w, h);

            // Optional: a missing landmarker degrades pitch, it does not stop
            // the player, so this must not be fatal.
            var landmarkPath = AppPaths.Resolve(_cfg.LandmarkModelPath);
            try
            {
                landmarker = new FaceLandmarker(landmarkPath) { Smoothing = _cfg.LandmarkSmoothing };
                Console.WriteLine("  [camera] 68-point landmarker loaded");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [camera] no 68-point landmarker ({ex.Message.Split('\n')[0]})");
                Console.WriteLine("  [camera] falling back to 5 points — up/down will be poor");
            }

            using var frame = new Mat();
            using var faces = new Mat();
            var lostFrames = 0;
            var warnedNoFace = false;
            var lastLandmarkAt = double.NegativeInfinity;
            var landmarkMs = 0.0;
            var landmarkRuns = 0;

            while (!_stop && !ct.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(10);
                    continue;
                }

                // The camera sees a mirror image of what the viewer does. Without
                // this, turning your head left moves the view right.
                if (_cfg.Mirror) Cv2.Flip(frame, frame, FlipMode.Y);

                detector.Detect(frame, faces);

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
                    var dueForLandmarks = _cfg.LandmarkFps <= 0 ||
                                          t - lastLandmarkAt >= 1.0 / _cfg.LandmarkFps;

                    if (landmarker is not null && dueForLandmarks && BoxOf(faces) is { } box)
                    {
                        lastLandmarkAt = t;

                        // Timed and reported once: "the landmarker is slow on
                        // this machine" is otherwise indistinguishable from any
                        // other cause of stutter, and the fix differs.
                        var started = Clock.Seconds;
                        dense = landmarker.Locate(frame, box);
                        landmarkMs += (Clock.Seconds - started) * 1000;

                        if (++landmarkRuns == 30)
                        {
                            var avg = landmarkMs / landmarkRuns;
                            Console.WriteLine($"  [camera] landmarker {avg:F0} ms/frame at {_cfg.LandmarkFps:F0} fps" +
                                              $" (~{avg * _cfg.LandmarkFps / 10:F0}% of one core)");
                            if (avg > 60)
                                Console.WriteLine("  [camera] that is slow — lower source.camera.landmarkFps, " +
                                                  "or set landmarkModelPath empty to fall back to 5 points");
                        }

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
