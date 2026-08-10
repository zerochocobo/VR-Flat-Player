using System.Text.RegularExpressions;

namespace HeadTrackBridge.Mpv;

/// <summary>mpv360's input_projection enum. Order matches projection_names in mpv360.lua.</summary>
public enum Projection
{
    Equirectangular = 0,
    DualFisheye = 1,
    DualHalfEquirectangular = 2,
    HalfEquirectangular = 3,
    DualVertEquirectangular = 4,
    Cylindrical = 5,
    EquiAngularCubemap = 6,
    DualEquiAngularCubemap = 7,

    /// <summary>Added by our shader fork: stereo 360 packed side by side (4:1).</summary>
    DualHorizEquirectangular = 8,

    /// <summary>Added by our shader fork: a single mono fisheye circle.</summary>
    Fisheye = 9,
}

/// <summary>
/// What shape the source covers. This is the axis users actually think in —
/// "is this a 180 or a 360 file" — and it is deliberately separate from how the
/// two eyes are packed.
/// </summary>
public enum Geometry { Flat, Deg180, Deg360, Fisheye, Cylindrical, Eac }

/// <summary>How the two eyes are packed into the frame, if at all.</summary>
public enum Stereo { Mono, SideBySide, TopBottom }

/// <summary>mpv360's eye enum.</summary>
public enum Eye { Left = 0, Right = 1, Both = 2 }

/// <summary>How much the detector trusts its own answer.</summary>
public enum DetectionSource
{
    /// <summary>A recognised naming convention was found. Trust it.</summary>
    Filename,

    /// <summary>The frame's aspect ratio can only mean one thing. Trust it.</summary>
    Shape,

    /// <summary>
    /// Nothing decided it. The caller should fall back to the last mode the
    /// user chose, and then to the configured default — never to a guess
    /// dressed up as an answer.
    /// </summary>
    Ambiguous,
}

public readonly record struct VideoLayout(
    Geometry Geometry, Stereo Stereo, Eye Eye, string Reason, DetectionSource Source)
{
    public bool IsConfident => Source != DetectionSource.Ambiguous;
}

