using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

namespace HeadTrackBridge.Tracking.Face;

/// <summary>
/// 68 facial landmarks from a face crop.
///
/// Replaces YuNet's five points for pose. Five was never enough for pitch —
/// the nose tip was the only out-of-plane point and the worst localised of
/// them, so pitch rode on the noisiest thing in the set and arrived in visible
/// steps. 68 points bring the chin and jaw, which give pitch a lever arm
/// comparable to the one the eyes already give yaw.
///
/// Not named for its model. It ran 2DFAN4 first, which was accurate and far
/// too slow — 310 ms a call here and 438 ms on the machine that complained,
/// so about 3 fps while competing with video decode. peppa_wutz does the same
/// job in 28.6 ms from 13 MB instead of 93, and the two are interchangeable
/// here: identical input and output shapes, so only the file changed.
///
/// Interface confirmed by probing the file rather than assuming:
///     input   float32 [1,3,256,256]   NCHW
///     output  float32 [1,68,3]        x, y, score
///
/// Coordinates arrive already decoded and subpixel — fractional, in 0..64 —
/// so there is no argmax or refinement to do here. That fractional precision
/// is what the five-point path lacked when pitch came out in steps.
/// </summary>
public sealed class FaceLandmarker : IDisposable
{
    private const int InputSize = 256;
    private const int HeatmapSize = 64;

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public FaceLandmarker(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Landmarker model not found at {modelPath}. Run tools\\install-models.bat.",
                modelPath);

        // Two intra-op threads, down from four. Measured on this model, on an
        // eight-core machine, with spinning off as configured below — the
        // earlier table here was taken before that and read high:
        //
        //     threads   ms/call   core-seconds/call
        //           1      44.6               0.045
        //           2      26.6               0.052
        //           4      22.8               0.073
        //
        // Two is the knee, twice over: it buys 40% of the latency off one
        // thread for 16% more CPU, and four buys only 14% more for another 40%.
        // Stable across repeated passes, which the first attempt at this table
        // was not.
        //
        // It matters because these are ORT's own threads, created once with
        // this session and kept for its lifetime. They inherit the priority of
        // whichever thread built the session — the camera thread, BelowNormal —
        // so the session must be constructed there and nowhere else. Four of
        // them were enough to make the whole player unresponsive on a laptop
        // playing 4K60 the moment tracking was switched on.
        //
        // Note what this is *not* the cause of. When the player was reported as
        // very choppy, the suspicion fell here and the measurement cleared it:
        // 27 ms was never the problem next to a face detector costing 118 ms a
        // frame on the same loop. See CameraFaceSource.DetectWidth.
        var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        // Do not let the thread pool spin.
        //
        // ORT's pools busy-wait for work by default, which is right when you are
        // chasing latency and wrong for a background task: between calls the
        // pool would burn whole cores waiting for work that is deliberately
        // spaced out by the duty-cycle budget in CameraFaceSource.
        //
        // Added while chasing a hang that turned out to be an access violation
        // instead, so this fixes nothing about that. It is kept on its own
        // merits — a tracker that must lose to the video decoder has no business
        // spinning.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// 68 landmarks in frame pixel coordinates, or null if the crop is unusable.
    ///
    /// <paramref name="box"/> is the detector's face rectangle. The crop is a
    /// square around it rather than the box itself: the network was trained on
    /// square crops with margin, and feeding it a tight or non-square box moves
    /// every landmark slightly, which is exactly the kind of error that yields
    /// a plausible but wrong pose.
    /// </summary>
    public Point2f[]? Locate(Mat frame, Rect box)
    {
        var side = (int)(Math.Max(box.Width, box.Height) * 1.6);
        if (side < 24) return null;

        var cx = box.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;
        var x0 = cx - side / 2;
        var y0 = cy - side / 2;

        // Let the crop hang off the edge of the frame and pad, rather than
        // clamping it inward: clamping would change the scale and centre, and
        // the landmarks would come back subtly displaced with nothing to
        // indicate it.
        var src = new Rect(x0, y0, side, side);
        var clipped = src & new Rect(0, 0, frame.Width, frame.Height);
        if (clipped.Width <= 0 || clipped.Height <= 0) return null;

        using var square = new Mat(new Size(side, side), frame.Type(), Scalar.All(0));
        using (var srcRoi = new Mat(frame, clipped))
        using (var dstRoi = new Mat(square, new Rect(clipped.X - x0, clipped.Y - y0,
                                                     clipped.Width, clipped.Height)))
            srcRoi.CopyTo(dstRoi);

        using var resized = new Mat();
        Cv2.Resize(square, resized, new Size(InputSize, InputSize), 0, 0, InterpolationFlags.Linear);

        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = ToTensor(rgb);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

        // Found by shape, not by name. 2DFAN4 calls this output "landmarks" and
        // peppa_wutz calls it "output", and hardcoding either makes the class
        // silently model-specific — it threw on the swap. [1,68,3] identifies
        // it unambiguously; 2DFAN4's other output is [1,68,64,64] heatmaps.
        var landmarks = results
            .Select(r => r.AsTensor<float>())
            .FirstOrDefault(t => t.Dimensions.Length == 3 && t.Dimensions[1] == 68 && t.Dimensions[2] >= 2);

        if (landmarks is null) return null;

        // 0..64 -> 0..256 -> back through the crop to frame pixels.
        var scale = (float)side / InputSize;
        var points = new Point2f[68];
        for (var i = 0; i < 68; i++)
        {
            var lx = landmarks[0, i, 0] / HeatmapSize * InputSize;
            var ly = landmarks[0, i, 1] / HeatmapSize * InputSize;
            points[i] = new Point2f(x0 + lx * scale, y0 + ly * scale);
        }

        Smooth(points);
        return points;
    }

