using System.Reflection;

namespace HeadTrackBridge;

/// <summary>
/// The product's name and version, in one place.
///
/// The version is read back from the assembly rather than duplicated here, so
/// the number in the About box, the console banner and the exe's file
/// properties can never disagree — there is exactly one place to edit it, and
/// that is <c>&lt;InformationalVersion&gt;</c> in the csproj.
/// </summary>
public static class AppInfo
{
    public const string Name = "VR Flat Player";

    /// <summary>Short display version, e.g. "0.1".</summary>
    public static string Version { get; } = ReadVersion();

    public static string NameAndVersion => $"{Name} {Version}";

    /// <summary>
    /// The window and Alt+Tab icon, or null if the resource is missing.
    ///
    /// Null rather than throwing: a missing icon costs nothing but the stock
    /// WinForms one, and refusing to open the player over it would be absurd.
    /// </summary>
    public static Icon? Icon { get; } = ReadIcon();

    private static Icon? ReadIcon()
    {
        try
        {
            using var stream = typeof(AppInfo).Assembly.GetManifestResourceStream("VRFlatPlayer.icon.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  [icon] could not load the embedded icon: {e.Message}");
            return null;
        }
    }

    private static string ReadVersion()
    {
        var raw = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(raw)) return "0.0";

        // The SDK appends "+<commit sha>" when the build knows its source
        // revision. That belongs in a build log, not in a title bar.
        var plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }
}
