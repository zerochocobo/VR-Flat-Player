using OpenCvSharp;

namespace HeadTrackBridge.Tracking.Face;

/// <summary>
/// Five landmarks in, head rotation out, via OpenCV's PnP.
///
/// Method choice is not free here, and the difference is not subtle:
///
///   Iterative  needs >= 6 points (its DLT initialisation does) and throws on
///              the 5 a face detector gives.
///   AP3P       accepts exactly 3 or 4.
///   EPNP       works, exact on synthetic input.
///   SQPNP      works, exact on synthetic input, and is the better-conditioned
///              of the two on near-degenerate configurations -- which a face
///              is, since four of the five points are close to coplanar.
///
/// Measured, not assumed: the round trip in the unit tests puts a known
/// rotation in and gets it back to a hundredth of a degree.
/// </summary>
public sealed class HeadPoseSolver : IDisposable
{
    private readonly Mat _camera;
    private readonly Mat _distortion;
    private readonly Point3f[] _model;

    private readonly Point3f[] _poseModel;

    /// <summary>
    /// Pose from 2DFAN4's landmarks, using the six rigid points in
    /// <see cref="FaceGeometry.PoseIndices"/>.
    ///
    /// Six points also unlocks SOLVEPNP_ITERATIVE, which five could not use —
    /// its DLT initialisation needs six. Iterative refines by minimising
    /// reprojection error rather than solving in one shot, which is what we
    /// want now that the inputs are worth refining.
    /// </summary>
    public HeadPose? Solve68(Point2f[] landmarks, double timeSeconds)
    {
        if (landmarks.Length < 68) return null;

        var image = new Point2f[FaceGeometry.PoseIndices.Length];
        for (var i = 0; i < image.Length; i++)
            image[i] = landmarks[FaceGeometry.PoseIndices[i]];

        return SolveWith(_poseModel, image, timeSeconds, SolvePnPMethod.Iterative);
    }

    public HeadPoseSolver(int frameWidth, int frameHeight)
    {
        var f = FaceGeometry.FocalLengthPixels(frameWidth);
        _camera = Mat.FromArray(new[,]
        {
            { f, 0, frameWidth / 2.0 },
            { 0, f, frameHeight / 2.0 },
            { 0, 0, 1.0 },
        });

        // Assumed zero. Webcam barrel distortion mostly affects the frame edges,
        // and a face being tracked sits near the middle.
        _distortion = Mat.Zeros(4, 1, MatType.CV_64FC1).ToMat();

        _model = FaceGeometry.Model
            .Select(p => new Point3f((float)p.X, (float)p.Y, (float)p.Z))
            .ToArray();

        _poseModel = FaceGeometry.PoseModel
            .Select(p => new Point3f((float)p.X, (float)p.Y, (float)p.Z))
            .ToArray();
    }

    /// <summary>
    /// Head rotation in degrees and translation in centimetres, or null if the
    /// solver could not converge.
    ///
    /// Translation is returned but unused by the view mapper today. It is the
    /// input a later pivot correction needs: turning the head also translates
    /// the face, because the neck axis sits behind it, and separating the two
    /// needs both halves of the pose.
    /// </summary>
    public HeadPose? Solve(in FaceLandmarks lm, double timeSeconds)
    {
        var image = new[]
        {
            new Point2f((float)lm.ImageLeftEyeX, (float)lm.ImageLeftEyeY),
            new Point2f((float)lm.ImageRightEyeX, (float)lm.ImageRightEyeY),
            new Point2f((float)lm.NoseX, (float)lm.NoseY),
            new Point2f((float)lm.ImageLeftMouthX, (float)lm.ImageLeftMouthY),
            new Point2f((float)lm.ImageRightMouthX, (float)lm.ImageRightMouthY),
        };

        return SolveWith(_model, image, timeSeconds, SolvePnPMethod.SQPNP);
    }

    // Carried between calls so each solve can start from the last answer.
    private Mat? _lastRvec, _lastTvec;

    private HeadPose? SolveWith(Point3f[] model, Point2f[] image, double timeSeconds,
                                SolvePnPMethod method)
    {
        // Seeded from the previous frame when the method supports it.
        //
        // Six points on a face are nearly coplanar, which leaves PnP mildly
        // ambiguous: several poses reproject almost equally well, and solving
        // each frame from scratch lets the answer wander between them. That is
        // not jitter — it is a slow drift, and no amount of smoothing removes
        // it, because the input is not what is moving.
        //
        // An extrinsic guess anchors the refinement near where the head already
        // was, so it settles into the same branch every frame instead of
        // hopping. Iterative is the only method here that accepts one.
        var guess = method == SolvePnPMethod.Iterative && _lastRvec is not null && _lastTvec is not null;

        var rvec = guess ? _lastRvec!.Clone() : new Mat();
        var tvec = guess ? _lastTvec!.Clone() : new Mat();
        try
        {
            Cv2.SolvePnP(InputArray.Create(model), InputArray.Create(image),
                         _camera, _distortion, rvec, tvec, guess, method);

            if (method == SolvePnPMethod.Iterative)
            {
                _lastRvec?.Dispose();
                _lastTvec?.Dispose();
                _lastRvec = rvec.Clone();
                _lastTvec = tvec.Clone();
            }
        }
        catch (OpenCVException)
        {
            // A failed solve must not poison the next one with a stale seed.
            _lastRvec?.Dispose();
            _lastTvec?.Dispose();
            _lastRvec = _lastTvec = null;
            rvec.Dispose();
            tvec.Dispose();
            return null;
        }
        using var _r = rvec;
        using var _t = tvec;

        using var rot = new Mat();
        Cv2.Rodrigues(rvec, rot);

        var m = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = rot.At<double>(i, j);

        var (yaw, pitch, roll) = FaceGeometry.ToEuler(m);
        if (double.IsNaN(yaw) || double.IsNaN(pitch) || double.IsNaN(roll)) return null;

        // mm -> cm, matching the unit convention HeadPose carries from opentrack.
        return new HeadPose(yaw, pitch, roll,
                            tvec.At<double>(0) / 10.0,
                            tvec.At<double>(1) / 10.0,
                            tvec.At<double>(2) / 10.0,
                            timeSeconds);
    }

    public void Dispose()
    {
        _camera.Dispose();
        _distortion.Dispose();
        _lastRvec?.Dispose();
        _lastTvec?.Dispose();
    }
}
