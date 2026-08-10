using System.Globalization;
using HeadTrackBridge.Mapping;

namespace HeadTrackBridge.Mpv;

/// <summary>
/// Drives the mpv360 camera.
///
/// It bypasses mpv360's Lua command table on purpose. That table only exposes
/// *relative* steps (look-left, look-up, ...), which is useless for absolute
/// head tracking. But the script ultimately pushes its state into mpv with:
///
///     change-list glsl-shader-opts add "mpv360/yaw=..,mpv360/pitch=.."
///
/// so we write the same property directly and get absolute control with no fork
/// of mpv360 required. The trade-off: mpv360's own key bindings (Ctrl+arrows,
/// mouse-look) still hold their own copy of the state, so if you use them while
/// the bridge is running the two will fight. Treat the bridge as the owner of
/// yaw/pitch/roll and leave FOV, projection and eye selection to mpv360.
/// </summary>
public sealed class MpvCameraDriver
{
    private readonly MpvIpcClient _ipc;
    private readonly MpvConfig _cfg;
    private ViewAngles _lastSent;
    private bool _everSent;

    public MpvCameraDriver(MpvIpcClient ipc, MpvConfig cfg)
    {
        _ipc = ipc;
        _cfg = cfg;
    }

    public long WriteCount { get; private set; }

    /// <summary>
    /// Sets projection and eye once, rather than in the per-frame write.
    /// Deliberate: if these went out 120 times a second they would stomp
    /// mpv360's own Ctrl+Shift+p / Ctrl+Shift+e cycling the instant you pressed
    /// it. Written once, the user can still override interactively.
    /// </summary>
    public Task SetLayoutAsync(Projection projection, Eye eye) =>
        _ipc.SendAsync("no-osd", "change-list", "glsl-shader-opts", "add",
            $"mpv360/input_projection={(int)projection},mpv360/eye={(int)eye}");

    /// <summary>Field of view, in degrees. mpv360 stores it in radians.</summary>
    public Task SetFovAsync(double degrees)
    {
        var radians = Math.Clamp(degrees, 20, 170) * Math.PI / 180.0;
        return _ipc.SendAsync("no-osd", "change-list", "glsl-shader-opts", "add",
            string.Create(CultureInfo.InvariantCulture, $"mpv360/fov={radians:F5}"));
    }

    public async Task PushAsync(ViewAngles v)
    {
        if (_everSent && !Changed(v, _lastSent, _cfg.MinDeltaDegrees)) return;

        var scale = _cfg.AnglesInRadians ? Math.PI / 180.0 : 1.0;
        var opts = string.Create(CultureInfo.InvariantCulture,
            $"mpv360/yaw={v.YawDegrees * scale:F5},mpv360/pitch={v.PitchDegrees * scale:F5},mpv360/roll={v.RollDegrees * scale:F5}");

        // "no-osd" matters: mpv360 uses it too, and without it every one of these
        // 120-per-second writes would print an OSD line.
        await _ipc.SendAsync("no-osd", "change-list", "glsl-shader-opts", "add", opts).ConfigureAwait(false);

        _lastSent = v;
        _everSent = true;
        WriteCount++;
    }

    private static bool Changed(ViewAngles a, ViewAngles b, double eps) =>
        Math.Abs(a.YawDegrees - b.YawDegrees) > eps ||
        Math.Abs(a.PitchDegrees - b.PitchDegrees) > eps ||
        Math.Abs(a.RollDegrees - b.RollDegrees) > eps;
}
