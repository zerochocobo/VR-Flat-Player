using OpenCvSharp;

namespace HeadTrackBridge.Tracking.Hand;

/// <summary>
/// Everything gesture control decides, with no camera and no models in it.
/// </summary>
/// <remarks>
/// Split out of <see cref="GestureRecognizer"/> so it can be tested. The half
/// that runs the two networks cannot be exercised without a camera — the
/// machine this was written on has none — but the half that decides what a hand
/// shape *means* is where the behaviour people complain about lives: firing
/// twice, not firing at all, gesture mode dropping out mid-session, a swipe
/// triggering off its own follow-through. All of those are reachable here by
/// feeding a sequence of readings and timestamps.
///
/// It holds no state that outlives a session and nothing that needs disposing,
/// so a test can run a thousand simulated seconds in a millisecond.
/// </remarks>
public sealed class GestureStateMachine
{
    private readonly GestureConfig _cfg;
    private readonly bool _mirrored;

    private double _lastHandSeenAt = double.NegativeInfinity;
    private double _palmHoldSince = double.NaN;
    private Point2f _palmHoldAnchor;
    private bool _palmLatched;

    private Gesture _held = Gesture.None;
    private int _heldRuns;
    private Gesture _latched = Gesture.None;
    private double _lastFireAt = double.NegativeInfinity;
    private double _nextRepeatAt = double.PositiveInfinity;

    private readonly List<(double At, Point2f Centre, bool Open)> _trail = [];
    private Point2f _swipeAnchor;
    private double _swipeAnchorAt = double.NegativeInfinity;
    private bool _swipeAnchorSet;
    private bool _swipeSpent;
    private double _swipeReach;

    public GestureStateMachine(GestureConfig cfg, bool mirrored)
    {
        _cfg = cfg;
        _mirrored = mirrored;
    }

    /// <summary>True while gesture mode is on, which is also while head tracking is paused.</summary>
    public bool Armed { get; private set; }

    /// <summary>Raised when gesture mode is entered or left.</summary>
    public event Action<bool>? ArmedChanged;

    /// <summary>Raised when a gesture fires.</summary>
    public event Action<Gesture>? Fired;

    /// <summary>Which action table applies: a flat file and a VR file differ on the thumb.</summary>
    public bool Vr { get; set; }

    /// <summary>
    /// The furthest an open palm has reached inside the swipe window since this
    /// was last called, in palm widths, and resets it.
    /// </summary>
    /// <remarks>
    /// A failing swipe is otherwise completely silent — the same shape of
    /// problem as a face that is never found. This is the number that separates
    /// "you are not sweeping far enough" from "the hand is not being followed
    /// through the sweep at all", and those need opposite fixes.
    /// </remarks>
    public double TakeSwipeReach()
    {
        var reach = _swipeReach;
        _swipeReach = 0;
        return reach;
    }

    /// <summary>
    /// Feed one reading. <paramref name="reading"/> is null when no hand was
    /// found in the frame, which is a state in its own right rather than a
    /// missing call.
    /// </summary>
    public void Accept(PoseReading? reading, double now)
    {
        if (reading is not { } r)
        {
            // A hand that has gone leaves nothing latched behind it: the next one
            // to appear should be read from scratch rather than inheriting a
            // half-completed hold from the last one.
            _held = Gesture.None;
            _heldRuns = 0;
            BreakPalmHold();

            // The swipe anchor is the exception, and only for a moment. A hand
            // sweeping fast enough to be a swipe blurs, and the landmarker loses
            // it for a frame or two in the middle of the sweep — exactly the
            // motion the anchor exists to measure. Dropping it there restarted
            // the measurement mid-stroke, which is why a swipe that was made
            // never reached the threshold. Past the swipe window it is stale
            // anyway, and then it goes.
            if (now - _lastHandSeenAt > _cfg.SwipeSeconds) DropSwipe();

            // Armed with no hand in sight expires, so a session cannot be left
            // switched on — and with it, head tracking left switched off — by
            // simply walking away. Holding the palm again is the deliberate way
            // out; this is the accident.
            if (Armed && now - _lastHandSeenAt > _cfg.IdleTimeoutSeconds) SetArmed(false, now);
            return;
        }

        _lastHandSeenAt = now;
        UpdatePalmHold(r, now);

        if (!Armed) { DropSwipe(); return; }

        // Cooldown covers the moment after a fire, when the hand is still in the
        // shape that caused it and on its way to somewhere else. Without it a
        // swipe re-triggers off its own follow-through.
        //
        // It gates new gestures only, never a repeat. Gating everything was the
        // first version and it made RepeatSeconds a lie: at 0.4 against a 0.5
        // cooldown, volume actually stepped every 0.5 s and lowering the setting
        // did nothing at all. The two limits are for different things — one
        // stops an unintended second gesture, the other paces a deliberate one.
        //
        // The hold counter keeps running through it, so a gesture genuinely made
        // during the cooldown fires the moment it lifts rather than having to be
        // started over.
        var cooling = now - _lastFireAt < _cfg.CooldownSeconds;

        // Swipes are consumed either way — Swipe() marks itself spent whether or
        // not this fires. One found during the cooldown is the follow-through of
        // the last one, and letting it survive would only mean it fired a moment
        // later.
        if (Swipe(r, now) is { } swipe)
        {
            if (!cooling) Fire(swipe, now);
            return;
        }

        Evaluate(new Gesture(r.Pose, r.Direction, false), now, cooling);
    }

