using System.Runtime.InteropServices;

namespace HeadTrackBridge.Input;

/// <summary>
/// Detects left-drag over the video and reports it as normalised deltas.
///
/// Why this is not done in Lua, where it belongs: uosc claims MBTN_LEFT with a
/// forced binding for its whole cursor system. It has a documented path to
/// forward clicks it does not use on to other scripts, but in practice that
/// forward never reached us, and a script binding cannot outrank uosc's without
/// breaking every uosc button. Watching the physical mouse from the host
/// process sidesteps mpv's input routing completely, and this is a Windows-only
/// player anyway.
///
/// The cost of bypassing mpv is that we no longer get "is the cursor over a UI
/// element" for free, so presses that begin over the control bar or while the
/// mode panel is open are excluded explicitly.
/// </summary>
public sealed class MouseDragWatcher : IDisposable
{
    private readonly Func<IntPtr> _resolveVideoWindow;
    private readonly double _bottomExclusionFraction;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="resolveVideoWindow">
    /// The window whose client area *is* the video. Detached mode passes mpv's
    /// own window (see <see cref="FindLargestVisibleWindow"/>); hosted mode
    /// passes the host's video panel, which is the mpv child's parent. Resolved
    /// on every press rather than cached because in detached mode mpv destroys
    /// and recreates its window.
    /// </param>
    public MouseDragWatcher(Func<IntPtr> resolveVideoWindow, double bottomExclusionFraction = 0.18,
                            bool requireForeground = true)
    {
        _resolveVideoWindow = resolveVideoWindow;
        _bottomExclusionFraction = bottomExclusionFraction;
        _requireForeground = requireForeground;
    }

    private readonly bool _requireForeground;

    /// <summary>Set while the mode panel is open, so its buttons are not draggable.</summary>
    public bool Suppressed { get; set; }

    public event Action? DragBegin;

    /// <summary>Deltas as a fraction of client width (DPI- and size-independent).</summary>
    public event Action<double, double>? DragDelta;

    public event Action? DragEnd;

    /// <summary>
    /// A press and release on the video that did not move — a click rather than
    /// a drag.
    ///
    /// It shares every rule with a drag, which is the point: a press over the
    /// control bar or while the mode panel is open is already excluded, so this
    /// cannot steal a click that belongs to uosc or to vrmenu.
    ///
    /// The two thresholds below are what separates a click from a very short
    /// drag. Both have to hold, because either alone is wrong: a flick of the
    /// wrist is fast *and* moves, and resting the button down without moving is
    /// still not a click.
    /// </summary>
    public event Action? Click;

    /// <summary>Movement, in pixels, still counted as a click rather than a drag.</summary>
    private const int ClickSlopPixels = 4;

    /// <summary>How long the button may be held and still count as a click.</summary>
    private static readonly TimeSpan ClickHold = TimeSpan.FromMilliseconds(400);

    /// <summary>Why a press did not start a drag. Diagnostics only.</summary>
    public event Action<string>? Rejected;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => Loop(_cts.Token), _cts.Token);
    }

    private async Task Loop(CancellationToken ct)
    {
        var dragging = false;
        var wasDown = false;
        POINT last = default;
        var hwnd = IntPtr.Zero;   // the window the current drag started on

        // Where and when the press began, for telling a click from a drag on
        // release. Tracked separately from `last`, which moves with the cursor.
        POINT pressAt = default;
        var pressedAt = DateTime.MinValue;
        var movedTooFar = false;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));   // ~120 Hz
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }

            var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

            if (!down)
            {
                if (dragging)
                {
                    dragging = false;
                    DragEnd?.Invoke();

                    // Fired on release, not on press: until the button comes up
                    // there is no way to know this was not the start of a drag,
                    // and opening a file dialog under a held button would be
                    // the worst possible guess.
                    if (!movedTooFar && DateTime.UtcNow - pressedAt <= ClickHold)
                        Click?.Invoke();
                }
                wasDown = false;
                continue;
            }

            if (!GetCursorPos(out var p)) continue;

            if (!wasDown)
            {
                wasDown = true;
                // Only a press that STARTS on the video begins a drag. Anything
                // that starts elsewhere (control bar, another window) is left
                // alone for its whole duration.
                var why = Reject(p, out hwnd);
                if (why is null)
                {
                    dragging = true;
                    last = p;
                    pressAt = p;
                    pressedAt = DateTime.UtcNow;
                    movedTooFar = false;
                    DragBegin?.Invoke();
                }
                else
                {
                    Rejected?.Invoke(why);
                }
                continue;
            }

            if (!dragging) continue;

            // Measured from the press, not from the last sample: a slow drag
            // moves a pixel at a time and would otherwise never trip this.
            if (Math.Abs(p.X - pressAt.X) > ClickSlopPixels ||
                Math.Abs(p.Y - pressAt.Y) > ClickSlopPixels)
                movedTooFar = true;

            var dx = p.X - last.X;
            var dy = p.Y - last.Y;
            if (dx == 0 && dy == 0) continue;
            last = p;

            if (!GetClientRect(hwnd, out var rc) || rc.Right <= 0) continue;
            DragDelta?.Invoke(dx / (double)rc.Right, dy / (double)rc.Right);
        }

        if (dragging) DragEnd?.Invoke();
    }

    /// <summary>
    /// Null when the press should start a drag, otherwise the reason it should
    /// not. Returning a reason rather than a bool because every one of these
    /// conditions is a plausible silent failure.
    /// </summary>
    private string? Reject(POINT screen, out IntPtr hwnd)
    {
        hwnd = _resolveVideoWindow();
        if (Suppressed) return "mode panel is open";
        if (hwnd == IntPtr.Zero) return "~no video window yet";

        if (_requireForeground)
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return "no foreground window";
            // Compare against the top-level ancestor, not the target itself:
            // in hosted mode the target is a child panel and the foreground
            // window is the host form that contains it.
            if (fg != hwnd && fg != GetAncestor(hwnd, GA_ROOT))
                // Normal and frequent: the user is working in another application.
                return "~the player is not the active window";
        }

        if (!GetClientRect(hwnd, out var rc)) return "GetClientRect failed";

        var p = screen;
        if (!ScreenToClient(hwnd, ref p)) return "ScreenToClient failed";

        if (p.X < 0 || p.Y < 0 || p.X >= rc.Right || p.Y >= rc.Bottom)
            return $"cursor ({p.X},{p.Y}) outside client 0,0-{rc.Right},{rc.Bottom}";

        var exclude = Math.Max(90, rc.Bottom * _bottomExclusionFraction);
        if (p.Y >= rc.Bottom - exclude)
            return $"press in the control-bar band (y={p.Y}, band starts {rc.Bottom - exclude:F0})";

        return null;
    }

    /// <summary>
    /// Largest visible top-level window owned by the process — mpv's video
    /// window in detached mode. By area rather than by
    /// <c>Process.MainWindowHandle</c>, which returns one of mpv's other
    /// top-level windows and so never matches.
    /// </summary>
    public static IntPtr FindLargestVisibleWindow(int pid)
    {
        if (pid == 0) return IntPtr.Zero;

        var best = IntPtr.Zero;
        long bestArea = 0;

        EnumWindows((h, lParam) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || !IsWindowVisible(h)) return true;
            if (!GetWindowRect(h, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    private const int VK_LBUTTON = 0x01;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromMilliseconds(200)); } catch { /* shutting down */ }
        _cts?.Dispose();
    }
}
