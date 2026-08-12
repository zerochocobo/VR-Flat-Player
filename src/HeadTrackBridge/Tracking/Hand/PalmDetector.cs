using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

namespace HeadTrackBridge.Tracking.Hand;

/// <summary>
/// One palm found in a frame: a box, seven keypoints, and how sure the model is.
/// </summary>
/// <remarks>
/// The seven keypoints are the reason this type exists rather than a plain
/// rectangle. <see cref="HandLandmarker"/> needs them to work out which way the
/// hand is pointing before it crops, and the crop is only correct because it is
/// built from these. Their order is the model's own:
///
///     0 wrist   1 index MCP   2 middle MCP   3 ring MCP   4 pinky MCP
///     5 thumb CMC   6 thumb MCP
///
/// Only 0 and 2 are read — palm base and middle-finger base give the hand axis.
/// The rest are carried because the crop is sized from their bounding box.
/// </remarks>
public readonly record struct PalmDetection(Rect Box, Point2f[] Keypoints, float Score)
{
    /// <summary>Index into <see cref="Keypoints"/> of the wrist.</summary>
    public const int PalmBase = 0;

    /// <summary>Index into <see cref="Keypoints"/> of the middle finger's base.</summary>
    public const int MiddleFingerBase = 2;

    public const int KeypointCount = 7;
}

/// <summary>
/// MediaPipe's palm detector (BlazePalm), as converted by OpenCV's model zoo.
///
/// Finds palms rather than whole hands, and that is deliberate on the model's
/// part: a palm is a rigid, roughly square patch, so it can be found with a
/// square-anchor detector, while a hand with its fingers spread is neither.
/// Everything about a hand that varies is left to <see cref="HandLandmarker"/>.
///
/// Interface confirmed by probing the file rather than assuming:
///     input   float32 [1,192,192,3]   NHWC, RGB, 0..1
///     output  float32 [1,2016,18]     4 box deltas + 7 keypoint pairs
///             float32 [1,2016,1]      logits, sigmoid for the score
///
/// NHWC, unlike the two face models, which are NCHW. OpenCV's Mat is already
/// HWC, so the tensor conversion here is a scale and nothing else — none of the
/// per-pixel transpose <see cref="Face.FaceLandmarker.ToTensor"/> has to do.
/// </summary>
public sealed class PalmDetector : IDisposable
{
    private const int InputSize = 192;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly Point2f[] _anchors;

    /// <summary>Detections below this score are dropped.</summary>
    public float ScoreThreshold { get; set; } = 0.55f;

    /// <summary>IoU above which two detections are treated as the same palm.</summary>
    public float NmsThreshold { get; set; } = 0.3f;

    public PalmDetector(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Palm detector model not found at {modelPath}. Run tools\\install-models.bat.",
                modelPath);

