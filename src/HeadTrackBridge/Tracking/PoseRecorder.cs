using System.Globalization;

namespace HeadTrackBridge.Tracking;

/// <summary>
/// Appends poses to a CSV so a session captured on the laptop (real webcam)
/// can be replayed on the desktop (real 8K video). This is the bridge between
/// the two machines when they are not on the same network.
/// </summary>
public sealed class PoseRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public PoseRecorder(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
        _writer.WriteLine("t,yaw,pitch,roll,x,y,z");
        Path_ = Path.GetFullPath(path);
    }

    public string Path_ { get; }

    public long Count { get; private set; }

    public void Write(in HeadPose p)
    {
        lock (_lock)
        {
            _writer.Write(p.TimeSeconds.ToString("F4", CultureInfo.InvariantCulture)); _writer.Write(',');
            _writer.Write(p.Yaw.ToString("F4", CultureInfo.InvariantCulture)); _writer.Write(',');
            _writer.Write(p.Pitch.ToString("F4", CultureInfo.InvariantCulture)); _writer.Write(',');
            _writer.Write(p.Roll.ToString("F4", CultureInfo.InvariantCulture)); _writer.Write(',');
            _writer.Write(p.X.ToString("F4", CultureInfo.InvariantCulture)); _writer.Write(',');
            _writer.Write(p.Y.ToString("F4", CultureInfo.InvariantCulture)); _writer.Write(',');
            _writer.Write(p.Z.ToString("F4", CultureInfo.InvariantCulture));
            _writer.Write('\n');
            Count++;
            if (Count % 240 == 0) _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
