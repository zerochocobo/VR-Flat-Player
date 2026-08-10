namespace HeadTrackBridge.Filtering;

/// <summary>
/// One Euro filter (Casiez, Roussel, Vogel 2012).
///
/// Chosen over a fixed low-pass or a Kalman filter because it adapts: it
/// filters hard when the signal is slow (kills the involuntary micro-jitter
/// that makes a head-tracked view shimmer) and backs off when the signal is
/// fast (so a deliberate head turn does not lag).
///
/// Tuning order that actually works:
///   1. Set <c>beta = 0</c>, lower <c>minCutoff</c> until jitter at rest is gone.
///   2. Raise <c>beta</c> until a fast head turn no longer feels like it drags.
/// </summary>
public sealed class OneEuroFilter
{
    private readonly LowPass _x = new();
    private readonly LowPass _dx = new();
    private double _lastTime = double.NaN;

    public OneEuroFilter(double minCutoff = 1.0, double beta = 0.007, double dCutoff = 1.0)
    {
        MinCutoff = minCutoff;
        Beta = beta;
        DCutoff = dCutoff;
    }

    /// <summary>Cutoff in Hz at zero speed. Lower = smoother but laggier at rest.</summary>
    public double MinCutoff { get; set; }

    /// <summary>Speed coefficient. Higher = less lag during fast motion.</summary>
    public double Beta { get; set; }

    /// <summary>Cutoff for the derivative estimate. 1.0 is almost always right.</summary>
    public double DCutoff { get; set; }

    public void Reset()
    {
        _x.Reset();
        _dx.Reset();
        _lastTime = double.NaN;
    }

    public double Filter(double value, double timeSeconds)
    {
        double rate;
        if (double.IsNaN(_lastTime) || timeSeconds <= _lastTime)
        {
            rate = 60.0;                       // sane default for the very first sample
        }
        else
        {
            rate = 1.0 / (timeSeconds - _lastTime);
            // A stalled source must not produce an absurd rate estimate.
            rate = Math.Clamp(rate, 1.0, 1000.0);
        }
        _lastTime = timeSeconds;

        var dValue = _x.HasValue ? (value - _x.Raw) * rate : 0.0;
        var edValue = _dx.Apply(dValue, Alpha(DCutoff, rate));

        var cutoff = MinCutoff + Beta * Math.Abs(edValue);
        return _x.Apply(value, Alpha(cutoff, rate));
    }

    private static double Alpha(double cutoff, double rate)
    {
        var tau = 1.0 / (2 * Math.PI * cutoff);
        var te = 1.0 / rate;
        return 1.0 / (1.0 + tau / te);
    }

    private sealed class LowPass
    {
        private double _hat;

        public bool HasValue { get; private set; }
        public double Raw { get; private set; }

        public double Apply(double value, double alpha)
        {
            Raw = value;
            _hat = HasValue ? alpha * value + (1 - alpha) * _hat : value;
            HasValue = true;
            return _hat;
        }

        public void Reset()
        {
            HasValue = false;
            _hat = 0;
            Raw = 0;
        }
    }
}
