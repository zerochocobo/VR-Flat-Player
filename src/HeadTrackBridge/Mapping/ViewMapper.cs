using HeadTrackBridge.Filtering;
using HeadTrackBridge.Tracking;

namespace HeadTrackBridge.Mapping;

public readonly record struct ViewAngles(double YawDegrees, double PitchDegrees, double RollDegrees);

/// <summary>
/// Turns a stream of head poses into a view orientation for the 360 renderer.
///
/// Pipeline, in order:
///   raw pose -> One Euro filter -> subtract centre -> deadzone -> response
///   curve/gain -> invert -> clamp.
///
/// Filtering happens on the raw signal, before gain, so the filter constants
/// stay in real head-degrees and do not need retuning when you change gain.
/// </summary>
public sealed class ViewMapper
{
    private readonly BridgeConfig _cfg;
    private readonly OneEuroFilter _fYaw;
    private readonly OneEuroFilter _fPitch;
    private readonly OneEuroFilter _fRoll;

    private double _centreYaw, _centrePitch, _centreRoll;
    private bool _haveCentre;
    private double _lastPoseTime = double.NaN;

    /// <summary>Smoothed interval between arriving poses. 0 until two have landed.</summary>
    private double _poseGap;

    // Head-derived part of the view, kept separately from the manual part so a
    // mouse drag and head tracking can coexist instead of overwriting each other.
    private ViewAngles _head;
    private ViewAngles _headTarget;
    private double _manualYaw, _manualPitch;
    private bool _dragging;

    public ViewMapper(BridgeConfig cfg)
    {
        _cfg = cfg;
        _fYaw = MakeFilter(cfg.Filter);
        _fPitch = MakeFilter(cfg.Filter);
        _fRoll = MakeFilter(cfg.Filter);
    }

    private static OneEuroFilter MakeFilter(FilterConfig f) =>
        new(f.MinCutoff, f.Beta, f.DerivativeCutoff);

    /// <summary>Last mapped output. Held (not zeroed) when tracking drops out.</summary>
    public ViewAngles Current { get; private set; }

    /// <summary>Last filtered head pose, in head-degrees relative to centre. For the status line.</summary>
    public ViewAngles CurrentRelative { get; private set; }

    public bool HasSignal { get; private set; }

    /// <summary>Make the current head position the new neutral.</summary>
    public void RequestRecenter() => _haveCentre = false;

    /// <summary>Return the view to dead ahead without moving the head reference.</summary>
    public void ResetView()
    {
        _manualYaw = 0;
        _manualPitch = 0;
        _head = default;
        _headTarget = default;
        Current = default;
        CurrentRelative = default;
        RequestRecenter();
    }

    /// <summary>
    /// Mouse drag begins. Head input is frozen for the duration so the two
    /// inputs do not fight over the same degrees while the user is aiming.
    /// </summary>
    public void BeginManualOverride() => _dragging = true;

    // How far the view may turn before it runs off the edge of the picture.
    // Infinity means "no edge in this axis", which is what a full 360 sphere
    // has horizontally. Set by the mode controller, because it depends on the
    // projection and the field of view, neither of which this class owns.
    private double _yawLimit = double.PositiveInfinity;
    private double _pitchLimit = double.PositiveInfinity;

    /// <summary>
    /// Constrain panning to the area the source actually covers.
    ///
    /// A 180 file is half a sphere: pan past its edge and mpv360 has nothing to
    /// sample, so you get a large flat void beside the picture. Every other VR
    /// player stops you at the edge instead, and so does this now.
    /// </summary>
    public void SetContentLimits(double yawLimitDegrees, double pitchLimitDegrees)
    {
        _yawLimit = yawLimitDegrees;
        _pitchLimit = pitchLimitDegrees;

        // Pull the current view back inside the new limits rather than waiting
        // for the next input: switching 360 -> 180 while looking behind you
        // would otherwise leave you staring at the void until you dragged.
        _manualYaw = ClampYaw(_manualYaw);
        _manualPitch = Math.Clamp(_manualPitch, -PitchBound, PitchBound);
        Recompose();
    }

    private double PitchBound => Math.Min(_cfg.Pitch.ClampDegrees, _pitchLimit);

    private double ClampYaw(double yaw) =>
        double.IsInfinity(_yawLimit) ? yaw : Math.Clamp(yaw, -_yawLimit, _yawLimit);

    /// <summary>Pan by a delta in view degrees. Signs follow "grab the world".</summary>
    public void ApplyManualDelta(double deltaYawDegrees, double deltaPitchDegrees)
    {
        // Clamped as it accumulates, not only when composed. Letting it run past
        // the edge and clamping later means dragging back does nothing until the
        // overshoot unwinds — the picture sticks and feels broken.
        _manualYaw = ClampYaw(_manualYaw - deltaYawDegrees);
        _manualPitch = Math.Clamp(_manualPitch + deltaPitchDegrees, -PitchBound, PitchBound);
        Recompose();
    }

