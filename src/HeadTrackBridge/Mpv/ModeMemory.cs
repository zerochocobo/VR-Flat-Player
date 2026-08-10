using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeadTrackBridge.Mpv;

public sealed record RememberedMode(Geometry Geometry, Stereo Stereo, Eye Eye, double? FovDegrees);

/// <summary>
/// Remembers the VR mode chosen for each file.
///
/// This matters more than improving auto-detection. A mono 360 file and a VR180
/// side-by-side file are both 2:1 with nothing in the stream to tell them
/// apart, so detection is guessing from the filename and will keep being wrong
/// for some files no matter how many patterns get added. Remembering turns that
/// from "wrong every single time you open it" into "wrong once".
///
/// Keyed by full path, hashed, so the index does not become a list of
/// everything the user has ever watched in plain text.
/// </summary>
public sealed class ModeMemory
{
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private readonly object _lock = new();
    private bool _dirty;

    private sealed class Entry
    {
        public string Geometry { get; set; } = "";
        public string Stereo { get; set; } = "";
        public string Eye { get; set; } = "";
        public double? Fov { get; set; }
        public string? Hint { get; set; }      // filename, for a readable index
        public DateTimeOffset Saved { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ModeMemory(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable index must never stop playback; start fresh.
            return new();
        }
    }

    /// <summary>
    /// Reserved key for the sticky "mode the user last picked". Safe to keep in
    /// the same map: real keys are 16 hex characters, so this cannot collide,
    /// and it means the sticky mode survives a restart without a second file or
    /// a format change.
    /// </summary>
    private const string LastUsedKey = "last";

    /// <summary>Files remembered, not counting the sticky-mode entry.</summary>
    public int Count { get { lock (_lock) return _entries.Count(e => e.Key != LastUsedKey); } }

    public RememberedMode? Get(string? mediaPath) => Read(KeyFor(mediaPath));

    public void Set(string? mediaPath, RememberedMode mode) =>
        Write(KeyFor(mediaPath), mode, Path.GetFileName(mediaPath));

    /// <summary>
    /// The last mode the user chose by hand, for any file. Used for a file we
    /// have never seen whose layout cannot be detected: someone working through
    /// a folder of similarly-encoded videos should set the mode once, not once
    /// per file.
    /// </summary>
    public RememberedMode? LastUsed => Read(LastUsedKey);

    public void SetLastUsed(RememberedMode mode) => Write(LastUsedKey, mode, "last mode chosen by hand");

    private RememberedMode? Read(string? key)
    {
        if (key is null) return null;

        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var e)) return null;
            if (!Enum.TryParse<Geometry>(e.Geometry, true, out var g)) return null;
            if (!Enum.TryParse<Stereo>(e.Stereo, true, out var s)) return null;
            if (!Enum.TryParse<Eye>(e.Eye, true, out var eye)) return null;
            return new RememberedMode(g, s, eye, e.Fov);
        }
    }

    private void Write(string? key, RememberedMode mode, string? hint)
    {
        if (key is null) return;

        lock (_lock)
        {
            _entries[key] = new Entry
            {
                Geometry = mode.Geometry.ToString(),
                Stereo = mode.Stereo.ToString(),
                Eye = mode.Eye.ToString(),
                Fov = mode.FovDegrees,
                Hint = hint,
                Saved = DateTimeOffset.Now,
            };
            _dirty = true;
        }
    }

    /// <summary>Writes via a temp file so an interrupted save cannot corrupt the index.</summary>
    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
                File.Move(tmp, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Losing the memory is not worth interrupting playback over.
            }
        }
    }

    /// <summary>
    /// Null for anything without a stable identity — streams, lavfi test
    /// sources — so we never key an entry on something that will not recur.
    /// </summary>
    private static string? KeyFor(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return null;
        if (mediaPath.Contains("://", StringComparison.Ordinal)) return null;

        string full;
        try { full = Path.GetFullPath(mediaPath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }
}
