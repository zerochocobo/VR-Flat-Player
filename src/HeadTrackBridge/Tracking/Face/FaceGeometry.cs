using OpenCvSharp;

namespace HeadTrackBridge.Tracking.Face;

/// <summary>
/// The five points a face detector gives us, in image pixels.
///
/// Named by where they sit in the *image*, not by the subject's anatomy, and
/// that distinction is the whole reason these names were changed.
///
/// YuNet calls its first landmark the "right eye", meaning the subject's right,
/// which lands on the image's left for a camera pointed at them. But we mirror
/// the frame before detection, so the subject's right eye is on the image's
/// right and YuNet's "right eye" slot holds the subject's *left* one. Two
/// conventions that disagree, with nothing to catch it: the pose came out
/// correct anyway, purely because <see cref="FaceGeometry.Model"/> is
/// symmetric about X, so exchanging the pair changes nothing. Give that model
/// a real asymmetric face and it breaks with no error.
///
/// Image position is the one description that stays true whether the frame is
/// mirrored or not, so it is what these are named for.
/// </summary>
public readonly record struct FaceLandmarks(
    double ImageLeftEyeX, double ImageLeftEyeY,
    double ImageRightEyeX, double ImageRightEyeY,
    double NoseX, double NoseY,
    double ImageLeftMouthX, double ImageLeftMouthY,
    double ImageRightMouthX, double ImageRightMouthY);

/// <summary>
/// The fixed 3D face the landmarks are matched against, and the rotation
/// bookkeeping around it. No OpenCV here on purpose: this is the part with sign
/// and ordering conventions in it, which is the part worth unit-testing, and
/// keeping it free of native calls means the tests can check it directly.
/// </summary>
public static class FaceGeometry
{
    /// <summary>
    /// A generic adult face in millimetres. Origin at the nose tip, +X to the
    /// image right, +Y down, +Z away from the camera.
    ///
    /// It does not have to match the viewer's actual face. Every angle is used
    /// relative to a captured centre, so a face that is wider or narrower than
    /// this scales the response slightly but cannot introduce drift or bias —
    /// which is why there is no per-user calibration step.
    ///
    /// The nose tip is the only strongly out-of-plane point, and it is what
    /// makes pitch and yaw observable at all; the other four are near-coplanar.
    /// </summary>
    /// Ordered to match <see cref="FaceLandmarks"/>: image-left first. The pairs
    /// are symmetric about X, which is why the eye-slot confusion described
    /// there never produced a visible fault.
    public static readonly (double X, double Y, double Z)[] Model =
    [
        (-32, -38, 35),   // eye, image left
        ( 32, -38, 35),   // eye, image right
        (  0,   0,  0),   // nose tip
        (-27,  35, 28),   // mouth corner, image left
        ( 27,  35, 28),   // mouth corner, image right
    ];

    /// <summary>
    /// Which of 2DFAN4's 68 points we solve from, and where they sit in 3D.
    ///
    /// Six, not all sixty-eight. The extra points are mostly contour and
    /// eyelid detail that moves with expression, and a generic 3D face is
    /// wrong about them per-person; these six are rigid, far apart, and the
    /// classic set for this problem.
    ///
    /// The chin is the whole reason for the change. In the five-point model the
    /// only out-of-plane distance pitch could measure was the nose tip's 35 mm
    /// of protrusion. Here the chin sits 330 below and 65 behind the nose, so
    /// pitch gets a lever arm of the same order as the 450 between eye corners
    /// that yaw has always enjoyed. Units are arbitrary but consistent —
    /// solvePnP recovers rotation from their ratios.
    ///
    /// Indices follow the standard 68-point layout, and pairs are listed
    /// image-left first for the same reason as <see cref="FaceLandmarks"/>.
    /// </summary>
    public static readonly int[] PoseIndices = [30, 8, 36, 45, 48, 54];