/// <summary>
/// Works out how a file is laid out from its name and its shape.
///
/// The hard case, and the reason this can never be fully automatic: a mono 360
/// equirectangular file and a side-by-side VR180 file are BOTH 2:1. A 7680x3840
/// file is either "the whole sphere" or "two 180-degree eyes side by side", and
/// nothing in the video stream says which. So the filename decides when it can,
/// the aspect ratio decides when it is unambiguous, and otherwise this reports
/// <see cref="DetectionSource.Ambiguous"/> and lets the caller reuse whatever
/// the user last chose. Guessing 360 in that case — which this used to do — is
/// wrong about half the time and looks like a broken player.
///
/// The naming conventions are merged from the three players that established
/// them: DeoVR, HereSphere and SKYBOX. Where they disagree, see the notes on
/// the individual patterns.
/// </summary>
public static class VideoFormat
{
    /// <summary>
    /// A token has to stand alone. HereSphere requires a suffix to start with
    /// an underscore, hyphen or space; requiring a non-alphanumeric boundary on
    /// both sides is the same idea and also stops "1800" or "x360p" from
    /// counting. The cost is that every variant has to be spelled out —
    /// "hsbs" does not match a bare "sbs" pattern — which is why the lists
    /// below are long and literal rather than clever.
    /// </summary>
    private static Regex Token(string alternatives) =>
        new($@"(?<![A-Za-z0-9])(?:{alternatives})(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---------------------------------------------------------- projection ---
    // Ordered most specific first, because the boundary rule alone does not
    // separate "eac360" from "360".

    private static readonly Regex Eac = Token(@"eac360|360eac|eac");

    /// <summary>
    /// Fisheye lens conventions. The number is the LENS field of view, not the
    /// viewing FOV — MKX200 is a 200-degree lens. We deliberately do not feed it
    /// to mpv360's `fov`, which means something else entirely (how much of the
    /// sphere fills the window); doing so would zoom the picture rather than
    /// correct its geometry.
    /// </summary>
    private static readonly Regex FisheyeLens = Token(@"mkx200|mkx22|vrca220|fisheye190|rf52|f180|180f");

    private static readonly Regex FisheyeGeneric = Token(@"fisheye|dual.?fisheye");

    /// <summary>
    /// SKYBOX documents "F180"/"VR180" as fisheye. Everyone else, and Google's
    /// VR180 spec that named the format, means equirectangular 180 side-by-side
    /// — and that is what the overwhelming majority of files tagged vr180
    /// actually are. F180 stays fisheye (all three agree); VR180 goes here.
    /// </summary>
    private static readonly Regex Deg180 = Token(@"vr180|180x180|180sbs|half.?equirect|180");

    private static readonly Regex Deg360 = Token(@"360|equirect|equirectangular");

    /// <summary>Not in any of the three conventions; ours, because mpv360 supports it.</summary>
    private static readonly Regex Cylindrical = Token(@"cylindrical|cylinder");

    // -------------------------------------------------------------- stereo ---

    private static readonly Regex SideBySide =
        Token(@"sbs|hsbs|lr|lrf|3dh|side.?by.?side|half.?sbs|half.?side.?by.?side|left.?right|half.?lr");

    /// <summary>Right eye first. The halves are the other way round, so the eye selection flips.</summary>
    private static readonly Regex RightLeft = Token(@"rl|rlf|right.?left");

    private static readonly Regex TopBottom =
        Token(@"tb|htb|tbf|ou|hou|3dv|over.?under|top.?bottom|half.?ou|half.?over.?under|half.?tb");

    /// <summary>Bottom eye first — the vertical equivalent of RL.</summary>
    private static readonly Regex BottomTop = Token(@"bt|btf|bottom.?top");

    private static readonly Regex MonoTag = Token(@"2d|mono");

    // Kept last: HereSphere uses a bare "_3D" for left-right. Checked after the
    // specific ones so 3dh/3dv are not swallowed by it.
    private static readonly Regex Bare3D = Token(@"3d");

    public static VideoLayout Detect(int width, int height, string? path)
    {
        var name = string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);
        var aspect = height > 0 ? width / (double)height : 0;
        var shape = $"{width}x{height}";

        var geometry = MatchGeometry(name, out var geometryToken);
        var stereo = MatchStereo(name, out var swapped, out var stereoToken);

        // ---- both halves of the name agree on something -------------------
        if (geometry is { } g && stereo is { } s)
            return Make(g, s, swapped, DetectionSource.Filename,
                        $"filename says {geometryToken} + {stereoToken}, {shape}");

        // ---- a projection tag, but nothing about the eyes -----------------
        if (geometry is { } g2)
        {
            // HereSphere's rule: "_180"/"_360" with no stereo suffix means mono.
            // The fisheye lens tags are the documented exception — MKX200 and
            // friends are always shot side-by-side.
            var implied = FisheyeLens.IsMatch(name) ? Stereo.SideBySide : Stereo.Mono;
            return Make(g2, implied, swapped: false, DetectionSource.Filename,
                        $"filename says {geometryToken}, no stereo tag -> {(implied == Stereo.Mono ? "mono" : "side by side")}, {shape}");
        }

        // ---- eyes tagged, but no projection -------------------------------
        // The other players call this a flat 3D movie. That is right for a
        // 3.55:1 file (two 16:9 frames) and wrong for a 2:1 one (two square
        // frames, which is exactly what VR180 SBS looks like), so the aspect
        // ratio decides instead of a blanket rule.
        if (stereo is { } s2 && s2 != Stereo.Mono)
        {
            if (s2 == Stereo.SideBySide)
            {
                if (Near(aspect, 2.0)) return Make(Geometry.Deg180, s2, swapped, DetectionSource.Filename,
                    $"filename says {stereoToken}; {shape} is two square eyes -> VR180");
                if (Near(aspect, 32.0 / 9.0, 0.25)) return Make(Geometry.Flat, s2, swapped, DetectionSource.Filename,
                    $"filename says {stereoToken}; {shape} is two 16:9 frames -> flat 3D, not VR");
                if (Near(aspect, 4.0, 0.08)) return Make(Geometry.Deg360, s2, swapped, DetectionSource.Filename,
                    $"filename says {stereoToken}; {shape} is two 2:1 eyes -> stereo 360");
            }
            else
            {
                if (Near(aspect, 1.0)) return Make(Geometry.Deg360, s2, swapped, DetectionSource.Filename,
                    $"filename says {stereoToken}; {shape} is two 2:1 eyes stacked -> stereo 360");
                if (Near(aspect, 0.5)) return Make(Geometry.Deg180, s2, swapped, DetectionSource.Filename,
                    $"filename says {stereoToken}; {shape} is two square eyes stacked -> VR180");
                if (Near(aspect, 8.0 / 9.0, 0.08)) return Make(Geometry.Flat, s2, swapped, DetectionSource.Filename,
                    $"filename says {stereoToken}; {shape} is two 16:9 frames -> flat 3D, not VR");
            }
        }

        if (stereo == Stereo.Mono && Near(aspect, 2.0))
            return Make(Geometry.Deg360, Stereo.Mono, false, DetectionSource.Filename,
                        $"filename says {stereoToken}, {shape} -> mono 360");

        // ---- nothing in the name; only unambiguous shapes count -----------
        if (Near(aspect, 4.0, 0.08))
            return Make(Geometry.Deg360, Stereo.SideBySide, false, DetectionSource.Shape,
                        $"{shape} is 4:1 -> stereo 360 side by side");

        if (Near(aspect, 0.5))
            return Make(Geometry.Deg360, Stereo.TopBottom, false, DetectionSource.Shape,
                        $"{shape} is 1:2 -> stereo 360 over/under");

        if (Near(aspect, 1.0))
            return Make(Geometry.Deg180, Stereo.Mono, false, DetectionSource.Shape,
                        $"{shape} is square -> mono 180");

        // 2:1 is the one shape that genuinely cannot be resolved from the frame:
        // a mono 360 sphere and two square VR180 eyes produce exactly the same
        // rectangle. Only here does the caller need to fall back to what the
        // user chose last.
        if (Near(aspect, 2.0))
            return new VideoLayout(Geometry.Deg180, Stereo.SideBySide, Eye.Left,
                $"{shape} with no recognised tag in the filename — 2:1 is equally a mono 360 " +
                "and a VR180 side-by-side, so nothing here can tell them apart",
                DetectionSource.Ambiguous);

        // Every other shape with nothing in the name is an ordinary video: 16:9,
        // 4:3, 2.39:1 cinema, 9:16 phone footage. None of those is a VR layout,
        // and this is still a video player — wrapping a normal film onto a
        // sphere because the last VR file needed it would be absurd.
        return Make(Geometry.Flat, Stereo.Mono, false, DetectionSource.Shape,
                    $"{shape} is not a VR aspect ratio and the filename says nothing -> plain 2D");
    }

