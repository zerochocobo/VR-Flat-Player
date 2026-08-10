using System.Globalization;

namespace HeadTrackBridge;

/// <summary>
/// Works out which UI language the player should use.
///
/// mpv and uosc do not read the Windows UI language on their own — uosc's
/// `languages` option defaults to `slang,en`, and `slang` (the subtitle
/// language preference) is unset on a normal install, so everything falls back
/// to English no matter what locale the machine is in. The bridge is the only
/// component that can see <see cref="CultureInfo.CurrentUICulture"/>, so it
/// resolves the language once and pushes it into both.
/// </summary>
public static class Localization
{
    /// <summary>
    /// Locales available in mpv/scripts/uosc/intl. Most come from uosc itself;
    /// ja and ko are ours, written because upstream has none and a Japanese
    /// menu bar over an English control bar is worse than either alone. They are
    /// copied in by tools/install-mpv360.ps1 from mpv/uosc-intl.
    /// </summary>
    private static readonly string[] UoscLocales =
        ["de", "es", "fr", "it", "ja", "ko", "pl", "pt", "ro", "ru", "tr", "uk"];

    /// <summary>Languages we have written our own strings for, beyond English.</summary>
    private static readonly string[] OwnLocales = ["ja", "ko"];

    /// <summary>
    /// Resolves a config value ("auto" or an explicit tag) to the tag uosc
    /// wants. Returns null when English (uosc's built-in strings) should be used.
    /// </summary>
    public static string? ResolveUoscLanguage(string configured)
    {
        var culture = ResolveCulture(configured);
        if (culture is null) return null;

        if (IsChinese(culture, out var simplified))
            return simplified ? "zh-hans" : "zh-HK";

        var two = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return UoscLocales.Contains(two) ? two : null;
    }

    /// <summary>Language tag for our own UI: zh-hans, zh-hant, ja, ko or en.</summary>
    public static string ResolveOwnLanguage(string configured)
    {
        var culture = ResolveCulture(configured);
        if (culture is null) return "en";
        if (IsChinese(culture, out var simplified)) return simplified ? "zh-hans" : "zh-hant";

        var two = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return OwnLocales.Contains(two) ? two : "en";
    }

    private static CultureInfo? ResolveCulture(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.CurrentUICulture;

        if (configured.Equals("en", StringComparison.OrdinalIgnoreCase)) return null;

        try { return CultureInfo.GetCultureInfo(configured); }
        catch (CultureNotFoundException) { return CultureInfo.CurrentUICulture; }
    }

    /// <summary>
    /// Chinese needs script, not region: zh-SG is Simplified while zh-MO is
    /// Traditional, so branching on the country code alone gets both wrong.
    /// </summary>
    private static bool IsChinese(CultureInfo culture, out bool simplified)
    {
        simplified = true;
        if (!culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = culture.Name.ToLowerInvariant();
        simplified = !(name.Contains("hant") || name.Contains("-tw") ||
                       name.Contains("-hk") || name.Contains("-mo"));
        return true;
    }

    /// <summary>mpv command-line arguments that apply the resolved language.</summary>
    public static IEnumerable<string> MpvArgs(string configured)
    {
        var own = ResolveOwnLanguage(configured);
        yield return $"--script-opts-append=vrmenu-language={own}";

        var uosc = ResolveUoscLanguage(configured);
        if (uosc is not null)
            yield return $"--script-opts-append=uosc-languages={uosc},en";
    }

    public static string Describe(string configured) =>
        $"{CultureInfo.CurrentUICulture.Name} -> vrmenu={ResolveOwnLanguage(configured)}, " +
        $"uosc={ResolveUoscLanguage(configured) ?? "en"}";
}