    // -------------------------------------------------------- gesture mode ---

    /// <summary>
    /// An open palm held still toggles gesture mode, in both directions.
    /// </summary>
    /// <remarks>
    /// One shape for both edges rather than a timeout for the way out. A timeout
    /// makes leaving something that happens *to* you, at a moment you did not
    /// choose, and it forces the window to be short enough to be safe — which is
    /// exactly what makes it too short to work in. Holding the palm again is
    /// deliberate, immediate, and the same motion as getting in, so there is one
    /// thing to learn rather than two.
    ///
    /// Still, because the same open palm swiping sideways means something else.
    /// The two cannot be confused: this one has to stay inside 0.6 palm widths
    /// for a whole second, and a swipe has to cover 1.0 in under one.
    ///
    /// The latch is what stops the hold from firing again on the very next
    /// reading — after the toggle the palm is, necessarily, still being held. It
    /// clears as soon as the hand moves or changes shape, so a swipe re-arms the
    /// toggle on its way past.
    /// </remarks>
    private void UpdatePalmHold(PoseReading reading, double now)
    {
        if (reading.Pose != Pose.OpenPalm) { BreakPalmHold(); return; }

        var drift = reading.PalmSize > 1
            ? Distance(reading.Centre, _palmHoldAnchor) / reading.PalmSize
            : float.MaxValue;

        if (double.IsNaN(_palmHoldSince) || drift > _cfg.TogglePalms)
        {
            _palmHoldSince = now;
            _palmHoldAnchor = reading.Centre;
            _palmLatched = false;
            HoldProgress = 0;
            return;
        }

        // Published even while it is only part way, because the second spent
        // holding a palm at a screen that does nothing is where people conclude
        // the feature is broken. A bar that visibly fills is the difference
        // between waiting and giving up — and one that stays empty says the
        // pose is not being read as an open palm at all, which is a different
        // problem with a different fix.
        HoldProgress = _palmLatched ? 0
            : Math.Clamp((now - _palmHoldSince) / Math.Max(0.05, _cfg.ToggleSeconds), 0, 1);

        if (_palmLatched || now - _palmHoldSince < _cfg.ToggleSeconds) return;

        _palmLatched = true;
        HoldProgress = 0;
        SetArmed(!Armed, now);
    }

    private void BreakPalmHold()
    {
        _palmHoldSince = double.NaN;
        _palmLatched = false;
        HoldProgress = 0;
    }

    /// <summary>How far through the palm hold that toggles gesture mode, 0 to 1.</summary>
    public double HoldProgress { get; private set; }

    private void SetArmed(bool on, double now)
    {
        if (Armed == on) return;
        Armed = on;

        // Everything about the previous state is dropped on the way through, in
        // both directions. Leaving a half-completed hold behind would let the
        // first reading after arming fire something the user was not asking for.
        _held = Gesture.None;
        _heldRuns = 0;
        _latched = Gesture.None;
        _nextRepeatAt = double.PositiveInfinity;
        DropSwipe();
        _lastFireAt = now;

        ArmedChanged?.Invoke(on);
    }

    // -------------------------------------------------------------- firing ---

