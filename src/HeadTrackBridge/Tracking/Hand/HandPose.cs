using OpenCvSharp;

namespace HeadTrackBridge.Tracking.Hand;

/// <summary>The shapes the hand can be in. Anything else is <see cref="None"/>.</summary>
/// <remarks>
/// Four, and the number is the point. Every pose here has to be told apart from
/// every other one by a model that only ever sees a hand for a fraction of a
/// second, in whatever light the room happens to have — so the set is chosen to
/// be separable first and expressive second. All four differ in how many fingers
/// are out, which is the one thing 21 landmarks measure robustly.
/// </remarks>
public enum Pose
{
    None,

    /// <summary>All four fingers out. The wake pose, and the one that swipes.</summary>
    OpenPalm,

    /// <summary>Nothing out, thumb included.</summary>
    Fist,

    /// <summary>Index out, the rest curled.</summary>
    Point,

    /// <summary>Thumb out, all four fingers curled.</summary>
    Thumb,
}

/// <summary>Which way a pointing pose points. Screen directions, already un-mirrored.</summary>
public enum Direction { None, Left, Right, Up, Down }

/// <summary>
/// What the hand is doing this frame.
/// </summary>
/// <param name="Pose">The shape, or <see cref="Pose.None"/> when it is not clearly one of them.</param>
/// <param name="Direction">Where a <see cref="Pose.Point"/> or <see cref="Pose.Thumb"/> aims.</param>
/// <param name="Centre">Palm centre in frame pixels. Steady across finger movement, which is why swipes are measured from it.</param>
/// <param name="PalmSize">Wrist to middle knuckle, in pixels. The unit distances are expressed in, so nothing depends on how far away the hand is.</param>
/// <param name="Fingers">Thumb, index, middle, ring, pinky. Carried for the diagnostic overlay — when a pose will not register, this is the number that says why.</param>
public readonly record struct PoseReading(
    Pose Pose, Direction Direction, Point2f Centre, float PalmSize, bool[] Fingers);

/// <summary>
/// Turns 21 landmarks into one of four poses.
///
/// Geometry rather than a third model, and that is the reason the landmark
/// pipeline was chosen over a one-stage gesture detector: the vocabulary lives
/// here, in code, where it can be changed without retraining anything and where
/// the intermediate values can be printed. A pose that will not register is
/// nearly always one finger being read as curled when it is out, and that is
/// visible in <see cref="PoseReading.Fingers"/> and nowhere else.
/// </summary>
public static class HandPoseReader
{
    // Landmark indices, named once. Written as numbers at the call site they are
    // unreadable and every one of them is a silent bug if it is off by one.
    private const int Wrist = 0;
    private const int ThumbMcp = 2, ThumbIp = 3, ThumbTip = 4;
    private const int IndexMcp = 5, IndexPip = 6, IndexDip = 7, IndexTip = 8;
    private const int MiddleMcp = 9;

    /// <summary>(MCP, PIP, DIP, TIP) for index, middle, ring and pinky.</summary>
    private static readonly (int Mcp, int Pip, int Dip, int Tip)[] Fingers =
    [
        (IndexMcp, IndexPip, IndexDip, IndexTip),
        (MiddleMcp, 10, 11, 12),
        (13, 14, 15, 16),
        (17, 18, 19, 20),
    ];

    /// <summary>
    /// Degrees of bend below which a finger counts as out.
    ///
    /// Measured between "PIP back to the wrist" and "tip back to the DIP": both
    /// point toward the palm on a straight finger, and the second reverses as
    /// the finger curls. 60 sits between OpenCV's own reference thresholds,
    /// which call under 49 straight and over 65 curled and leave the gap
    /// undecided. There is no measurement behind 60 on a real hand — this
    /// machine has no usable camera — so it is a config key rather than a
    /// constant, and the preview prints the angle it computed.
    /// </summary>
    public const double FingerStraightDegrees = 60;

    /// <summary>The thumb bends less than a finger does, so it needs its own bar.</summary>
    public const double ThumbStraightDegrees = 40;

    /// <summary>
    /// How much longer one axis must be than the other before a direction is
    /// called. 1.2, so a finger held diagonally reports <see cref="Direction.None"/>
    /// rather than picking whichever axis won by a pixel — a wrong seek is worse
    /// than no seek.
    /// </summary>
    private const float DirectionMargin = 1.2f;

