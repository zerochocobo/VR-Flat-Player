namespace HeadTrackBridge.Tracking;

/// <summary>
/// One head-pose sample. Rotations in degrees, translations in cm — this is the
/// unit convention opentrack uses on the wire, so we keep it end to end.
/// </summary>
public readonly record struct HeadPose(
    double Yaw,
    double Pitch,
    double Roll,
    double X,
    double Y,
    double Z,
    double TimeSeconds)
{
    public static HeadPose Zero(double t) => new(0, 0, 0, 0, 0, 0, t);

    public override string ToString() =>
        $"yaw={Yaw,7:F2}  pitch={Pitch,7:F2}  roll={Roll,7:F2}  x={X,7:F2}  y={Y,7:F2}  z={Z,7:F2}";
}
