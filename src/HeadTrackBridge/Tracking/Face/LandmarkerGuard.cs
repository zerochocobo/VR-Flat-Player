namespace HeadTrackBridge.Tracking.Face;

/// <summary>
/// Survive a crash inside ONNX Runtime instead of being killed by it forever.
///
/// Creating the first session took the whole process down on a user's machine
/// with an access violation inside ORT's static constructor. <see
/// cref="OnnxLoader"/> explains why and fixes the cause; this is the seatbelt
/// for the next time a native library decides to die on us.
///
/// An access violation cannot be caught — it is a corrupted-state exception and
/// .NET tears the process down without running any handler — so the crash is
/// remembered instead. A marker file is written before the attempt and removed
/// after it returns. Finding one at startup means the previous attempt never
/// came back, so this run skips the landmarker, keeps the five-point fallback
/// and says so. Head tracking gets worse; the player still runs.
/// </summary>
public static class LandmarkerGuard
{
    /// <summary>
    /// True when a previous run of <em>this same build</em> crashed loading the
    /// landmarker.
    ///
    /// Version-scoped on purpose. A new build is usually a new build *because*
    /// of the crash, and refusing to try again would hide the fix behind a
    /// leftover file the user never knew to delete. Read once at startup,
    /// before <see cref="Begin"/> can overwrite the evidence.
    /// </summary>
    public static bool PreviousAttemptCrashed { get; } = CrashedBefore();

    private static bool CrashedBefore()
    {
        try
        {
            return File.Exists(AppPaths.LandmarkerGuardFile)
                && File.ReadAllText(AppPaths.LandmarkerGuardFile).Trim() == AppInfo.Version;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void Begin()
    {
        try { File.WriteAllText(AppPaths.LandmarkerGuardFile, AppInfo.Version); }
        catch (IOException) { /* diagnostics must never be the thing that fails */ }
        catch (UnauthorizedAccessException) { }
    }

    public static void Succeeded()
    {
        try { File.Delete(AppPaths.LandmarkerGuardFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