    /// <param name="mirrored">
    /// Whether the frame was flipped horizontally before it reached the model,
    /// which it is by default. A camera facing you shows your left hand on the
    /// right of the picture; the flip undoes that, so with it on, image +x is
    /// already the direction you would call your right. With it off the sign
    /// has to be put back — the same correction, for the same reason, that
    /// <c>CameraFaceSource.ToTrackerConvention</c> applies to yaw.
    /// </param>
    public static PoseReading Read(HandLandmarks hand, bool mirrored)
    {
        var p = hand.Points;
        if (p.Length < HandLandmarks.PointCount)
            return new PoseReading(Pose.None, Direction.None, default, 0, new bool[5]);

        var extended = new bool[5];
        extended[0] = ThumbExtended(p);
        for (var i = 0; i < Fingers.Length; i++) extended[i + 1] = FingerExtended(p, Fingers[i]);

        var (thumb, index, middle, ring, pinky) =
            (extended[0], extended[1], extended[2], extended[3], extended[4]);

        // Ordered so the more specific tests come first. Fist and Thumb share
        // four curled fingers and differ only in the thumb, so a Fist test that
        // ignored the thumb would swallow every thumbs-up.
        var pose = (thumb, index, middle, ring, pinky) switch
        {
            // The thumb is deliberately not part of this. A hand held up to say
            // "stop" has the thumb anywhere from flat against the palm to fully
            // out, and requiring a particular one of those would reject the
            // gesture for half the people making it. Nothing else in the set has
            // four fingers out, so there is nothing to confuse it with.
            (_, true, true, true, true) => Pose.OpenPalm,
            (true, false, false, false, false) => Pose.Thumb,
            (false, false, false, false, false) => Pose.Fist,
            // The thumb is free here too: people point with it tucked and with
            // it cocked, and both mean the same thing.
            (_, true, false, false, false) => Pose.Point,
            _ => Pose.None,
        };

        var direction = pose switch
        {
            Pose.Point => Aim(p[IndexMcp], p[IndexTip], mirrored),
            Pose.Thumb => Aim(p[ThumbMcp], p[ThumbTip], mirrored),
            _ => Direction.None,
        };

        var palmSize = Distance(p[Wrist], p[MiddleMcp]);
        return new PoseReading(pose, direction, p[MiddleMcp], palmSize, extended);
    }

    /// <summary>
    /// A finger is out when its tip has travelled away from the wrist and the
    /// last two joints still line up with the first.
    /// </summary>
    /// <remarks>
    /// Two tests because each one alone has a pose that defeats it. The distance
    /// test alone calls a finger out whenever the whole hand tips toward the
    /// camera, since foreshortening moves the tip without straightening
    /// anything. The angle test alone is unreliable on a finger curled so
    /// tightly that the tip comes back around near the knuckle, where the
    /// vectors are short and noisy. Requiring both costs nothing and neither
    /// failure survives it.
    /// </remarks>
    private static bool FingerExtended(Point2f[] p, (int Mcp, int Pip, int Dip, int Tip) f)
    {
        if (Distance(p[0], p[f.Tip]) <= Distance(p[0], p[f.Pip])) return false;
        return AngleBetween(Sub(p[0], p[f.Pip]), Sub(p[f.Dip], p[f.Tip])) < FingerStraightDegrees;
    }

    /// <summary>
    /// The thumb, which does not fold the way the fingers do.
    /// </summary>
    /// <remarks>
    /// It hinges sideways across the palm rather than curling into it, so the
    /// distance from the wrist barely changes and the finger test above reads it
    /// as out no matter what it is doing. Compared against the index knuckle
    /// instead: a thumb held out reaches past it, a thumb folded across the palm
    /// does not.
    /// </remarks>
    private static bool ThumbExtended(Point2f[] p)
    {
        if (Distance(p[0], p[ThumbTip]) <= Distance(p[0], p[IndexMcp])) return false;
        return AngleBetween(Sub(p[0], p[ThumbMcp]), Sub(p[ThumbIp], p[ThumbTip])) < ThumbStraightDegrees;
    }

    /// <summary>Which way a digit aims, or None when it is too diagonal to say.</summary>
    private static Direction Aim(Point2f from, Point2f to, bool mirrored)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (!mirrored) dx = -dx;

        if (Math.Abs(dx) > Math.Abs(dy) * DirectionMargin) return dx > 0 ? Direction.Right : Direction.Left;
        // Image y grows downward, so a tip above its knuckle has the smaller y.
        if (Math.Abs(dy) > Math.Abs(dx) * DirectionMargin) return dy < 0 ? Direction.Up : Direction.Down;
        return Direction.None;
    }

    private static Point2f Sub(Point2f a, Point2f b) => new(a.X - b.X, a.Y - b.Y);

    private static float Distance(Point2f a, Point2f b) => MathF.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Angle between two vectors in degrees, or 180 if either has no length.</summary>
    private static double AngleBetween(Point2f a, Point2f b)
    {
        var la = MathF.Sqrt(a.X * a.X + a.Y * a.Y);
        var lb = MathF.Sqrt(b.X * b.X + b.Y * b.Y);
        // Degenerate means "cannot tell", and for every caller here the safe
        // answer to that is "not extended", which is what 180 produces.
        if (la < 1e-6 || lb < 1e-6) return 180;

        var cos = Math.Clamp((a.X * b.X + a.Y * b.Y) / (la * lb), -1f, 1f);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }
}