    public static readonly (double X, double Y, double Z)[] PoseModel =
    [
        (   0,    0,    0),   // 30 nose tip, the origin
        (   0,  330,   65),   // 8  chin: +Y is down the image, +Z is away
        (-225, -170,  135),   // 36 eye corner, image left
        ( 225, -170,  135),   // 45 eye corner, image right
        (-150,  150,  125),   // 48 mouth corner, image left
        ( 150,  150,  125),   // 54 mouth corner, image right
    ];

    /// <summary>
    /// Rotation matrix to yaw/pitch/roll, for R composed as Ry·Rx·Rz.
    ///
    /// Multiplying that product out gives
    ///     m[1,2] = -sin(pitch)
    ///     m[1,0] =  cos(pitch)·sin(roll),  m[1,1] = cos(pitch)·cos(roll)
    ///     m[0,2] =  sin(yaw)·cos(pitch),   m[2,2] = cos(yaw)·cos(pitch)
    /// so each angle comes out of the entries that isolate it. Picking any
    /// other entries appears to work for single-axis rotations and quietly goes
    /// wrong once two axes move together, which is the normal case for a head.
    /// </summary>
    public static (double Yaw, double Pitch, double Roll) ToEuler(double[,] m)
    {
        var pitch = Math.Asin(Math.Clamp(-m[1, 2], -1, 1));
        var roll = Math.Atan2(m[1, 0], m[1, 1]);
        var yaw = Math.Atan2(m[0, 2], m[2, 2]);
        const double d = 180.0 / Math.PI;
        return (yaw * d, pitch * d, roll * d);
    }

    /// <summary>Ry·Rx·Rz, the inverse of <see cref="ToEuler"/>.</summary>
    public static double[,] FromEuler(double yawDeg, double pitchDeg, double rollDeg)
    {
        const double r = Math.PI / 180.0;
        double cy = Math.Cos(yawDeg * r), sy = Math.Sin(yawDeg * r);
        double cx = Math.Cos(pitchDeg * r), sx = Math.Sin(pitchDeg * r);
        double cz = Math.Cos(rollDeg * r), sz = Math.Sin(rollDeg * r);

        return Multiply(
            Multiply(
                new[,] { { cy, 0.0, sy }, { 0.0, 1.0, 0.0 }, { -sy, 0.0, cy } },
                new[,] { { 1.0, 0.0, 0.0 }, { 0.0, cx, -sx }, { 0.0, sx, cx } }),
            new[,] { { cz, -sz, 0.0 }, { sz, cz, 0.0 }, { 0.0, 0.0, 1.0 } });
    }