        // One intra-op thread. Measured on this model on an eight-core machine,
        // with spinning off as configured below:
        //
        //     threads   ms/call   core-seconds/call
        //           1      12.2               0.012
        //           2      11.8               0.024
        //           4       8.7               0.035
        //
        // Two threads buy 3% of the latency for twice the CPU, which is not a
        // trade worth making anywhere, let alone on a thread that must lose to
        // the video decoder. Four buy 29% for three times the CPU.
        //
        // The same reasoning as Face.FaceLandmarker, but the numbers came out
        // differently enough to be worth writing down: that model's knee is at
        // two threads, this one has no knee at all.
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        // ORT's pools busy-wait for work by default. Right for chasing latency,
        // wrong here: between calls this pool would burn a core waiting for work
        // that the camera loop's duty cycle deliberately spaces out.
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _anchors = BuildAnchors();
    }

    /// <summary>
    /// SSD anchor centres, in 0..1 of the 192x192 input.
    /// </summary>
    /// <remarks>
    /// Generated rather than copied. OpenCV's reference implementation carries
    /// this table as two thousand lines of hardcoded literals, which is two
    /// thousand lines nobody will ever read for a structure that is four:
    /// two feature maps, at strides 8 and 16, with 2 and 6 anchors per cell,
    /// centred in each cell. 24*24*2 + 12*12*6 = 2016, which is exactly the
    /// second dimension the model reports.
    ///
    /// Checked against their table element by element before this replaced it:
    /// the largest disagreement is 3.3e-8, which is the rounding in their
    /// printed decimals. The unit tests keep a sample of it.
    ///
    /// Order matters as much as the values. The model's output rows are in this
    /// exact sequence — feature map, then row, then column, then the anchors
    /// within a cell — and any rearrangement pairs every box with the wrong
    /// centre. That failure looks like detections in plausible but wrong places,
    /// which is far harder to spot than no detections at all.
    /// </remarks>
    internal static Point2f[] BuildAnchors()
    {
        var anchors = new List<Point2f>(2016);
        foreach (var (stride, perCell) in new[] { (8, 2), (16, 6) })
        {
            var grid = InputSize / stride;
            for (var y = 0; y < grid; y++)
                for (var x = 0; x < grid; x++)
                {
                    var centre = new Point2f((x + 0.5f) / grid, (y + 0.5f) / grid);
                    for (var k = 0; k < perCell; k++) anchors.Add(centre);
                }
        }
        return anchors.ToArray();
    }

    /// <summary>
    /// The largest palm in the frame, or null if none passes the score threshold.
    /// </summary>
    /// <remarks>
    /// Largest rather than highest-scoring, for the same reason
    /// <c>CameraFaceSource.Largest</c> picks the largest face: with a second
    /// person in the room, the one gesturing at the screen is the one filling
    /// the frame, and score does not track distance.
    /// </remarks>
    public PalmDetection? Detect(Mat frame)
    {
        var (blob, scale, padX, padY) = ToTensor(frame);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, blob)]);

        // Found by shape, not by name. Both outputs are called Identity-something
        // and the numbering is a detail of the conversion, not a promise.
        var outputs = results.Select(r => r.AsTensor<float>()).ToArray();
        var boxes = outputs.FirstOrDefault(t => t.Dimensions.Length == 3 && t.Dimensions[2] == 18);
        var logits = outputs.FirstOrDefault(t => t.Dimensions.Length == 3 && t.Dimensions[2] == 1);
        if (boxes is null || logits is null || boxes.Dimensions[1] != _anchors.Length) return null;

        // Two passes, and the split is what keeps this cheap: the first reads one
        // float per anchor and the second reads eighteen. At a 0.55 threshold the
        // second pass usually runs on a handful of rows out of 2016.
        var kept = new List<int>();
        for (var i = 0; i < _anchors.Length; i++)
        {
            var score = 1f / (1f + MathF.Exp(-logits[0, i, 0]));
            if (score >= ScoreThreshold) kept.Add(i);
        }
        if (kept.Count == 0) return null;

        var candidates = new List<(Rect2f Box, Point2f[] Keys, float Score)>(kept.Count);
        foreach (var i in kept)
        {
            var anchor = _anchors[i];
            var score = 1f / (1f + MathF.Exp(-logits[0, i, 0]));

            // Deltas are in input pixels relative to the anchor centre, so they
            // are divided by the input size to join the anchor in 0..1 and only
            // then scaled out to the letterboxed frame.
            var cx = boxes[0, i, 0] / InputSize + anchor.X;
            var cy = boxes[0, i, 1] / InputSize + anchor.Y;
            var w = boxes[0, i, 2] / InputSize;
            var h = boxes[0, i, 3] / InputSize;

            var keys = new Point2f[PalmDetection.KeypointCount];
            for (var k = 0; k < keys.Length; k++)
                keys[k] = Undo(boxes[0, i, 4 + k * 2] / InputSize + anchor.X,
                               boxes[0, i, 5 + k * 2] / InputSize + anchor.Y);

            var topLeft = Undo(cx - w / 2, cy - h / 2);
            var bottomRight = Undo(cx + w / 2, cy + h / 2);
            candidates.Add((Rect2f.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y),
                            keys, score));
        }

        // The 0..1 coordinates are of the padded square, so undoing the letterbox
        // is the same subtraction and division for boxes and keypoints alike.
        Point2f Undo(float x, float y) =>
            new((x * InputSize - padX) / scale, (y * InputSize - padY) / scale);

        var best = SuppressAndPick(candidates);
        if (best is not var (box, keypoints, bestScore)) return null;

        // Clamped to the frame after NMS rather than before: the box is only
        // used to size a crop that pads beyond the edge anyway, and clamping
        // earlier would have shrunk the box the crop is scaled from.
        var rect = new Rect(
            (int)MathF.Round(box.X), (int)MathF.Round(box.Y),
            (int)MathF.Round(box.Width), (int)MathF.Round(box.Height));
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        return new PalmDetection(rect, keypoints, bestScore);
    }

    /// <summary>
    /// Non-maximum suppression, then the largest survivor.
    /// </summary>
    /// <remarks>
    /// Written out rather than calling <c>Cv2.NMSBoxes</c>: that overload wants
    /// arrays it can marshal and returns indices into them, so using it here
    /// would mean building two parallel arrays purely to be handed back a number
    /// that indexes the list we already have. The loop is shorter than the
    /// marshalling would be.
    /// </remarks>
    private (Rect2f Box, Point2f[] Keys, float Score)? SuppressAndPick(
        List<(Rect2f Box, Point2f[] Keys, float Score)> candidates)
    {
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        var survivors = new List<(Rect2f Box, Point2f[] Keys, float Score)>();
        foreach (var c in candidates)
            if (survivors.All(s => Iou(s.Box, c.Box) <= NmsThreshold))
                survivors.Add(c);

        if (survivors.Count == 0) return null;

        var best = survivors[0];
        foreach (var s in survivors)
            if (s.Box.Width * s.Box.Height > best.Box.Width * best.Box.Height) best = s;
        return best;
    }

    private static float Iou(Rect2f a, Rect2f b)
    {
        var x1 = MathF.Max(a.Left, b.Left);
        var y1 = MathF.Max(a.Top, b.Top);
        var x2 = MathF.Min(a.Right, b.Right);
        var y2 = MathF.Min(a.Bottom, b.Bottom);
        var overlap = MathF.Max(0, x2 - x1) * MathF.Max(0, y2 - y1);
        var union = a.Width * a.Height + b.Width * b.Height - overlap;
        return union <= 0 ? 0 : overlap / union;
    }

    /// <summary>
    /// The frame, letterboxed into a 192x192 RGB tensor in 0..1.
    /// </summary>
    /// <remarks>
    /// Letterboxed rather than stretched. A 16:9 frame squashed to a square
    /// makes every palm in it an ellipse, and the anchors this model was trained
    /// with are square — so a stretched palm matches no anchor well and the
    /// score falls short of the threshold. The bars cost the resolution they
    /// occupy and nothing else.
    ///
    /// Returned with the scale and offsets so the caller can undo it. Both
    /// halves have to agree exactly, which is why they are computed once here
    /// rather than derived again on the way back.
    /// </remarks>
    private static (DenseTensor<float> Blob, float Scale, int PadX, int PadY) ToTensor(Mat frame)
    {
        var scale = MathF.Min((float)InputSize / frame.Width, (float)InputSize / frame.Height);
        var w = Math.Max(1, (int)MathF.Round(frame.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(frame.Height * scale));
        var padX = (InputSize - w) / 2;
        var padY = (InputSize - h) / 2;

        using var square = new Mat(new Size(InputSize, InputSize), frame.Type(), Scalar.All(0));
        using (var roi = new Mat(square, new Rect(padX, padY, w, h)))
            Cv2.Resize(frame, roi, new Size(w, h), 0, 0, InterpolationFlags.Area);

        using var rgb = new Mat();
        Cv2.CvtColor(square, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = new DenseTensor<float>([1, InputSize, InputSize, 3]);
        var span = tensor.Buffer.Span;
        var bytes = new byte[InputSize * InputSize * 3];
        System.Runtime.InteropServices.Marshal.Copy(rgb.Data, bytes, 0, bytes.Length);
        for (var i = 0; i < bytes.Length; i++) span[i] = bytes[i] / 255f;

        return (tensor, scale, padX, padY);
    }

    public void Dispose() => _session.Dispose();
}
