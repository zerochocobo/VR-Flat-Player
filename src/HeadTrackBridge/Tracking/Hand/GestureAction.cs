namespace HeadTrackBridge.Tracking.Hand;

/// <summary>What a recognised gesture asks the player to do.</summary>
/// <remarks>
/// An enum rather than a delegate or an mpv command string, because the same
/// gesture means different things in a VR file and a flat one, and the mapping
/// wants to be a table that can be read at a glance and tested without a player
/// attached. <see cref="PlayerSession"/> is the only place that knows how each
/// of these reaches mpv.
/// </remarks>
public enum GestureAction
{
    None,
    PlayPause,
    SeekBack,
    SeekForward,
    VolumeUp,
    VolumeDown,
    FovNarrower,
    FovWider,
    PreviousFile,
    NextFile,
}

/// <summary>One recognised gesture: a shape, where it aimed, and whether it moved.</summary>
public readonly record struct Gesture(Pose Pose, Direction Direction, bool Swipe)
{
    public static readonly Gesture None = new(Pose.None, Direction.None, false);
    public bool IsNone => Pose == Pose.None;
}

/// <summary>
/// Which gesture does what, in a flat file and in a VR one.
/// </summary>
/// <remarks>
/// Five shapes, nine actions, and the vocabulary is small on purpose: every
/// gesture bound here is one that can misfire, so each has to earn its place by
/// being wanted often enough to be worth that risk.
///
///     pose              direction   flat file          VR file
///     ---------------------------------------------------------------------
///     open palm, still  -           enter / leave gesture mode
///     open palm         swipe L/R   previous / next file
///     fist              -           play / pause
///     index finger      left/right  back 10 s / forward 30 s
///     thumb             up/down     volume +5 / -5     narrower / wider view
///
/// Only the thumb differs between the two, and it differs because a flat file
/// has no field of view to adjust and a VR file's is the thing most often
/// reached for. Everything else means the same in both, which is what keeps
/// there being two profiles from turning into two vocabularies to learn.
///
/// Nothing here is destructive and nothing takes more than a second to undo,
/// which is the bar a gesture-triggered action has to clear. Skipping to the
/// next file does not obviously clear it — but it is on a swipe, the one motion
/// nobody makes by accident while sitting still, and it is only reachable inside
/// the armed window.
/// </remarks>
public static class GestureMap
{
    /// <summary>The action a gesture asks for, or None if it asks for nothing here.</summary>
    /// <param name="vr">True when the file is being shown through the VR pipeline.</param>
    public static GestureAction Resolve(Gesture g, bool vr) => (g.Pose, g.Direction, g.Swipe) switch
    {
        (Pose.OpenPalm, Direction.Left, true) => GestureAction.PreviousFile,
        (Pose.OpenPalm, Direction.Right, true) => GestureAction.NextFile,

        (Pose.Fist, _, false) => GestureAction.PlayPause,

        (Pose.Point, Direction.Left, false) => GestureAction.SeekBack,
        (Pose.Point, Direction.Right, false) => GestureAction.SeekForward,

        // Up is "more" in both profiles, and for the view that means narrower:
        // the same direction the wheel and Ctrl+Shift+Up already zoom in. Two
        // conventions for one axis would be worse than either.
        (Pose.Thumb, Direction.Up, false) => vr ? GestureAction.FovNarrower : GestureAction.VolumeUp,
        (Pose.Thumb, Direction.Down, false) => vr ? GestureAction.FovWider : GestureAction.VolumeDown,

        _ => GestureAction.None,
    };

    /// <summary>
    /// Whether holding the gesture keeps the action coming.
    /// </summary>
    /// <remarks>
    /// The adjustments repeat; the decisions do not. Volume, field of view and
    /// seeking are all things you want several of and would rather ask for by
    /// holding still than by making the same shape six times. Play/pause and
    /// changing file are answers, not amounts — and a fist resting in view that
    /// repeated would toggle pause ten times a second.
    ///
    /// Seeking was in the second group at first, and that was wrong. It reads as
    /// a decision because a menu entry called "Forward 30 s" is one, but the
    /// gesture is a finger held out at the screen, and holding it out is the
    /// obvious way to ask for more. Reported after ten minutes of use as
    /// "前进和后退没有持续性" — no continuity — which is exactly right: every
    /// other held pose kept going and this one stopped after one step.
    /// </remarks>
    public static bool Repeats(GestureAction action) => action is
        GestureAction.VolumeUp or GestureAction.VolumeDown or
        GestureAction.FovNarrower or GestureAction.FovWider or
        GestureAction.SeekBack or GestureAction.SeekForward;

    /// <summary>
    /// Whether the action moves the playhead, and so paces itself separately.
    /// </summary>
    /// <remarks>
    /// A repeat interval is only half of a rate; the other half is the step, and
    /// seeking has by far the largest one. Volume moves 5 units and the view 5
    /// degrees — both small enough that ten a second is a smooth adjustment — but
    /// a seek moves ten seconds of film, and at the same interval it runs the
    /// picture past faster than anyone can read it. The first build repeated a
    /// 30 s step every 0.4 s — 75 seconds of film for every second the finger was
    /// held out, which was reported as too fast to aim with.
    /// </remarks>
    public static bool IsSeek(GestureAction action) => action is
        GestureAction.SeekBack or GestureAction.SeekForward;

    /// <summary>UiStrings key for the toast shown when the action fires.</summary>
    public static string Key(GestureAction action) => "gesture." + action switch
    {
        GestureAction.PlayPause => "playPause",
        GestureAction.SeekBack => "seekBack",
        GestureAction.SeekForward => "seekForward",
        GestureAction.VolumeUp => "volumeUp",
        GestureAction.VolumeDown => "volumeDown",
        GestureAction.FovNarrower => "fovNarrower",
        GestureAction.FovWider => "fovWider",
        GestureAction.PreviousFile => "prevFile",
        GestureAction.NextFile => "nextFile",
        _ => "none",
    };
}
