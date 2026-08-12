using System.Globalization;

namespace HeadTrackBridge.Mpv;

/// <summary>
/// Single owner of "what mode is the player in": geometry, stereo packing, eye,
/// and whether the 360 pipeline is on at all.
///
/// Everything that can change these — the OSD menu, the WPF tuner, auto-detect
/// at file load — goes through here, so there is exactly one copy of the state
/// and one place that pushes it to mpv and broadcasts it back to the UI.
/// </summary>
/// <summary>
/// Where a mode change came from.
///
/// The distinction exists for the per-file mode memory. Auto-detection cannot
/// do better than a guess for the ambiguous 2:1 case, and a remembered mode
/// beats detection on the next open — so persisting a guess would freeze it in
/// place and put it beyond the reach of any later improvement to detection.
/// Only a choice the user actually made is worth writing down.
/// </summary>
public enum ModeOrigin
{
    /// <summary>The user picked this, from a menu, the mode panel or a hotkey.</summary>
    User,

    /// <summary>Auto-detected at file load, or restored from the memory itself.</summary>
    Automatic,
}

public sealed class PlayerModeController
{
    private readonly MpvIpcClient _ipc;
    private readonly MpvCameraDriver _driver;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// <paramref name="video"/> seeds the starting mode, and it has to: the
    /// state here is what the VR menu shows and what the mode bar highlights
    /// during the seconds between the window appearing and the first file
    /// reporting its dimensions.
    ///
    /// It used to start hardcoded at 360 mono while the configured fallback was
    /// VR180 side-by-side, so the player contradicted its own documented default
    /// for as long as that window lasted — and if a file never loaded, forever.
    /// </summary>
    public PlayerModeController(MpvIpcClient ipc, MpvCameraDriver driver, VideoConfig? video = null)
    {
        _ipc = ipc;
        _driver = driver;

        if (video is null) return;

        // The forced projection if there is one, otherwise the fallback --
        // exactly the two answers in ApplyLayoutAsync that do not depend on the
        // file, so this cannot disagree with what the file will resolve to for
        // any reason other than the file itself.
        var start = VideoFormat.Parse(video.Projection)
                    ?? VideoFormat.Parse(video.Fallback)
                    ?? Projection.DualHalfEquirectangular;
        (Geometry, Stereo) = ViewMode.FromProjection(start);
        Eye = video.Eye;
    }

    public Geometry Geometry { get; private set; } = Geometry.Deg360;
    public Stereo Stereo { get; private set; } = Stereo.Mono;
    public Eye Eye { get; private set; } = Eye.Left;

    /// <summary>
    /// Horizontal field of view in degrees.
    ///
    /// 80, which is a limit on visible distortion, not a headset spec. Copying a
    /// headset's number is specifically the wrong move here, and the reason is
    /// physical: in goggles the lenses map the panel back out to angles, so
    /// rendering 105 degrees and receiving 105 degrees at the eye cancels out
    /// and there is no distortion at all. A monitor has no such optics. It
    /// subtends 30 to 40 degrees at a desk, so whatever is rendered gets
    /// compressed into that, and the rectilinear stretch shows up raw.
    ///
    /// Under rectilinear projection an object at half-angle t is magnified
    /// 1/cos^2(t) across and 1/cos(t) up, so faces near the edge widen by the
    /// ratio, 1/cos(t):
    ///
    ///      70 deg FOV -> +22% at the edge, +-55 deg of pan on a 180 file
    ///      80 deg     -> +31%,             +-50
    ///      90 deg     -> +41%,             +-45
    ///     105 deg     -> +64%,             +-37.5
    ///     120 deg     -> +100%,            +-30      (mpv360's own 2.1 rad)
    ///
    /// 64% wider is what "the people look distorted" was. The panorama
    /// literature draws the same line: rectilinear is fine below 90 and severe
    /// past 120, and lens designers call 40-60 normal perspective.
    ///
    /// 80 over 70 because the +31% is only at the extreme edge — halfway out it
    /// is 5% — and 70 starts to feel like a keyhole. Both are defensible; wider
    /// than 90 is not.
    /// </summary>
    public double FovDegrees { get; private set; } = DefaultFovDegrees;