    private static VideoLayout Make(Geometry g, Stereo s, bool swapped, DetectionSource source, string reason)
    {
        // A stereo file has to show one eye on a flat monitor. "Right-left" and
        // "bottom-top" put the left eye in the second half, so selecting the
        // right half is what actually shows the left eye.
        var eye = s == Stereo.Mono ? Eye.Both : swapped ? Eye.Right : Eye.Left;
        return new VideoLayout(g, s, eye, reason, source);
    }

    private static bool Near(double value, double target, double tolerance = 0.02) =>
        Math.Abs(value - target) < tolerance;

    private static Geometry? MatchGeometry(string name, out string token)
    {
        token = "";
        if (Eac.IsMatch(name)) { token = "EAC"; return Geometry.Eac; }
        if (FisheyeLens.IsMatch(name)) { token = FisheyeLens.Match(name).Value; return Geometry.Fisheye; }
        if (FisheyeGeneric.IsMatch(name)) { token = "fisheye"; return Geometry.Fisheye; }
        if (Cylindrical.IsMatch(name)) { token = "cylindrical"; return Geometry.Cylindrical; }
        if (Deg180.IsMatch(name)) { token = Deg180.Match(name).Value; return Geometry.Deg180; }
        if (Deg360.IsMatch(name)) { token = Deg360.Match(name).Value; return Geometry.Deg360; }
        return null;
    }

    private static Stereo? MatchStereo(string name, out bool swapped, out string token)
    {
        swapped = false;
        token = "";

        if (RightLeft.IsMatch(name)) { swapped = true; token = RightLeft.Match(name).Value; return Stereo.SideBySide; }
        if (BottomTop.IsMatch(name)) { swapped = true; token = BottomTop.Match(name).Value; return Stereo.TopBottom; }
        if (SideBySide.IsMatch(name)) { token = SideBySide.Match(name).Value; return Stereo.SideBySide; }
        if (TopBottom.IsMatch(name)) { token = TopBottom.Match(name).Value; return Stereo.TopBottom; }
        if (MonoTag.IsMatch(name)) { token = MonoTag.Match(name).Value; return Stereo.Mono; }
        if (Bare3D.IsMatch(name)) { token = "3D"; return Stereo.SideBySide; }
        return null;
    }

    /// <summary>Parses a user-supplied projection name or index. Returns null for "auto".</summary>
    public static Projection? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (int.TryParse(s, out var i) && Enum.IsDefined(typeof(Projection), i)) return (Projection)i;

        var key = s.Replace("-", "").Replace("_", "").Replace(" ", "");
        foreach (var p in Enum.GetValues<Projection>())
            if (p.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)) return p;

        // Friendly aliases for the names people actually type.
        return key.ToLowerInvariant() switch
        {
            "equirect" or "360" or "mono" or "mono360" => Projection.Equirectangular,
            "360sbs" or "3603d" => Projection.DualHorizEquirectangular,
            "360tb" => Projection.DualVertEquirectangular,
            "dualhalfequirect" or "vr180" or "180" or "sbs180" or "1803d" => Projection.DualHalfEquirectangular,
            "halfequirect" or "180mono" => Projection.HalfEquirectangular,
            "fisheye" or "monofisheye" => Projection.Fisheye,
            "dualfisheye" or "fisheyesbs" => Projection.DualFisheye,
            "eac" => Projection.EquiAngularCubemap,
            "eacsbs" => Projection.DualEquiAngularCubemap,
            "cylinder" or "cylindrical" => Projection.Cylindrical,
            _ => throw new ArgumentException(
                $"Unknown projection '{s}'. Valid: {string.Join(", ", Enum.GetNames<Projection>())} (or auto)."),
        };
    }
}
