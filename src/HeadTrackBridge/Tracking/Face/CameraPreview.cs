using HeadTrackBridge.Tracking.Face;
using OpenCvSharp;

// The project enables WinForms, so System.Drawing is in the implicit usings and
// collides with OpenCvSharp on Point and Size. Alias rather than fully qualify:
// this file draws, so the names appear on nearly every line.
using Point = OpenCvSharp.Point;

namespace HeadTrackBridge.Tracking;

/// <summary>
/// A window showing what the tracker sees, for answering "why is this not
/// working" without guessing.
///
/// Numbers alone cannot separate the three things that go wrong — the camera
/// not opening, the face not being found, and the pose being solved wrongly.
/// Drawing the landmarks and an axis cross distinguishes all three at a glance:
/// no window means the camera failed, no dots means detection failed, and dots
/// on the right features with an axis pointing the wrong way means the pose or
/// the landmark ordering is wrong.
/// </summary>
public static class CameraPreview
{
    private const string Title = "VR Flat Player — camera diagnostic";

    /// <summary>
    /// Runs the OpenCV window loop until Esc or the cancellation token fires.
    /// Must be called on the thread that owns the console: HighGui pumps its
    /// own message loop and does not like being driven from a pool thread.
    /// </summary>
    /// <param name="mirrored">
    /// Whether the frames are mirrored, which decides what the eye labels say.
    /// In a mirrored view the point on the image's left is the viewer's own
    /// left eye, the way a bathroom mirror behaves; unmirrored it is their
    /// right. Labelling it without knowing which is how the first version got
    /// them backwards.
    /// </param>
    public static void Run(CameraFaceSource source, bool mirrored, CancellationToken ct)
    {
        var gate = new object();
        Mat? latest = null;
        Point2f[]? dense = null;

        // Kept beside the frame rather than passed with it: the two events fire
        // separately, and the landmarks are what tell you whether the crop fed
        // to the network was right. A wrong crop still returns 68 plausible
        // points — they just sit slightly off the features, which is visible
        // here and nowhere else.
        source.LandmarksProcessed += pts => Volatile.Write(ref dense, pts);
        var ended = false;
        var frames = 0;

        source.Stopped += why =>
        {
            Console.WriteLine($"  [camera] capture ended: {why}");
            Volatile.Write(ref ended, true);
        };

        source.FrameProcessed += (frame, lm, pose) =>
        {
            // Cloned under a lock: the capture thread reuses one Mat, so drawing
            // straight onto it would race with the next Read into the same buffer.
            var copy = frame.Clone();
            Annotate(copy, lm, pose, mirrored);
            DrawDense(copy, Volatile.Read(ref dense));
            lock (gate)
            {
                latest?.Dispose();
                latest = copy;
            }
        };

        Console.WriteLine();
        Console.WriteLine("Camera diagnostic. Esc or Ctrl+C to stop.");
        Console.WriteLine(mirrored
            ? "  view        mirrored, so it moves the way a mirror does"
            : "  view        NOT mirrored (source.camera.mirror is off)");
        Console.WriteLine("  green dots  the five landmarks: eyes, nose, mouth corners");
        Console.WriteLine("  L / R       should sit on YOUR left and right eye");
        Console.WriteLine("  blue line   where the head is pointing");
        Console.WriteLine("  no dots     no face found — check lighting and framing");
        Console.WriteLine();

        // A camera that never opens, or opens and never delivers, must not leave
        // this sitting at a blank window: an unresponsive diagnostic tells the
        // user nothing about the thing they are diagnosing.
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!ct.IsCancellationRequested)
        {
            if (Volatile.Read(ref ended)) break;

            // Before ImShow, not after. ImShow recreates a closed window, so
            // checking afterwards inspects the one just brought back and finds
            // it perfectly visible — the window reappeared two or three times
            // before a race happened to catch it. Guarded on having shown
            // something, since the window does not exist on the first pass.
            if (frames > 0 && WasClosed()) break;

            Mat? show = null;
            lock (gate)
            {
                if (latest is not null) { show = latest; latest = null; }
            }

            if (show is null)
            {
                if (frames == 0 && DateTime.UtcNow > deadline)
                {
                    Console.Error.WriteLine(
                        "  [camera] no frames after 10s. The device opened but is not producing " +
                        "images — try another --camera=N, or close whatever else is using it.");
                    break;
                }

                // No new frame yet. WaitKey still has to run so the window
                // stays responsive rather than showing as "not responding".
                if (Cv2.WaitKey(30) == 27) break;
                continue;
            }

            frames++;

            Cv2.ImShow(Title, show);
            show.Dispose();
            if (Cv2.WaitKey(1) == 27) break;
        }