    /// <summary>
    /// Decide whether a steady pose has earned a fire.
    /// </summary>
    /// <remarks>
    /// Edge-triggered with a hold. The hold is what separates a gesture from a
    /// hand that happens to pass through a shape on its way somewhere else; the
    /// edge is what stops a shape being held from firing on every reading. Both
    /// are necessary and neither is sufficient — without the edge a fist resting
    /// in frame toggles pause twelve times a second, and without the hold a hand
    /// waved past the camera fires whatever it looked most like in passing.
    ///
    /// The two adjustments, volume and field of view, opt back into repeating.
    /// They are the only ones where holding the pose is how you ask for more.
    /// </remarks>
    private void Evaluate(Gesture gesture, double now, bool cooling)
    {
        if (gesture != _held) { _held = gesture; _heldRuns = 0; }
        _heldRuns++;

        var action = GestureMap.Resolve(gesture, Vr);

        // A pose the map has nothing for still counts as leaving the last one,
        // which is what lets the same gesture be made twice in a row.
        if (action == GestureAction.None)
        {
            if (_heldRuns >= _cfg.HoldRuns) _latched = Gesture.None;
            return;
        }

        if (gesture == _latched)
        {
            if (GestureMap.Repeats(action) && now >= _nextRepeatAt)
            {
                _nextRepeatAt = now + RepeatInterval(action);
                Fire(gesture, now);
            }
            return;
        }

        if (_heldRuns < _cfg.HoldRuns || cooling) return;

        _latched = gesture;
        _nextRepeatAt = GestureMap.Repeats(action) ? now + RepeatInterval(action) : double.PositiveInfinity;
        Fire(gesture, now);
    }

    /// <summary>
    /// How long to wait before the held pose asks for the same thing again.
    /// </summary>
    /// <remarks>
    /// Seeking gets its own, because the interval is only half of the rate it
    /// produces and seeking has by far the largest step. See
    /// <see cref="GestureMap.IsSeek"/>.
    /// </remarks>
    private double RepeatInterval(GestureAction action) =>
        GestureMap.IsSeek(action) ? _cfg.SeekRepeatSeconds : _cfg.RepeatSeconds;

    private void Fire(Gesture gesture, double now)
    {
        _lastFireAt = now;
        Fired?.Invoke(gesture);
    }

    // --------------------------------------------------------------- swipe ---

