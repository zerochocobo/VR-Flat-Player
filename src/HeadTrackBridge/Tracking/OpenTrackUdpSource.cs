using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace HeadTrackBridge.Tracking;

/// <summary>
/// Receives opentrack's "UDP over network" output protocol.
///
/// Wire format: a single datagram of 6 little-endian float64 =
///   x, y, z (cm), yaw, pitch, roll (degrees)   -> 48 bytes.
///
/// Because the laptop (webcam) and the desktop (GPU) are different machines
/// here, set opentrack's UDP output host to the desktop's LAN IP and this
/// receiver just binds 0.0.0.0 on the configured port.
/// </summary>
public sealed class OpenTrackUdpSource : ITrackingSource
{
    private const int PacketSize = 48;

    private readonly int _port;
    private readonly bool _dump;
    private UdpClient? _client;
    private Task? _loop;

    public OpenTrackUdpSource(int port, bool dump = false)
    {
        _port = port;
        _dump = dump;
    }

    public string Name => $"opentrack-udp:{_port}";

    public event Action<HeadPose>? PoseReceived;

    /// <summary>Datagrams received since start — used by the diagnostics line.</summary>
    public long PacketCount { get; private set; }

    /// <summary>Datagrams dropped because they were not <see cref="PacketSize"/> bytes.</summary>
    public long MalformedCount { get; private set; }

    public void Start(CancellationToken ct)
    {
        _client = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        _loop = Task.Run(() => ReceiveLoop(ct), ct);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // Transient ICMP port-unreachable etc. Keep listening.
                continue;
            }

            var data = result.Buffer;
            if (data.Length != PacketSize)
            {
                MalformedCount++;
                if (_dump)
                {
                    Console.WriteLine($"[udp] unexpected packet size {data.Length} from {result.RemoteEndPoint}");
                }
                continue;
            }

            PacketCount++;
            var t = (DateTime.UtcNow - start).TotalSeconds;
            var pose = ParsePose(data, t);

            if (_dump)
            {
                Console.WriteLine($"[udp] {result.RemoteEndPoint}  {pose}");
            }

            PoseReceived?.Invoke(pose);
        }
    }

    /// <summary>Kept out of the async loop: a Span local is not allowed in an async method.</summary>
    private static HeadPose ParsePose(byte[] data, double t)
    {
        var span = data.AsSpan();
        return new HeadPose(
            Yaw: ReadD(span, 3),
            Pitch: ReadD(span, 4),
            Roll: ReadD(span, 5),
            X: ReadD(span, 0),
            Y: ReadD(span, 1),
            Z: ReadD(span, 2),
            TimeSeconds: t);
    }

    private static double ReadD(ReadOnlySpan<byte> buf, int index) =>
        BinaryPrimitives.ReadDoubleLittleEndian(buf.Slice(index * 8, 8));

    public void Dispose()
    {
        _client?.Dispose();
        try { _loop?.Wait(TimeSpan.FromMilliseconds(200)); } catch { /* shutting down */ }
    }
}