    private Point2f[]? _previous;

    /// <summary>Landmark smoothing strength, 0 = off. See <see cref="Smooth"/>.</summary>
    public double Smoothing { get; set; } = 0.5;

    /// <summary>
    /// Smooth the landmarks before they reach the solver.
    /// </summary>
    /// <remarks>
    /// Filtering the angles afterwards, which is what the view mapper does,
    /// cannot undo this noise properly — PnP is non-linear, so a wobbling point
    /// set does not produce a wobbling angle that averages back to the truth.
    /// Steadying the input is strictly better than steadying the output, and
    /// this is the earliest place it can be done.
    ///
    /// Deliberately mild and per-point. A landmark moves a pixel or two between
    /// frames on a still face, and a pixel is about a degree of pose; half a
    /// frame of memory removes most of that while staying far below the lag a
    /// head turn would notice at 30 fps.
    /// </remarks>
    private void Smooth(Point2f[] points)
    {
        var a = 1 - Math.Clamp(Smoothing, 0, 0.95);
        if (a >= 1) { _previous = (Point2f[])points.Clone(); return; }

        if (_previous is { Length: 68 } prev)
        {
            for (var i = 0; i < 68; i++)
            {
                // Reset rather than blend when the face has clearly moved or a
                // different face was picked up: easing across a jump would drag
                // the landmarks through empty space for several frames.
                var dx = points[i].X - prev[i].X;
                var dy = points[i].Y - prev[i].Y;
                if (dx * dx + dy * dy > 900) continue;

                points[i] = new Point2f(
                    (float)(prev[i].X + dx * a),
                    (float)(prev[i].Y + dy * a));
            }
        }

        _previous = (Point2f[])points.Clone();
    }

    /// <summary>
    /// HWC bytes to NCHW floats in 0..1.
    ///
    /// Two conversions in one pass, and both matter: OpenCV stores pixels
    /// interleaved while the network wants planes, and it stores them BGR while
    /// the caller has already converted to RGB.
    /// </summary>
    private static DenseTensor<float> ToTensor(Mat rgb)
    {
        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        var data = new byte[InputSize * InputSize * 3];
        System.Runtime.InteropServices.Marshal.Copy(rgb.Data, data, 0, data.Length);

        for (var y = 0; y < InputSize; y++)
            for (var x = 0; x < InputSize; x++)
            {
                var o = (y * InputSize + x) * 3;
                tensor[0, 0, y, x] = data[o] / 255f;
                tensor[0, 1, y, x] = data[o + 1] / 255f;
                tensor[0, 2, y, x] = data[o + 2] / 255f;
            }
        return tensor;
    }

    public void Dispose() => _session.Dispose();
}
