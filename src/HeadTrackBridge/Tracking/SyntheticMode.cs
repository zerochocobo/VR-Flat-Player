namespace HeadTrackBridge.Tracking;

/// <summary>
/// Which fake head motion a synthetic source produces.
///
/// In its own file rather than beside SyntheticSource: BridgeConfig exposes it,
/// so it is part of the configuration surface, while SyntheticSource is a
/// Windows-only class full of P/Invoke. Keeping them together forced anything
/// that merely wanted to read the config — the unit tests, for one — to drag in
/// user32 as well.
/// </summary>
public enum SyntheticMode
{
    /// <summary>Slow yaw/pitch sweep plus jitter — exercises the filter unattended.</summary>
    Sweep,

    /// <summary>Mouse cursor position stands in for head position. Best for hand-tuning gain.</summary>
    Mouse,

    /// <summary>Dead centre plus jitter only — for checking that the deadzone holds still.</summary>
    Still,
}