    public static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                for (var k = 0; k < 3; k++)
                    m[i, j] += a[i, k] * b[k, j];
        return m;
    }

    /// <summary>
    /// A webcam's focal length in pixels, guessed from the frame width.
    ///
    /// Roughly a 60-degree horizontal field of view, which is the common case
    /// for a laptop or USB camera. A real calibration would be better, but it
    /// would also mean asking every user to photograph a chessboard — and since
    /// the output is relative to a captured centre and then run through a gain
    /// curve the user tunes by feel, an error here shows up as a slightly
    /// different gain, not as a wrong direction.
    /// </summary>
    public static double FocalLengthPixels(int frameWidth) => frameWidth;

    /// <summary>Project <see cref="PoseModel"/>. Same purpose as the overload below.</summary>
    public static (double X, double Y)[] ProjectPose(double yawDeg, double pitchDeg, double rollDeg,
                                                     int width, int height, double distanceMm = 600)
    {
        var r = FromEuler(yawDeg, pitchDeg, rollDeg);
        var f = FocalLengthPixels(width);
        var result = new (double, double)[PoseModel.Length];

        for (var i = 0; i < PoseModel.Length; i++)
        {
            var (mx, my, mz) = PoseModel[i];
            var x = r[0, 0] * mx + r[0, 1] * my + r[0, 2] * mz;
            var y = r[1, 0] * mx + r[1, 1] * my + r[1, 2] * mz;
            var z = r[2, 0] * mx + r[2, 1] * my + r[2, 2] * mz + distanceMm;
            result[i] = (f * x / z + width / 2.0, f * y / z + height / 2.0);
        }
        return result;
    }

    /// <summary>
    /// Project the model through a pinhole camera. Only the tests use this —
    /// it is how they build landmark sets for a rotation they already know,
    /// so a round trip can prove the solver recovers what went in.
    /// </summary>
    public static FaceLandmarks Project(double yawDeg, double pitchDeg, double rollDeg,
                                        int width, int height, double distanceMm = 600)
    {
        var r = FromEuler(yawDeg, pitchDeg, rollDeg);
        var f = FocalLengthPixels(width);
        var p = new double[5, 2];

        for (var i = 0; i < 5; i++)
        {
            var (mx, my, mz) = Model[i];
            var x = r[0, 0] * mx + r[0, 1] * my + r[0, 2] * mz;
            var y = r[1, 0] * mx + r[1, 1] * my + r[1, 2] * mz;
            var z = r[2, 0] * mx + r[2, 1] * my + r[2, 2] * mz + distanceMm;
            p[i, 0] = f * x / z + width / 2.0;
            p[i, 1] = f * y / z + height / 2.0;
        }

        return new FaceLandmarks(
            p[0, 0], p[0, 1], p[1, 0], p[1, 1], p[2, 0], p[2, 1],
            p[3, 0], p[3, 1], p[4, 0], p[4, 1]);
    }

    /// <summary>
    /// Whether a set of 68 landmarks still sits properly inside the box they were
    /// found from, so that box can be used again on the next frame.
    /// </summary>
    /// <remarks>
    /// Two ways for it to stop fitting, and both have to be caught because they
    /// happen at different moments. Sideways: the head moves across the frame and
    /// the face drifts toward the edge of the crop, where the model has less and
    /// less of it to work with. Depth: the head comes closer or goes further and
    /// the face no longer fills the box it was measured in, which changes the
    /// scale the model sees.
    ///
    /// The numbers come from the crop geometry rather than from taste.
    /// <c>FaceLandmarker.Locate</c> takes a square of 1.6 times the longer side of
    /// the box, so there is 0.3 of a side of margin on each edge. Allowing a
    /// quarter of that leaves the face comfortably inside a crop that still looks
    /// like the ones the model was trained on. The scale window is wider, 0.75 to
    /// 1.35, because a face box and the bounding box of 68 landmarks are not the
    /// same rectangle to begin with — the landmarks stop at the eyebrows and the
    /// detector's box does not — so a constant offset between them is expected
    /// and only a change in it is interesting.
    ///
    /// The failure it does not catch: a face that leaves the room entirely. The
    /// landmarker will return 68 points from whatever is in the crop, and they
    /// may well pass both tests, so a pose keeps being produced from furniture
    /// until <see cref="CameraConfig.DetectFps"/> next brings the detector back.
    /// That is the ceiling on how wrong this can be, and it is why that setting
    /// is a rate rather than "only when the landmarks say so".
    ///
    /// Here rather than in CameraFaceSource because this is where the geometry
    /// the tests can reach lives; the loop that calls it needs a camera and a
    /// moving head and cannot be tested at all.
    /// </remarks>
    public static bool StillFits(Point2f[] dense, Rect box)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in dense)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        var side = Math.Max(box.Width, box.Height);
        if (side <= 0) return false;

        var driftX = Math.Abs((minX + maxX) / 2 - (box.X + box.Width / 2f));
        var driftY = Math.Abs((minY + maxY) / 2 - (box.Y + box.Height / 2f));
        if (Math.Max(driftX, driftY) > side * 0.15f) return false;

        var scale = Math.Max(maxX - minX, maxY - minY) / side;
        return scale is > 0.75f and < 1.35f;
    }
}
