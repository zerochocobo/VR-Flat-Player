namespace HeadTrackBridge.Mpv;

/// <summary>
/// Translates between the two axes users think in (geometry x stereo packing)
/// and mpv360's single flat projection enum.
///
/// The reason this class exists: every other VR player presents "180 / 360 /
/// fisheye" and "2D / 3D SBS / 3D TB" as independent choices, but mpv360
/// collapses them into one list where, say, index 2 means "180 AND
/// side-by-side". Mapping between the two is the only way to build a
/// conventional mode switcher on top of it.
///
/// Some combinations genuinely do not exist in the shader — those return null
/// so the UI can grey them out rather than silently picking something wrong.
/// </summary>
public static class ViewMode
{
    /// <summary>
    /// mpv360 projection for a geometry/stereo pair, or null if that
    /// combination has no shader support.
    /// <see cref="Geometry.Flat"/> always returns null: "flat" is not a
    /// projection, it means switching the 360 pipeline off entirely.
    /// </summary>
    public static Projection? ToProjection(Geometry geometry, Stereo stereo) => (geometry, stereo) switch
    {
        (Geometry.Deg360, Stereo.Mono) => Projection.Equirectangular,
        (Geometry.Deg360, Stereo.SideBySide) => Projection.DualHorizEquirectangular,  // our fork
        (Geometry.Deg360, Stereo.TopBottom) => Projection.DualVertEquirectangular,

        (Geometry.Deg180, Stereo.Mono) => Projection.HalfEquirectangular,
        (Geometry.Deg180, Stereo.SideBySide) => Projection.DualHalfEquirectangular,
        // No dual-vert half-equirect in the shader. Over/under VR180 is rare.
        (Geometry.Deg180, Stereo.TopBottom) => null,

        (Geometry.Fisheye, Stereo.Mono) => Projection.Fisheye,                        // our fork
        (Geometry.Fisheye, Stereo.SideBySide) => Projection.DualFisheye,
        (Geometry.Fisheye, Stereo.TopBottom) => null,

        (Geometry.Cylindrical, Stereo.Mono) => Projection.Cylindrical,
        (Geometry.Cylindrical, _) => null,

        (Geometry.Eac, Stereo.Mono) => Projection.EquiAngularCubemap,
        (Geometry.Eac, Stereo.SideBySide) => Projection.DualEquiAngularCubemap,
        (Geometry.Eac, Stereo.TopBottom) => null,

        (Geometry.Flat, _) => null,

        _ => null,
    };

    /// <summary>Inverse mapping, for showing the current state in the UI.</summary>
    public static (Geometry Geometry, Stereo Stereo) FromProjection(Projection p) => p switch
    {
        Projection.Equirectangular => (Geometry.Deg360, Stereo.Mono),
        Projection.DualHorizEquirectangular => (Geometry.Deg360, Stereo.SideBySide),
        Projection.DualVertEquirectangular => (Geometry.Deg360, Stereo.TopBottom),
        Projection.HalfEquirectangular => (Geometry.Deg180, Stereo.Mono),
        Projection.DualHalfEquirectangular => (Geometry.Deg180, Stereo.SideBySide),
        Projection.Fisheye => (Geometry.Fisheye, Stereo.Mono),
        Projection.DualFisheye => (Geometry.Fisheye, Stereo.SideBySide),
        Projection.Cylindrical => (Geometry.Cylindrical, Stereo.Mono),
        Projection.EquiAngularCubemap => (Geometry.Eac, Stereo.Mono),
        Projection.DualEquiAngularCubemap => (Geometry.Eac, Stereo.SideBySide),
        _ => (Geometry.Deg360, Stereo.Mono),
    };

    public static bool IsSupported(Geometry g, Stereo s) => g == Geometry.Flat || ToProjection(g, s) is not null;

    /// <summary>Short labels for the OSD menu, in display order.</summary>
    public static readonly (Geometry Value, string Label)[] Geometries =
    [
        (Geometry.Flat, "Flat"),
        (Geometry.Deg180, "180"),
        (Geometry.Deg360, "360"),
        (Geometry.Fisheye, "Fisheye"),
        (Geometry.Cylindrical, "Cylinder"),
        (Geometry.Eac, "EAC"),
    ];

    public static readonly (Stereo Value, string Label)[] Stereos =
    [
        (Stereo.Mono, "2D"),
        (Stereo.SideBySide, "3D SBS"),
        (Stereo.TopBottom, "3D TB"),
    ];

    public static string Label(Geometry g) => Geometries.First(x => x.Value == g).Label;
    public static string Label(Stereo s) => Stereos.First(x => x.Value == s).Label;

    // The labels above are the terse ones the on-video bar needs, in English.
    // A native menu has room for a real name in the user's own language, so it
    // looks these keys up in UiStrings instead. Kept here so that adding a
    // geometry cannot leave the menu silently untranslated.

    public static string Key(Geometry g) => g switch
    {
        Geometry.Flat => "geo.flat",
        Geometry.Deg180 => "geo.180",
        Geometry.Deg360 => "geo.360",
        Geometry.Fisheye => "geo.fisheye",
        Geometry.Cylindrical => "geo.cylindrical",
        Geometry.Eac => "geo.eac",
        _ => "geo.360",
    };

    public static string Key(Stereo s) => s switch
    {
        Stereo.SideBySide => "stereo.sbs",
        Stereo.TopBottom => "stereo.tb",
        _ => "stereo.mono",
    };

    public static string Key(Eye e) => e switch
    {
        Eye.Right => "eye.right",
        Eye.Both => "eye.both",
        _ => "eye.left",
    };
}