    /// <summary>
    /// Drag ends. Recentring here is what makes the handover seamless: the head
    /// pose you are holding right now becomes the new neutral, so tracking
    /// resumes from where you dragged to instead of snapping back.
    /// </summary>
    public void EndManualOverride()
    {
        _dragging = false;
        RequestRecenter();
    }

    private void Recompose()
    {
        var yaw = _head.YawDegrees + _manualYaw;
        Current = new ViewAngles(
            // Wrapping is only correct when the content closes on itself.
            double.IsInfinity(_yawLimit) ? Wrap(yaw) : ClampYaw(yaw),
            Math.Clamp(_head.PitchDegrees + _manualPitch, -PitchBound, PitchBound),
            _head.RollDegrees);
    }

    private static double Wrap(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees < -180) degrees += 360;
        return degrees;
    }

    public void Accept(in HeadPose pose)
    {
        var t = pose.TimeSeconds;

        // How far apart poses are actually arriving, which is what the glide
        // has to cover. Measured here rather than assumed, because it is a
        // property of the machine and the model, not of the configuration.
        //
        // The cut-off between "a slow rate" and "a dropout" is two seconds, and
        // it was half a second first — which silently broke the whole thing for
        // the machines it was written for. A tracker managing 1.5 poses a
        // second has a 667 ms gap, so every sample was thrown out as a dropout,
        // the estimate stayed at zero, and neither the glide nor the timeout
        // ever adapted. It measured nothing precisely where it was needed.
        //
        // Nothing downstream trusts this number on its own: the glide clamps it
        // to GlideMaxSeconds and the timeout takes the larger of it and the
        // configured floor, so a stray long gap cannot do much even when one
        // does get through.
        if (!double.IsNaN(_lastPoseTime))
        {
            var gap = t - _lastPoseTime;
            if (gap > 0 && gap < 2.0)
                _poseGap = _poseGap <= 0 ? gap : _poseGap + (gap - _poseGap) * 0.2;
        }

        _lastPoseTime = t;
        HasSignal = true;

        double yaw = pose.Yaw, pitch = pose.Pitch, roll = pose.Roll;
        if (_cfg.Filter.Enabled)
        {
            yaw = _fYaw.Filter(yaw, t);
            pitch = _fPitch.Filter(pitch, t);
            roll = _fRoll.Filter(roll, t);
        }

        if (!_haveCentre)
        {
            if (_cfg.Recenter.OnFirstPose)
            {
                _centreYaw = yaw;
                _centrePitch = pitch;
                _centreRoll = roll;
            }
            else
            {
                _centreYaw = _centrePitch = _centreRoll = 0;
            }
            // Start the slack bands centred on the new reference. Carrying the
            // old ones over would let a recentre be undone by up to a band.
            _heldYaw = _heldPitch = _heldRoll = 0;
            _haveCentre = true;
        }

        ApplyAutoDrift(yaw, pitch, roll, t);

        var rYaw = Slack(yaw - _centreYaw, ref _heldYaw, _cfg.Yaw.StickyDegrees);
        var rPitch = Slack(pitch - _centrePitch, ref _heldPitch, _cfg.Pitch.StickyDegrees);
        var rRoll = Slack(roll - _centreRoll, ref _heldRoll, _cfg.Roll.StickyDegrees);

        // The held value, not the raw one. This line is what the user reads to
        // judge whether the view is steady, so it has to report the number the
        // view is actually built from.
        CurrentRelative = new ViewAngles(rYaw, rPitch, rRoll);

        // Frozen mid-drag: the mouse owns the view until the button comes up.
        if (_dragging) return;

        // The target, not the view itself. Advance() walks the view towards it.
        _headTarget = new ViewAngles(
            Map(rYaw, _cfg.Yaw),
            Map(rPitch, _cfg.Pitch),
            Map(rRoll, _cfg.Roll));

        if (_cfg.Filter.GlideSeconds <= 0)
        {
            _head = _headTarget;
            Recompose();
        }
    }

    /// <summary>
    /// Walk the view towards the latest pose. Called from the output loop, not
    /// from pose arrival.
    ///
    /// This exists because pose samples are not evenly spaced and can be far
    /// apart. The 68-point landmarker costs a quarter of a second per call on a
    /// fast machine and much more on a slow one, so poses can arrive a second
    /// apart — and jumping straight to each one turns head tracking into a
    /// slideshow that teleports the view. One Euro cannot help: it filters the
    /// samples it is given, and with one sample a second there is nothing
    /// between them to smooth.
    ///
    /// Exponential easing rather than a fixed rate, so a small correction
    /// settles quickly while a large one still takes a moment, and it can never
    /// overshoot however long the gap was.
    /// </summary>
    public void Advance(double dtSeconds)
    {
        var tau = _cfg.Filter.GlideSeconds;
        if (tau <= 0 || dtSeconds <= 0) return;

        // Stretch the glide to the measured pose gap, never shrink it below the
        // configured floor. On a tracker that keeps up the gap is smaller than
        // the floor and this changes nothing; on one that does not, it is the
        // difference between moving smoothly and arriving in steps. See
        // FilterConfig.GlideMaxSeconds for the measurements.
        if (_poseGap > 0 && _cfg.Filter.GlideMaxSeconds > tau)
            tau = Math.Clamp(_poseGap, tau, _cfg.Filter.GlideMaxSeconds);

        var a = 1 - Math.Exp(-dtSeconds / tau);
        _head = new ViewAngles(
            _head.YawDegrees + (_headTarget.YawDegrees - _head.YawDegrees) * a,
            _head.PitchDegrees + (_headTarget.PitchDegrees - _head.PitchDegrees) * a,
            _head.RollDegrees + (_headTarget.RollDegrees - _head.RollDegrees) * a);
        Recompose();
    }

    // Where each axis has been dragged to. Head-degrees relative to centre.
    private double _heldYaw, _heldPitch, _heldRoll;

    /// <summary>
    /// Let the head move <paramref name="band"/> degrees before the view does.
    ///
    /// The picture was rocking on a head the user believed was still, because
    /// the detector is never quite still: a landmark shifts a pixel between
    /// frames, a pixel is about a degree of pose, and the gain multiplies that
    /// into visible sway. Filtering could only trade it for lag — the signal
    /// and the noise are the same size at rest, so no filter can tell them
    /// apart, and every attempt so far has just moved the problem.
    ///
    /// This decides instead of averaging. Below the band the answer is the
    /// previous answer, exactly, so the view is bit-for-bit motionless; above
    /// it the band is dragged along and the view tracks at full rate with no
    /// smoothing added.
    ///
    /// The obvious alternative — quantising the range into fixed detents and
    /// stepping between them — was rejected. Fixed detents put the boundaries
    /// at absolute angles, so a head that happens to rest on one shakes across
    /// it just as badly, and a slow deliberate turn arrives in visible notches.
    /// Dragging the band with the head has neither problem: the output is
    /// continuous, so there is no step to see when it does start moving.
    /// </summary>
    private static double Slack(double raw, ref double held, double band)
    {
        if (band <= 0) { held = raw; return raw; }
        if (raw > held + band) held = raw - band;
        else if (raw < held - band) held = raw + band;
        return held;
    }

    private double _driftLastTime = double.NaN;

    private void ApplyAutoDrift(double yaw, double pitch, double roll, double t)
    {
        var tau = _cfg.Recenter.AutoDriftTimeConstant;
        if (tau <= 0)
        {
            _driftLastTime = t;
            return;
        }

        var dt = double.IsNaN(_driftLastTime) ? 0 : Math.Clamp(t - _driftLastTime, 0, 0.25);
        _driftLastTime = t;
        if (dt <= 0) return;

        // Only creep the reference while the user is roughly centred; otherwise
        // holding a deliberate off-axis look would silently become the new zero.
        var max = _cfg.Recenter.AutoDriftMaxDegrees;
        if (Math.Abs(yaw - _centreYaw) > max || Math.Abs(pitch - _centrePitch) > max) return;

        var a = 1 - Math.Exp(-dt / tau);
        _centreYaw += (yaw - _centreYaw) * a;
        _centrePitch += (pitch - _centrePitch) * a;
        _centreRoll += (roll - _centreRoll) * a;
    }

    /// <summary>
    /// Check whether the source has gone quiet. Returns true the moment it
    /// transitions to stale, so the caller can log it once.
    /// </summary>
    public bool CheckStale(double nowSeconds)
    {
        if (!HasSignal || double.IsNaN(_lastPoseTime)) return false;

        // Three missed poses, or the configured floor, whichever is longer.
        //
        // The floor alone is a fixed number against a rate that varies by an
        // order of magnitude between machines. At 30 poses a second 0.5 s is
        // fifteen of them and clearly a dropout; at three it is one and a half,
        // so an ordinary pair of slow frames looked like the tracker dying. The
        // view froze, the status line said LOST, a pose arrived, it all resumed
        // — over and over, in a log where the camera never actually failed.
        var timeout = Math.Max(_cfg.TrackingTimeoutSeconds,
                               _poseGap * _cfg.TrackingTimeoutPoseGaps);
        if (nowSeconds - _lastPoseTime <= timeout) return false;
        HasSignal = false;
        return true;   // view intentionally keeps its last value
    }

    private static double Map(double relativeDegrees, AxisConfig axis)
    {
        if (axis.OutputRangeDegrees == 0) return 0;

        var v = relativeDegrees;

        if (axis.DeadzoneDegrees > 0)
        {
            var a = Math.Abs(v);
            if (a <= axis.DeadzoneDegrees) return 0;
            // Rescale rather than subtract-and-jump, so there is no step at the edge.
            v = Math.Sign(v) * (a - axis.DeadzoneDegrees);
        }

        var range = Math.Max(1e-6, axis.InputRangeDegrees - axis.DeadzoneDegrees);
        var n = Math.Clamp(v / range, -1.0, 1.0);
        var shaped = Math.Sign(n) * Math.Pow(Math.Abs(n), axis.Curve);
        var outDeg = shaped * axis.OutputRangeDegrees;

        if (axis.Invert) outDeg = -outDeg;
        return Math.Clamp(outDeg, -axis.ClampDegrees, axis.ClampDegrees);
    }
}
