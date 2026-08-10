namespace HeadTrackBridge.Mpv;

/// <summary>
/// The decoder and renderer choices offered in the menu.
///
/// One list, used both to build the menu and to validate what comes back from
/// the config file, so a value that cannot be selected can also never be
/// written. Every entry here was checked against the bundled mpv's
/// <c>--hwdec=help</c> / <c>--vo=help</c> / <c>--gpu-api=help</c>; offering a
/// backend this build does not have would look like a broken menu item.
/// </summary>
public static class VideoBackends
{
    /// <param name="MpvValue">
    /// What to write to mpv's <c>hwdec</c> property. Empty means "do not pass
    /// --hwdec at all", leaving mpv.conf's tuned value in charge.
    /// </param>
    /// <param name="RequiresApi">
    /// GPU APIs this decoder can hand frames to, comma-separated; empty means
    /// any. Hardware decoding and rendering are not independent choices — the
    /// decoded frame lives in a GPU texture that the renderer has to be able to
    /// import, and if it cannot, mpv falls back to software without saying so.
    /// </param>
    public readonly record struct Decoder(string Key, string MpvValue, string RequiresApi = "");

    /// <summary>
    /// Ordered best-first, and the first entry is the project's own default
    /// rather than mpv's `auto`.
    ///
    /// That matters: mpv.conf pins d3d11va after measuring the alternatives,
    /// and `auto`/`auto-safe` can land on a copy-back path that reads every
    /// frame back out of VRAM — 17 fps against 32.7 at 8K. Offering "automatic"
    /// as the recommended entry would recommend the slow one.
    /// </summary>
    public static readonly Decoder[] Decoders =
    [
        new("dec.default", ""),
        new("dec.auto",    "auto-safe"),
        new("dec.d3d11va", "d3d11va", "d3d11"),
        new("dec.dxva2",   "dxva2",   "d3d11"),
        new("dec.nvdec",   "nvdec",   "opengl,vulkan"),
        new("dec.vulkan",  "vulkan",  "vulkan"),
        new("dec.off",     "no"),
    ];

    /// <summary>
    /// Whether a decoder can work with a given GPU API. `nvdec` is the one that
    /// bites: it is a CUDA interop path, mpv rejects it on the d3d11 backend
    /// ("CUDA hwdec only works with OpenGL or Vulkan backends") and silently
    /// decodes in software instead, which looks like the setting took effect.
    /// </summary>
    public static bool Supports(Decoder decoder, string gpuApi) =>
        decoder.RequiresApi.Length == 0 ||
        decoder.RequiresApi.Split(',').Contains(gpuApi, StringComparer.OrdinalIgnoreCase);

    /// <summary>The renderer to switch to so a decoder can actually be used.</summary>
    public static Renderer? RendererFor(Decoder decoder)
    {
        if (decoder.RequiresApi.Length == 0) return null;
        var wanted = decoder.RequiresApi.Split(',')[0];
        foreach (var r in Renderers)
            if (r.GpuApi.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <param name="GpuContext">Empty when the API's default context is right.</param>
    public readonly record struct Renderer(string Key, string Vo, string GpuApi, string GpuContext);

    /// <summary>
    /// A compatibility ladder, best-first. Each step gives up something for a
    /// wider range of drivers: libplacebo -> mpv's older shader renderer ->
    /// OpenGL instead of D3D11 -> OpenGL emulated over Direct3D -> Direct3D 9.
    /// A machine that cannot run the first one is exactly the machine that
    /// needs the last one.
    /// </summary>
    public static readonly Renderer[] Renderers =
    [
        new("ren.default", "gpu-next",  "d3d11",  ""),

        // There is no D3D12 to offer: mpv's --gpu-api is auto, d3d11, vulkan,
        // opengl, and libplacebo never grew a D3D12 backend. Vulkan is the
        // modern path here, and it was missing from this ladder entirely —
        // which also meant NVDEC had only OpenGL to fall back on, since NVDEC
        // is a CUDA interop path and D3D11 cannot accept its frames.
        new("ren.vulkan",  "gpu-next",  "vulkan", ""),

        new("ren.compat",  "gpu",       "d3d11",  ""),
        new("ren.opengl",  "gpu",       "opengl", ""),
        new("ren.angle",   "gpu",       "opengl", "angle"),
        new("ren.d3d9",    "direct3d",  "",       ""),
    ];

    public static Decoder? FindDecoder(string? mpvValue) =>
        Decoders.FirstOrDefault(d => d.MpvValue.Equals(mpvValue, StringComparison.OrdinalIgnoreCase))
            is { Key: not null } d ? d : null;

    public static Renderer? FindRenderer(string? vo, string? gpuApi) =>
        Renderers.FirstOrDefault(r =>
            r.Vo.Equals(vo, StringComparison.OrdinalIgnoreCase) &&
            (r.GpuApi.Length == 0 || r.GpuApi.Equals(gpuApi, StringComparison.OrdinalIgnoreCase)))
            is { Key: not null } r ? r : null;
}