    /// <summary>
    /// The starting field of view, and what "reset" returns to.
    ///
    /// Named because it had been written out twice: the menu's reset put the
    /// picture back to a hardcoded 120 long after the default became 80, so the
    /// one control whose whole job is "put it back" moved it somewhere the
    /// player never starts.
    /// </summary>
    public const double DefaultFovDegrees = 80;

    /// <summary>
    /// One step of the field of view, for every control that offers one.
    ///
    /// Named for the same reason the default above is: there are now four ways
    /// to change it — the wheel, Ctrl+Shift+Up/Down, the VR menu and a thumb
    /// held up at the camera — and three of them are in different files. The
    /// fourth, mpv's own binding, is in input.conf and has to be kept in step by
    /// hand, because mpv cannot read a C# constant.
    /// </summary>
    public const double FovStepDegrees = 5;

    public async Task SetFovAsync(double degrees, ModeOrigin origin = ModeOrigin.User)
    {
        // Down to 10 degrees, not 40. Narrowing the field of view is how you
        // lean in on something far away in the frame, and 40 stopped that well
        // before it stopped being useful — the log of a real session shows the
        // value pinned at 40 with the user still asking for less.
        //
        // The ceiling stays at 160. Past that a rectilinear render stretches
        // the edges beyond any use (at 160 an object at the edge is already
        // eleven times too wide), and 180 is a mathematical singularity.
        FovDegrees = Math.Clamp(degrees, 10, 160);
        await _driver.SetFovAsync(FovDegrees).ConfigureAwait(false);
        await BroadcastAsync().ConfigureAwait(false);
        Changed?.Invoke(Describe(), origin);

        // Say the number, once, wherever the change came from.
        //
        // There are four ways to change this — the wheel, Ctrl+Shift+Up/Down,
        // the VR menu and a thumb held at the camera — and until now none of
        // them put anything on screen. The clamp above is exactly why that
        // matters: at 10 or at 160 the picture stops responding, and with no
        // number there is nothing to distinguish "as far as it goes" from
        // "the control has stopped working".
        //
        // Announced here rather than at each of the four call sites, which is
        // also what keeps the gesture from toasting twice.
        if (origin == ModeOrigin.User)
            await _ipc.ShowTextAsync(
                UiStrings.Current.F("osd.fov",
                    FovDegrees.ToString("F0", CultureInfo.CurrentCulture)),
                1200).ConfigureAwait(false);
    }

    public Task AdjustFovAsync(double deltaDegrees) => SetFovAsync(FovDegrees + deltaDegrees);

    /// <summary>Raised after any successful change, with a human-readable summary.</summary>
    public event Action<string, ModeOrigin>? Changed;

    /// <summary>
    /// The mode as a line of text for the OSD. Localised through the same table
    /// as the menu bar, using the menu's own names rather than ViewMode's terse
    /// English labels — the toast has room, and a Chinese menu that announces
    /// "180 · 3D SBS / Left" in English is exactly the half-translated state
    /// this player used to be in.
    /// </summary>
    public string Describe()
    {
        var t = UiStrings.Current;
        if (Geometry == Geometry.Flat) return t["mode.flatOff"];

        var stereoPart = Stereo == Stereo.Mono
            ? t[ViewMode.Key(Stereo.Mono)]
            : $"{t[ViewMode.Key(Stereo)]} / {t[ViewMode.Key(Eye)]}";
        return $"{t[ViewMode.Key(Geometry)]} · {stereoPart} · {FovDegrees.ToString("F0", CultureInfo.CurrentCulture)}°";
    }

