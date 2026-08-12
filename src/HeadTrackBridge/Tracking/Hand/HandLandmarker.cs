using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

using Size = OpenCvSharp.Size;

namespace HeadTrackBridge.Tracking.Hand;

/// <summary>
/// One hand: 21 landmarks in frame pixels, and how sure the model is.
/// </summary>
/// <remarks>
/// The order is MediaPipe's, and every index used anywhere in this project is
/// named in <see cref="HandPose"/> rather than written as a number at the call
/// site:
///
///     0            wrist
///     1  2  3  4   thumb   CMC, MCP, IP, TIP
///     5  6  7  8   index   MCP, PIP, DIP, TIP
///     9 10 11 12   middle  MCP, PIP, DIP, TIP
///    13 14 15 16   ring    MCP, PIP, DIP, TIP
///    17 18 19 20   pinky   MCP, PIP, DIP, TIP
/// </remarks>
public readonly record struct HandLandmarks(Point2f[] Points, float Score, bool RightHand)
{
    public const int PointCount = 21;
}

/// <summary>
/// MediaPipe's hand landmark model, as converted by OpenCV's model zoo.
///
/// Interface confirmed by probing the file rather than assuming:
///     input   float32 [1,224,224,3]   NHWC, RGB, 0..1
///     output  float32 [1,63]          21 x (x, y, z) in input pixels
///             float32 [1,1]           presence score
///             float32 [1,1]           handedness, 0 left .. 1 right
///             float32 [1,63]          world coordinates, unused here
///
/// Costs 5.6 ms a call here at one thread, which makes it the cheap half of the
/// pipeline — and the half that runs on every gesture frame, because the palm
/// detector is skipped for as long as a hand is being tracked. See
/// <see cref="PalmFrom"/> for how that works.
/// </summary>
public sealed class HandLandmarker : IDisposable
{
    private const int InputSize = 224;

    /// <summary>
    /// Which of the 21 landmarks correspond to the palm detector's seven.
    /// </summary>
    /// <remarks>
    /// This is the whole mechanism behind tracking without re-detecting, and it
    /// is the model's own mapping rather than a guess: wrist, the four finger
    /// bases, and two points along the thumb. Feeding these back through the
    /// same crop construction the detector's output goes through means a tracked
    /// frame and a detected frame are cropped identically.
    ///
    /// Doing it any other way is a trap this project has already fallen into on
    /// the face side. The crop is scaled from the *detector's* box, so building
    /// it from some other bounding box — the 21 landmarks' own extent, say —
    /// changes the scale and centre, and every landmark comes back displaced
    /// with nothing to indicate it.
    /// </remarks>
    private static readonly int[] PalmLandmarkIds = [0, 5, 9, 13, 17, 1, 2];

    /// <summary>
    /// How much larger than the palm keypoints' own extent the crop is, and how
    /// far up from their centre it sits.
    ///
    /// MediaPipe's numbers, not ours. The model was trained on crops framed this
    /// way, and the fingers — which are entirely outside the seven palm points —
    /// are only in the picture because of the 3x.
    /// </summary>
    private const float CropEnlarge = 3.0f;
    private const float CropShiftY = -0.4f;

    private readonly InferenceSession _session;
    private readonly string _inputName;

    /// <summary>Results below this score are discarded as "no hand here".</summary>
    public float ScoreThreshold { get; set; } = 0.75f;

    public HandLandmarker(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Hand landmark model not found at {modelPath}. Run tools\\install-models.bat.",
                modelPath);

