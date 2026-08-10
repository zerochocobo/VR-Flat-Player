using System.Diagnostics;

namespace HeadTrackBridge;

/// <summary>
/// One monotonic clock for the whole process, in seconds since start.
///
/// Pose timestamps and the output loop have to be on the same origin, and they
/// were not. Every tracking source started its own <see cref="Stopwatch"/> when
/// it started, and the output loop started another when it began — so
/// <c>ViewMapper.CheckStale</c> compared a pose stamped at 5 s by the camera's
/// clock against 60 s from the loop's, decided the tracker had been silent for
/// 55 seconds, and reported the signal lost while it was plainly arriving.
///
/// It showed as a status line reading LOST with head angles visibly updating
/// beside it, plus a spurious "tracking lost" toast. Only the camera made it
/// obvious, because the camera is the one source that starts on demand: start
/// it a minute in and the two clocks are a minute apart. The others start with
/// the session, so the gap was small enough to hide.
///
/// Wall-clock time is deliberately not used. It jumps when the system clock is
/// adjusted, and a filter differentiating a jump produces a very large angular
/// velocity from a head that did not move.
/// </summary>
public static class Clock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static double Seconds => Watch.Elapsed.TotalSeconds;
}