        lock (gate) { latest?.Dispose(); latest = null; }
        Cv2.DestroyAllWindows();
    }

    /// <summary>
    /// The 68 landmarks, with the six that actually drive the pose picked out.
    ///
    /// Highlighting those six is the point: they are the ones a wrong 3D model
    /// or a wrong crop would misplace, and chin and jaw are what pitch now
    /// depends on. If the yellow markers do not sit on the nose tip, chin, outer
    /// eye corners and mouth corners, the pose is wrong no matter how neat the
    /// small dots look.
    /// </summary>
    private static void DrawDense(Mat frame, Point2f[]? pts)
    {
        if (pts is null) return;

        foreach (var p in pts)
            Cv2.Circle(frame, new Point((int)p.X, (int)p.Y), 1, new Scalar(255, 200, 80), -1);

        foreach (var i in FaceGeometry.PoseIndices)
        {
            if (i >= pts.Length) continue;
            Cv2.Circle(frame, new Point((int)pts[i].X, (int)pts[i].Y), 4, new Scalar(0, 230, 255), -1);
        }
    }

    /// <summary>
    /// Did the user close the window with its X button?
    ///
    /// HighGui has no close event, and ImShow silently *recreates* a window
    /// that is gone — so a loop that keeps showing frames resurrects it on
    /// every iteration and the X button appears to do nothing. Asking whether
    /// the window is still visible is the only way to notice.
    /// </summary>
    private static bool WasClosed()
    {
        try
        {
            return Cv2.GetWindowProperty(Title, WindowPropertyFlags.Visible) < 1;
        }
        catch (OpenCVException)
        {
            // Thrown when the window does not exist at all, which is the same
            // answer for our purposes.
            return true;
        }
    }

    private static void Annotate(Mat frame, FaceLandmarks? lm, HeadPose? pose, bool mirrored)
    {
        var green = new Scalar(0, 255, 0);
        var blue = new Scalar(255, 160, 0);
        var red = new Scalar(0, 0, 255);

        if (lm is not { } m)
        {
            Cv2.PutText(frame, "no face", new Point(12, 30),
                        HersheyFonts.HersheySimplex, 0.8, red, 2);
            return;
        }

        // Which of the viewer's eyes is on the image's left depends entirely on
        // whether the frame is mirrored. Deriving the label from that rather
        // than from the landmark's name is the fix for having shown them
        // swapped: the names come from YuNet's anatomical convention, and the
        // frame handed to it has already been flipped.
        var onImageLeft = mirrored ? "L" : "R";
        var onImageRight = mirrored ? "R" : "L";

        var points = new[]
        {
            (m.ImageLeftEyeX, m.ImageLeftEyeY, onImageLeft),
            (m.ImageRightEyeX, m.ImageRightEyeY, onImageRight),
            (m.NoseX, m.NoseY, "nose"),
            (m.ImageLeftMouthX, m.ImageLeftMouthY, ""),
            (m.ImageRightMouthX, m.ImageRightMouthY, ""),
        };
        foreach (var (x, y, label) in points)
        {
            Cv2.Circle(frame, new Point((int)x, (int)y), 3, green, -1);
            if (label.Length > 0)
                Cv2.PutText(frame, label, new Point((int)x + 6, (int)y - 6),
                            HersheyFonts.HersheySimplex, 0.4, green, 1);
        }

        if (pose is not { } p)
        {
            Cv2.PutText(frame, "face found, pose failed", new Point(12, 30),
                        HersheyFonts.HersheySimplex, 0.7, red, 2);
            return;
        }

        // Gaze ray from the nose. Small-angle projection is enough to show
        // direction, which is all this needs to prove.
        //
        // Signs follow the pose *after* CameraFaceSource has converted it, not
        // the solver's raw output — worth stating, because the two differ on
        // both axes and reasoning from the wrong one flips the arrow.
        //
        //   yaw   positive means the head turned to the viewer's right, which
        //         in the mirrored picture is the picture's right, so X adds.
        //   pitch positive means looking up, so Y subtracts: +Y is down.
        var len = frame.Width * 0.25;
        var tipX = m.NoseX + len * Math.Sin(p.Yaw * Math.PI / 180);
        var tipY = m.NoseY - len * Math.Sin(p.Pitch * Math.PI / 180);
        Cv2.ArrowedLine(frame, new Point((int)m.NoseX, (int)m.NoseY),
                        new Point((int)tipX, (int)tipY), blue, 2);

        var lines = new[]
        {
            $"yaw   {p.Yaw,7:F1}",
            $"pitch {p.Pitch,7:F1}",
            $"roll  {p.Roll,7:F1}",
            $"dist  {p.Z,7:F1} cm",
        };
        for (var i = 0; i < lines.Length; i++)
            Cv2.PutText(frame, lines[i], new Point(12, 28 + i * 24),
                        HersheyFonts.HersheySimplex, 0.6, green, 2);
    }
}
