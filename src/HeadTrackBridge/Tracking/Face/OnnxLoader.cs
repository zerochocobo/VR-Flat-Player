using System.Runtime.InteropServices;

namespace HeadTrackBridge.Tracking.Face;

/// <summary>
/// Make sure the onnxruntime we load is the one we shipped.
///
/// On a user's machine the player died with an access violation the first time
/// it touched ONNX Runtime, inside ORT's own static constructor. The diagnostic
/// line added for it named the culprit outright:
///
///     managed binding 1.28.0.0
///     native C:\WINDOWS\SYSTEM32\onnxruntime.DLL (1.17.260613 os-germanium)
///
/// Windows ships its own onnxruntime.dll in System32, and that machine's copy is
/// eleven releases older than the binding we build against. The managed side
/// asks the native API table for a function pointer that 1.17 never put there,
/// gets whatever is past the end of the older struct, and calls it. There is no
/// error to catch: the process is simply gone.
///
/// The default search order lets that happen — plain <c>LoadLibrary</c> reaches
/// System32 — so the version actually used depends on what Windows build the
/// user is on. It worked on the development machine for exactly that reason,
/// which is why four earlier theories all found something else to blame.
///
/// The fix is to stop leaving it to chance. A resolver bound to ORT's assembly
/// loads our own copy by full path, so the P/Invoke gets that module and never
/// consults the search path at all.
/// </summary>
public static class OnnxLoader
{
    private static bool _installed;
    private static IntPtr _handle;

    /// <summary>Where our onnxruntime.dll is, or null if it was not found.</summary>
    public static string? BundledPath { get; private set; }

    /// <summary>Why the pinned library could not be loaded, if it could not.</summary>
    public static string? Failure { get; private set; }

    /// <summary>
    /// True when our own onnxruntime is loaded and every ORT call will reach it.
    ///
    /// When this is false the landmarker must not be constructed at all. The
    /// tempting alternative — let the default search run and hope — is exactly
    /// what kills the process, because by the time the video is playing there is
    /// already a System32 copy in the process for it to find.
    /// </summary>
    public static bool Ready => _handle != IntPtr.Zero;

    /// <summary>
    /// Must run before anything touches an ORT type. A DllImportResolver is only
    /// consulted for loads that have not happened yet, and ORT's static
    /// constructor — the thing that crashes — is the first load.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        BundledPath = FindBundled();
        if (BundledPath is null)
        {
            Failure = "no bundled onnxruntime.dll found in the native search directories";
            return;
        }

        // Loaded here and now, not lazily from inside the resolver.
        //
        // The resolver used to load on demand and return IntPtr.Zero if that
        // failed, which quietly handed the decision back to the default search —
        // and the default search is the whole problem. A build shipped that way
        // logged "pinned to <our path>" and then crashed anyway, because the
        // pinning had silently not happened and nothing said so.
        //
        // The likeliest reason for a failure here is a missing Microsoft Visual
        // C++ runtime: our onnxruntime needs it, the one Windows ships in
        // System32 is built against what Windows already has, so the machine can
        // load theirs and not ours.
        try
        {
            _handle = NativeLibrary.Load(BundledPath);
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
            return;
        }

        // The resolver hangs off ORT's assembly, not ours: the DllImport
        // attributes that need redirecting live there. It hands back the handle
        // above, so no search of any kind takes place.
        NativeLibrary.SetDllImportResolver(
            typeof(Microsoft.ML.OnnxRuntime.SessionOptions).Assembly,
            // Exactly this name. onnxruntime_providers_shared is a different
            // library and handing it this handle would be a silent lie.
            (name, _, _) => name.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase)
                ? _handle
                : IntPtr.Zero);
    }

    /// <summary>
    /// Where our onnxruntime.dll is at run time, which depends on how the build
    /// was published and so must not be assumed.
    ///
    /// Beside the exe, for the release: the single-file build bundles only the
    /// managed assemblies, so the native DLLs are ordinary files in the install
    /// directory. Also beside the exe for a framework-dependent build and for a
    /// plain <c>dotnet run</c> during development.
    ///
    /// NATIVE_DLL_SEARCH_DIRECTORIES is still consulted first, and still has to
    /// be: a build made with IncludeNativeLibrariesForSelfExtract unpacks to a
    /// temporary directory and names it there, and that is the one layout where
    /// the base directory holds no DLL at all.
    /// </summary>
    private static string? FindBundled()
    {
        var dirs = new List<string>();
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string list)
            dirs.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(AppContext.BaseDirectory);

        foreach (var dir in dirs)
        {
            // System32 can legitimately appear in the list on some layouts, and
            // it is the one place whose copy must never win.
            if (dir.Contains("System32", StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = Path.Combine(dir, "onnxruntime.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// What we build against, what we intend to load, and what is loaded now.
    /// Written to the log before the session is created, because the
    /// interesting case is the one with no "after".
    /// </summary>
    public static string Describe()
    {
        var managed = typeof(Microsoft.ML.OnnxRuntime.SessionOptions).Assembly
                          .GetName().Version?.ToString() ?? "unknown";

        if (Ready) return $"binding {managed}, loaded {BundledPath}";

        // The failure has to be as loud as the success. The version that said
        // "pinned to <our path>" and nothing else was describing an intention
        // that had silently not happened, and the player crashed one line later.
        return $"binding {managed}, NOT LOADED — {Failure}" +
               (BundledPath is null ? "" : $" (tried {BundledPath})") +
               ". Head tracking will fall back to 5 points.";
    }

    /// <summary>
    /// Which onnxruntime is in the process right now. Printed after a
    /// successful load, because "pinned" is an intention and this is the fact —
    /// and the fact is the only thing that distinguishes a working machine from
    /// one that would have crashed.
    /// </summary>
    public static string LoadedNative()
    {
        var loaded = new List<string>();
        try
        {
            foreach (System.Diagnostics.ProcessModule m in
                     System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                if (!m.ModuleName.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase)) continue;
                loaded.Add($"{m.FileName} ({m.FileVersionInfo.FileVersion})");
            }
        }
        catch (Exception ex) { loaded.Add($"could not enumerate modules: {ex.Message}"); }

        return loaded.Count == 0 ? "none" : string.Join("; ", loaded);
    }
}
