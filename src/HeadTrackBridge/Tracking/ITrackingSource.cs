namespace HeadTrackBridge.Tracking;

/// <summary>
/// A source of head poses. The whole point of this abstraction is that the
/// renderer never knows whether the pose came from a real webcam, a synthetic
/// generator (dev box with no camera) or a recorded session.
/// </summary>
public interface ITrackingSource : IDisposable
{
    string Name { get; }

    /// <summary>Fired on the source's own thread whenever a new sample arrives.</summary>
    event Action<HeadPose>? PoseReceived;

    void Start(CancellationToken ct);
}
