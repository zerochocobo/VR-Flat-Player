using System.Globalization;

namespace HeadTrackBridge.Tracking;

/// <summary>
/// Replays a CSV written by <see cref="PoseRecorder"/>, preserving the original
/// inter-sample timing. Loops forever so you can leave it running while tuning.
/// </summary>
public sealed class ReplaySource : ITrackingSource
{
    private readonly List<HeadPose> _poses = new();
    private readonly bool _loop;
    private Task? _task;

    public ReplaySource(string path, bool loop = true)
    {
        _loop = loop;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith("t,", StringComparison.Ordinal)) continue;
            var f = line.Split(',');
            if (f.Length < 7) continue;
            _poses.Add(new HeadPose(
                Yaw: D(f[1]), Pitch: D(f[2]), Roll: D(f[3]),
                X: D(f[4]), Y: D(f[5]), Z: D(f[6]),
                TimeSeconds: D(f[0])));
        }

        if (_poses.Count == 0)
            throw new InvalidDataException($"No pose rows found in '{path}'.");

        Name = $"replay:{Path.GetFileName(path)} ({_poses.Count} samples)";
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    public string Name { get; }

    public event Action<HeadPose>? PoseReceived;

    public void Start(CancellationToken ct) => _task = Task.Run(() => Loop(ct), ct);

    private async Task Loop(CancellationToken ct)
    {
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var lapOffset = 0.0;

        while (!ct.IsCancellationRequested)
        {
            var t0 = _poses[0].TimeSeconds;
            foreach (var p in _poses)
            {
                if (ct.IsCancellationRequested) return;

                var due = lapOffset + (p.TimeSeconds - t0);
                var wait = due - wall.Elapsed.TotalSeconds;
                if (wait > 0.0005)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }

                PoseReceived?.Invoke(p with { TimeSeconds = wall.Elapsed.TotalSeconds });
            }

            if (!_loop) return;
            lapOffset = wall.Elapsed.TotalSeconds + 0.25;   // small gap between laps
        }
    }

    public void Dispose()
    {
        try { _task?.Wait(TimeSpan.FromMilliseconds(200)); } catch { /* shutting down */ }
    }
}