        // One intra-op thread. Measured on this model on an eight-core machine,
        // with spinning off:
        //
        //     threads   ms/call   core-seconds/call
        //           1       5.6               0.006
        //           2       3.7               0.007
        //           4       3.6               0.015
        //
        // Two threads are 1.9 ms faster. That is real but it is not worth a
        // second core on a machine whose complaint has always been that tracking
        // competes with video decode — and unlike the face landmarker, whose 27
        // ms sits on the critical path of the view following your head, nothing
        // here is watched frame by frame. A gesture has to be held for about
        // half a second before it fires.
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// 21 landmarks in frame pixels, or null when the crop holds no hand.
    /// </summary>
    public HandLandmarks? Locate(Mat frame, Point2f[] palmKeypoints)
    {
        using var toInput = CropTransform(palmKeypoints);
        if (toInput is null) return null;

        using var crop = new Mat();
        // Padded with black where the crop hangs off the edge of the frame,
        // never clamped inward. Clamping would change the scale and the centre
        // together, and the landmarks would come back subtly displaced with
        // nothing to say so — the same reason Face.FaceLandmarker.Locate pads.
        Cv2.WarpAffine(frame, crop, toInput, new Size(InputSize, InputSize),
                       InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        using var rgb = new Mat();
        Cv2.CvtColor(crop, rgb, ColorConversionCodes.BGR2RGB);

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, ToTensor(rgb))]);
        var outputs = results.Select(r => r.AsTensor<float>()).ToArray();

        // By shape, not by name: the four outputs are called Identity, Identity_1
        // and so on, and which is which is a detail of the conversion. Two of
        // them are [1,63] and two are [1,1], so shape alone is not enough for
        // the pairs — order within each shape is. Screen landmarks come before
        // world landmarks, score before handedness.
        var vectors = outputs.Where(t => t.Length == 63).ToArray();
        var scalars = outputs.Where(t => t.Length == 1).ToArray();
        if (vectors.Length < 1 || scalars.Length < 2) return null;

        var score = scalars[0][0];
        if (score < ScoreThreshold) return null;

        var landmarks = vectors[0];
        var points = new Point2f[HandLandmarks.PointCount];

        using var fromInput = new Mat();
        Cv2.InvertAffineTransform(toInput, fromInput);
        // A local function, because Mat.At returns by reference and so cannot be
        // turned into a Func directly.
        double M(int row, int col) => fromInput.At<double>(row, col);

        for (var i = 0; i < points.Length; i++)
        {
            var x = landmarks[0, i * 3];
            var y = landmarks[0, i * 3 + 1];
            points[i] = new Point2f(
                (float)(M(0, 0) * x + M(0, 1) * y + M(0, 2)),
                (float)(M(1, 0) * x + M(1, 1) * y + M(1, 2)));
        }

        return new HandLandmarks(points, score, scalars[1][0] >= 0.5f);
    }

    /// <summary>
    /// The seven palm keypoints a detection would have produced, taken from a
    /// previous frame's 21 landmarks.
    /// </summary>
    /// <remarks>
    /// This is what lets the palm detector be skipped: it costs 12.2 ms against
    /// the landmark model's 5.6, so on a frame where the hand is already known,
    /// running it again is two thirds of the work for a box we can rebuild
    /// exactly. See <see cref="PalmLandmarkIds"/> for why "exactly" matters.
    /// </remarks>
    public static Point2f[] PalmFrom(HandLandmarks hand)
    {
        var keys = new Point2f[PalmDetection.KeypointCount];
        for (var i = 0; i < keys.Length; i++) keys[i] = hand.Points[PalmLandmarkIds[i]];
        return keys;
    }

    /// <summary>
    /// The affine that maps the frame onto the model's 224x224 input: rotate the
    /// hand upright, then scale its crop square to fill the input.
    /// </summary>
    /// <remarks>
    /// One matrix, where the reference implementation crops a 4x box, rotates
    /// it, re-derives a box from the rotated keypoints, crops that at 3x and
    /// pads the result square. Every one of those steps is a copy of the pixels,
    /// and the inverse mapping afterwards has to be assembled by hand from the
    /// rotation's parts. Composing the same geometry into a single transform
    /// costs one warp, and the way back is <c>InvertAffineTransform</c>.
    ///
    /// The rotation centre is arbitrary and this relies on it. Changing it moves
    /// the rotated keypoints by a constant, which moves the square they define
    /// by the same constant, so it cancels on the way back — which is why the
    /// palm's bounding box is not needed here at all, only its keypoints.
    /// </remarks>
    internal static Mat? CropTransform(Point2f[] palmKeypoints)
    {
        if (palmKeypoints.Length < PalmDetection.KeypointCount) return null;

        var wrist = palmKeypoints[PalmDetection.PalmBase];
        var middle = palmKeypoints[PalmDetection.MiddleFingerBase];

        // Degrees to turn the frame by so that wrist-to-middle-knuckle points
        // straight up. The y negation is because image y grows downward while
        // atan2 expects it to grow upward, and the quarter turn is because the
        // target direction is up rather than along +x.
        var angle = 90.0 - Math.Atan2(-(middle.Y - wrist.Y), middle.X - wrist.X) * 180.0 / Math.PI;

        var centre = new Point2f(
            palmKeypoints.Average(p => p.X), palmKeypoints.Average(p => p.Y));

        using var rotate = Cv2.GetRotationMatrix2D(centre, angle, 1.0);
        double R(int row, int col) => rotate.At<double>(row, col);

        // The keypoints' extent once the hand is upright. Measured after the
        // rotation rather than before, because an axis-aligned box around a
        // tilted hand is larger than the hand and would scale the crop wrong.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in palmKeypoints)
        {
            var x = R(0, 0) * p.X + R(0, 1) * p.Y + R(0, 2);
            var y = R(1, 0) * p.X + R(1, 1) * p.Y + R(1, 2);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        var width = maxX - minX;
        var height = maxY - minY;
        var side = CropEnlarge * Math.Max(width, height);
        if (side < 24) return null;   // too small to hold anything the model can read

        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2 + CropShiftY * height;

        // Scale about the crop centre and move it to the middle of the input.
        var scale = InputSize / side;
        var m = new Mat(2, 3, MatType.CV_64FC1);
        m.Set(0, 0, R(0, 0) * scale);
        m.Set(0, 1, R(0, 1) * scale);
        m.Set(0, 2, (R(0, 2) - cx) * scale + InputSize / 2.0);
        m.Set(1, 0, R(1, 0) * scale);
        m.Set(1, 1, R(1, 1) * scale);
        m.Set(1, 2, (R(1, 2) - cy) * scale + InputSize / 2.0);
        return m;
    }

    /// <summary>
    /// HWC bytes to an NHWC tensor in 0..1.
    ///
    /// A straight scale, with no transpose: this model takes NHWC and OpenCV
    /// already stores pixels that way. The face landmarker's equivalent has to
    /// walk the image pixel by pixel to build planes, and does not.
    /// </summary>
    private static DenseTensor<float> ToTensor(Mat rgb)
    {
        var tensor = new DenseTensor<float>([1, InputSize, InputSize, 3]);
        var span = tensor.Buffer.Span;
        var bytes = new byte[InputSize * InputSize * 3];
        System.Runtime.InteropServices.Marshal.Copy(rgb.Data, bytes, 0, bytes.Length);
        for (var i = 0; i < bytes.Length; i++) span[i] = bytes[i] / 255f;
        return tensor;
    }

    public void Dispose() => _session.Dispose();
}
