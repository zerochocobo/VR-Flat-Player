using System.Runtime.InteropServices;

namespace HeadTrackBridge.Tracking;

/// <summary>
/// Pretends to be a webcam. This exists because the 8K-capable box has no
/// camera: it lets you develop and tune the whole render path there, then
/// switch <c>source</c> to <c>udp</c> on the laptop with no other changes.
///
/// Jitter is deliberately injected in every mode — without it the One Euro
/// filter looks perfect and you will ship an unusable jitter response.
/// </summary>
public sealed class SyntheticSource : ITrackingSource
{
    private readonly SyntheticMode _mode;
    private readonly double _rateHz;
    private readonly double _jitterDegrees;
    private readonly Random _rng = new(1234);
    private Task? _loop;

    public SyntheticSource(SyntheticMode mode, double rateHz = 60, double jitterDegrees = 0.35)
    {
        _mode = mode;
        _rateHz = rateHz;
        _jitterDegrees = jitterDegrees;
    }

    public string Name => $"synthetic:{_mode.ToString().ToLowerInvariant()}";

    public event Action<HeadPose>? PoseReceived;

    public void Start(CancellationToken ct) => _loop = Task.Run(() => Loop(ct), ct);

    private async Task Loop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / _rateHz));
        while (await SafeTick(timer, ct).ConfigureAwait(false))
        {
            var t = Clock.Seconds;
            var (yaw, pitch) = _mode switch
            {
                SyntheticMode.Sweep => (22.0 * Math.Sin(2 * Math.PI * 0.10 * t),
                                        9.0 * Math.Sin(2 * Math.PI * 0.07 * t + 1.1)),
                SyntheticMode.Mouse => MouseAngles(),
                _ => (0.0, 0.0),
            };

            PoseReceived?.Invoke(new HeadPose(
                Yaw: yaw + Jitter(),
                Pitch: pitch + Jitter(),
                Roll: Jitter(),
                X: 0, Y: 0, Z: 60,
                TimeSeconds: t));
        }
    }

    private static async Task<bool> SafeTick(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    private double Jitter() => (_rng.NextDouble() * 2 - 1) * _jitterDegrees;

    /// <summary>
    /// Maps the cursor to +/-30 deg yaw and +/-20 deg pitch across the primary
    /// screen, so dragging the mouse feels like turning your head.
    /// </summary>
    private static (double Yaw, double Pitch) MouseAngles()
    {
        if (!GetCursorPos(out var p)) return (0, 0);
        var w = GetSystemMetrics(SM_CXSCREEN);
        var h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0) return (0, 0);

        var nx = (p.X / (double)w) * 2 - 1;   // -1 .. +1
        var ny = (p.Y / (double)h) * 2 - 1;
        return (nx * 30.0, -ny * 20.0);       // screen Y grows downward; head pitch does not
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public void Dispose()
    {
        try { _loop?.Wait(TimeSpan.FromMilliseconds(200)); } catch { /* shutting down */ }
    }
}
