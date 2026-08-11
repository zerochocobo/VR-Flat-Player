namespace HeadTrackBridge;

/// <summary>
/// Where things live, which is not one directory but two.
///
/// <see cref="InstallRoot"/> is what we ship: the bundled mpv, its config tree,
/// and the default bridge.config.json. <see cref="DataRoot"/> is what we write:
/// the user's config, the per-file mode memory, mpv's log, recordings.
///
/// They are the same directory for a portable copy, and they have to be
/// different for an installed one — anything under Program Files is read-only
/// for a normal user, and a player that silently cannot remember a setting is
/// worse than one that says so. The split is decided once, by trying to write,
/// because permissions are not something to infer from a path.
/// </summary>
public static class AppPaths
{
    public static string InstallRoot { get; } = FindInstallRoot();
    public static string DataRoot { get; } = FindDataRoot(InstallRoot);

    /// <summary>True when config and mode memory do not sit next to the exe.</summary>
    public static bool IsRedirected => !PathsEqual(InstallRoot, DataRoot);

    public static string MpvConfigDir => Path.Combine(InstallRoot, "mpv");
    public static string ConfigFile => Path.Combine(DataRoot, "bridge.config.json");
    public static string ModeMemoryFile => Path.Combine(DataRoot, "mode-memory.json");

    /// <summary>Window position and size. State, not settings — see <see cref="WindowConfig"/>.</summary>
    public static string WindowStateFile => Path.Combine(DataRoot, "window-state.json");
    public static string MpvLogFile => Path.Combine(DataRoot, "mpv-last-run.log");

    /// <summary>
    /// Exists only while the ONNX landmarker is being created. See
    /// <c>Tracking.Face.LandmarkerGuard</c> — the presence of this file on
    /// startup means the last attempt killed the process.
    /// </summary>
    public static string LandmarkerGuardFile => Path.Combine(DataRoot, "landmarker-loading.flag");

    /// <summary>Resolve a possibly-relative path from the config against the data directory.</summary>
    public static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(DataRoot, path);

    /// <summary>
    /// Copy the shipped defaults into the data directory the first time we run
    /// from a read-only install, so the user has something to edit and so every
    /// later read and write can use one path.
    /// </summary>
    public static void SeedUserConfig()
    {
        if (!IsRedirected || File.Exists(ConfigFile)) return;

        var shipped = Path.Combine(InstallRoot, "bridge.config.json");
        if (!File.Exists(shipped)) return;

        try { File.Copy(shipped, ConfigFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not fatal: BridgeConfig falls back to its compiled-in defaults.
        }
    }

    /// <summary>
    /// A published layout has mpv/ next to the exe. A dev checkout has the exe
    /// several levels down in bin/, so walk up for the directory that has both
    /// mpv/ and src/ — requiring both is what stops a stray mpv/ folder
    /// somewhere up the tree from being mistaken for the repo.
    /// </summary>
    private static string FindInstallRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDir, "mpv"))) return TrimSeparator(baseDir);

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "mpv")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return TrimSeparator(baseDir);
    }

    private static string FindDataRoot(string installRoot)
    {
        if (IsWritable(installRoot)) return installRoot;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
        try { Directory.CreateDirectory(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return installRoot;   // out of options; let the write fail loudly later
        }
        return dir;
    }

    private static bool IsWritable(string dir)
    {
        var probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string TrimSeparator(string p) => p.TrimEnd(Path.DirectorySeparatorChar);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(TrimSeparator(Path.GetFullPath(a)), TrimSeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);
}