    public async Task SetAsync(Geometry geometry, Stereo stereo, Eye eye,
                               ModeOrigin origin = ModeOrigin.User)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (geometry != Geometry.Flat && ViewMode.ToProjection(geometry, stereo) is null)
            {
                // Fall back to a packing that exists rather than refusing outright:
                // the user asked for a geometry, give them that geometry.
                var fallback = ViewMode.Stereos.FirstOrDefault(s => ViewMode.IsSupported(geometry, s.Value));
                var t = UiStrings.Current;
                await _ipc.ShowTextAsync(
                    t.F("osd.comboUnsupported", t[ViewMode.Key(geometry)], t[ViewMode.Key(stereo)],
                        t[ViewMode.Key(fallback.Value)]), 2200);
                stereo = fallback.Value;
            }

            Geometry = geometry;
            Stereo = stereo;
            Eye = eye;

            var wantShader = geometry != Geometry.Flat;
            await SetShaderEnabledAsync(wantShader).ConfigureAwait(false);
            await SetKeepAspectAsync(!wantShader).ConfigureAwait(false);

            if (wantShader && ViewMode.ToProjection(geometry, stereo) is { } projection)
                await _driver.SetLayoutAsync(projection, eye).ConfigureAwait(false);
        }
        finally { _gate.Release(); }

        await BroadcastAsync().ConfigureAwait(false);
        Changed?.Invoke(Describe(), origin);
    }

    public Task SetGeometryAsync(Geometry g) => SetAsync(g, Stereo, PickEye(Stereo, Eye));

    /// <summary>
    /// Switching to a stereo packing while the eye is "Both" would render both
    /// eyes side by side on screen — correct for a headset, wrong for the flat
    /// monitor this player targets. Fall to Left, which is what the viewer
    /// almost certainly means.
    /// </summary>
    public Task SetStereoAsync(Stereo s) => SetAsync(Geometry, s, PickEye(s, Eye));

    public Task SetEyeAsync(Eye e) => SetAsync(Geometry, Stereo, e);

    private static Eye PickEye(Stereo stereo, Eye current) =>
        stereo != Stereo.Mono && current == Eye.Both ? Eye.Left : current;

    public Task CycleGeometryAsync() =>
        SetGeometryAsync(Next(ViewMode.Geometries.Select(x => x.Value).ToArray(), Geometry));

    public Task CycleStereoAsync()
    {
        // Skip packings this geometry cannot do, so cycling never lands on a dead entry.
        var options = ViewMode.Stereos.Select(x => x.Value).Where(s => ViewMode.IsSupported(Geometry, s)).ToArray();
        return options.Length == 0 ? Task.CompletedTask : SetStereoAsync(Next(options, Stereo));
    }

    public Task CycleEyeAsync() => SetEyeAsync(Next([Eye.Left, Eye.Right, Eye.Both], Eye));

    private static T Next<T>(T[] values, T current) where T : struct
    {
        var i = Array.IndexOf(values, current);
        return values[(i + 1 + values.Length) % values.Length];
    }

    /// <summary>
    /// mpv360 exposes only a *toggle*, not a setter, so read the actual shader
    /// list first instead of trusting a cached flag — otherwise pressing Ctrl+e
    /// in the mpv window would desync us and every later toggle would be inverted.
    /// </summary>
    /// <summary>
    /// Let the render fill the window while the 360 shader is on, and letterbox
    /// normally when it is off.
    /// </summary>
    /// <remarks>
    /// A file's aspect ratio stops meaning anything the moment the shader runs.
    /// The shader is not showing you the file: it samples a sphere and draws a
    /// rectilinear view of it at OUTPUT size, so the picture's shape is the
    /// *window's* shape, and render() already takes its frustum aspect from
    /// target_size to match. mpv does not know that. It still holds the source
    /// display aspect — 2:1 for a VR180 file — and with keepaspect on it fits
    /// the shader's output into a 2:1 box inside the window.
    ///
    /// That scaling is pure distortion, because the thing being scaled was
    /// already correct. It went unnoticed while the default window was 1100x660,
    /// only 17% off 2:1. A square window makes the same error 2x: people come
    /// out flattened, and dragging the window to a new shape squashes and
    /// stretches them without changing what is in frame — which is the giveaway,
    /// since a real field-of-view change would reframe rather than distort.
    ///
    /// Off only for the shader. Plain 2D video must still letterbox: there the
    /// file's aspect ratio is the whole truth about its shape.
    /// </remarks>
    private Task SetKeepAspectAsync(bool keep) => _ipc.SetPropertyAsync("keepaspect", keep);

    /// <summary>
    /// Turn the 360 shader on or off, and check that it actually happened.
    /// </summary>
    /// <remarks>
    /// mpv360/toggle is a *relative* operation driven by mpv360.lua's own
    /// `enabled` flag, and we cannot read that flag — we can only read
    /// glsl-shaders, which is the result. The two disagree at startup: the
    /// script's conf says enabled=yes, but with no file open there is nothing to
    /// hook, so glsl-shaders is empty. We read "off", ask for "on", and the
    /// toggle flips the script's flag from yes to no — leaving the shader off
    /// while this class believes it is on.
    ///
    /// That is the "the menu says 180 3D but the picture is the raw two-eye
    /// frame" report, and it only happens when the player is started with no
    /// file, because opening one on the command line applies the shader before
    /// this ever runs.
    ///
    /// So: verify, and flip again if the toggle went the wrong way. One retry,
    /// not a loop — if two toggles cannot reach the wanted state, something
    /// other than a stale flag is wrong and spinning would only hide it.
    /// </remarks>
    private async Task SetShaderEnabledAsync(bool enabled)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var loaded = await IsShaderLoadedAsync().ConfigureAwait(false);
            if (loaded is null || loaded == enabled) return;

            await _ipc.SendAsync("script-binding", "mpv360/toggle").ConfigureAwait(false);
            await Task.Delay(80).ConfigureAwait(false);   // let the script rebuild the shader list
        }

        if (await IsShaderLoadedAsync().ConfigureAwait(false) is { } got && got != enabled)
            Console.WriteLine($"\n  [mode] the 360 shader would not turn {(enabled ? "on" : "off")}");
    }

    private async Task<bool?> IsShaderLoadedAsync()
    {
        var v = await _ipc.GetPropertyAsync("glsl-shaders").ConfigureAwait(false);
        if (v is not { ValueKind: System.Text.Json.JsonValueKind.Array } arr) return null;
        foreach (var item in arr.EnumerateArray())
            if (item.GetString()?.Contains("mpv360", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    /// <summary>Push current state to the OSD menu script so its highlights match reality.</summary>
    public Task BroadcastAsync() => _ipc.SendAsync(
        "script-message-to", "vrmenu", "vr-state",
        Geometry.ToString(),
        Stereo.ToString(),
        Eye.ToString(),
        string.Join(",", ViewMode.Stereos
            .Where(s => ViewMode.IsSupported(Geometry, s.Value))
            .Select(s => s.Value.ToString())),
        FovDegrees.ToString("F0", CultureInfo.InvariantCulture));

    /// <summary>Parse helpers for values arriving as strings over script-message.</summary>
    public static Geometry? ParseGeometry(string s) =>
        Enum.TryParse<Geometry>(s, true, out var g) ? g : null;

    public static Stereo? ParseStereo(string s) =>
        Enum.TryParse<Stereo>(s, true, out var v) ? v : null;

    public static Eye? ParseEye(string s) =>
        Enum.TryParse<Eye>(s, true, out var v) ? v : null;

    public static string Deg(double radians) => (radians * 180 / Math.PI).ToString("F0", CultureInfo.InvariantCulture);
}