    /// <summary>
    /// An open palm travelling sideways, or null.
    /// </summary>
    /// <remarks>
    /// Travel is measured from where the palm last came to rest, not across a
    /// sliding window.
    ///
    /// What was actually reported is that swipes almost never fired, and when one
    /// did it was hard to say why — 只能播放一个方向, only one direction ever
    /// plays, with "how to trigger it is a mystery". The log agrees and is more
    /// specific: the furthest reach in each reporting window, over ten minutes of
    /// deliberate swiping, was 0.3, 0.8, 0.9, 0.8, 0.3, 0.6, 1.0, 1.0 against a
    /// threshold of 1.5. Nothing reached it. That, not the direction, is the
    /// measured fault, and its cause is in <see cref="Accept"/>: travel was
    /// thrown away whenever the hand was lost for a frame, which happens when the
    /// hand is moving fastest.
    ///
    /// The origin change is a second thing, and it is a hazard rather than a
    /// diagnosis. Measured across a window, an outward sweep that falls short
    /// leaves its travel behind it, so bringing the hand back — the same
    /// distance, the other way — can clear the threshold on the return. From a
    /// rest anchor it cannot: a return only fires by travelling a full threshold
    /// *past* where the stroke began, which returning to neutral never does, and
    /// a stroke that did fire leaves the anchor spent until the hand is still
    /// again. Both are pinned by tests, neither by a camera.
    ///
    /// In palm widths rather than pixels, so the same motion works at arm's
    /// length and at a desk. A pixel threshold would need retuning for every
    /// camera, every resolution and every seating position, and would be wrong
    /// for all but one of them.
    ///
    /// The window is still there, and now it only bounds the stroke: a sweep that
    /// takes longer than this is not one. Drift is stopped a step earlier, by
    /// <see cref="AtRest"/> — a hand moving slowly enough to be drifting never
    /// leaves the rest test, so the anchor follows it and no travel accumulates
    /// at all.
    /// </remarks>
    private Gesture? Swipe(PoseReading reading, double now)
    {
        // No scale to express the travel in. Not a reason to throw the anchor
        // away — the next reading with a size can carry on from it.
        if (reading.PalmSize <= 1) return null;

        // Recorded whatever the shape is, and judged afterwards. A hand moving
        // fast enough to be a swipe blurs, and a frame read as something other
        // than an open palm in the middle of one must not throw away the travel
        // either side of it.
        _trail.Add((now, reading.Centre, reading.Pose == Pose.OpenPalm));
        _trail.RemoveAll(s => now - s.At > _cfg.SwipeSeconds);

        // Still, and has been for long enough to say so. This is where a swipe
        // starts from, and — because it is the only thing that clears the spent
        // flag — it is also the pause between one file change and the next.
        if (!_swipeAnchorSet || AtRest(reading, now))
        {
            Anchor(reading.Centre, now);
            _swipeSpent = false;
            return null;
        }

        var dx = reading.Centre.X - _swipeAnchor.X;
        var dy = reading.Centre.Y - _swipeAnchor.Y;

        // Kept for the diagnostic even when nothing fires, because "the sweep
        // reached 0.8 palm widths and needed 1.0" is the one sentence that tells
        // a user whether to sweep further or to stop trying.
        _swipeReach = Math.Max(_swipeReach, Math.Abs(dx) / reading.PalmSize);

        // The stroke took too long to be a sweep. Re-anchored where the hand is
        // now — without that, an anchor left behind at the far end of a stroke
        // could never be reached again and nothing would ever fire from it. The
        // spent flag deliberately survives this: re-anchoring at the turning
        // point of a stroke that just fired is exactly how the return stroke got
        // back in.
        if (now - _swipeAnchorAt > _cfg.SwipeSeconds) { Anchor(reading.Centre, now); return null; }

        // It has to be an open palm now, and to have been one for most of the
        // way. The first condition stops a fist that opens at the end of a
        // reach; the second tolerates the blurred frames in the middle.
        if (reading.Pose != Pose.OpenPalm) return null;
        if (_trail.Count(s => s.Open) * 2 < _trail.Count) return null;

        if (Math.Abs(dx) < _cfg.SwipeTravelPalms * reading.PalmSize) return null;
        // Mostly horizontal, or it is a hand being lowered rather than swept.
        if (Math.Abs(dy) > Math.Abs(dx)) return null;

        if (_swipeSpent) return null;
        _swipeSpent = true;

        // The same correction HandPoseReader applies to a pointing finger: with
        // the frame mirrored, image +x is already the viewer's own right.
        var right = _mirrored ? dx > 0 : dx < 0;
        return new Gesture(Pose.OpenPalm, right ? Direction.Right : Direction.Left, true);
    }

    /// <summary>
    /// The palm has stayed inside a small circle for <see cref="GestureConfig.SwipeRestSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Over a span of history rather than against the last reading, and that
    /// distinction is the whole test. Comparing consecutive readings measures
    /// speed per frame: at ten looks a second even a brisk sweep moves only about
    /// a third of a palm width between two of them, so every sweep would read as
    /// still and no swipe could ever start. Over a quarter of a second the same
    /// sweep has moved most of a palm width and a drift has not.
    ///
    /// The history requirement matters as much as the distance one, and it has
    /// two halves. Something older than the window has to exist, or a hand that
    /// has only just appeared passes trivially — one reading is always still. And
    /// something else *inside* the window has to exist, or a hand reappearing
    /// after a dropped frame or two passes just as trivially: everything recent
    /// is the current reading, which is always exactly where it is. That second
    /// case is the one that matters, because frames are dropped when the hand is
    /// moving fastest, and re-anchoring there restarts the measurement in the
    /// middle of the sweep it was meant to measure.
    /// </remarks>
    private bool AtRest(PoseReading reading, double now)
    {
        var from = now - _cfg.SwipeRestSeconds;
        if (_trail.Count == 0 || _trail[0].At > from) return false;

        var inside = 0;
        foreach (var s in _trail) if (s.At >= from) inside++;
        if (inside < 2) return false;

        var limit = _cfg.SwipeRestPalms * reading.PalmSize;
        return _trail.All(s => s.At < from || Distance(s.Centre, reading.Centre) <= limit);
    }

    private void Anchor(Point2f centre, double now)
    {
        _swipeAnchor = centre;
        _swipeAnchorAt = now;
        _swipeAnchorSet = true;
    }

    private void DropSwipe()
    {
        _swipeAnchorSet = false;
        _swipeSpent = false;
        _trail.Clear();
    }

    private static float Distance(Point2f a, Point2f b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
