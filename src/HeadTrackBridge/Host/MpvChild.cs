using System.Runtime.InteropServices;

namespace HeadTrackBridge.Host;

/// <summary>
/// Keeps mpv's embedded video window filling the panel we gave it.
///
/// mpv does resize itself to its <c>--wid</c> parent, but only when it notices,
/// and it never notices at all in two cases that matter here: the moment right
/// after launch (the parent's size is read once, before our layout has settled)
/// and any attempt to go fullscreen, where mpv may resize the child to the
/// monitor and leave it hanging outside the host. Driving the size from the
/// host makes both self-correcting, and costs one GetWindowRect per tick
/// because the move only happens when the rectangle is actually wrong.
/// </summary>
public static class MpvChild
{
    /// <summary>The first direct child of <paramref name="parent"/>, or zero.</summary>
    public static IntPtr Find(IntPtr parent)
    {
        var found = IntPtr.Zero;
        if (parent == IntPtr.Zero) return found;

        EnumChildWindows(parent, (h, _) => { found = h; return false; }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Resize mpv's child to the parent's client area if it does not already
    /// match, and make sure it can still receive the mouse. Returns false when
    /// there is no child yet, which is the normal state for the first fraction
    /// of a second after launch.
    /// </summary>
    /// <param name="wantEnabled">
    /// False while a menu is open. See <see cref="SetEnabled"/>.
    /// </param>
    public static bool FitToParent(IntPtr parent, bool wantEnabled = true)
    {
        var child = Find(parent);
        if (child == IntPtr.Zero) return false;

        SetEnabled(child, wantEnabled);
        if (!GetClientRect(parent, out var rc)) return false;

        // The child's own client rect is what we are matching; its window rect
        // is in screen coordinates and a child has no border here anyway.
        if (GetClientRect(child, out var cur) && cur.Right == rc.Right && cur.Bottom == rc.Bottom)
            return true;

        MoveWindow(child, 0, 0, rc.Right, rc.Bottom, bRepaint: true);
        return true;
    }

    /// <summary>
    /// mpv creates its --wid child with WS_DISABLED, because the embedding
    /// application is expected to handle input and forward what it wants. A
    /// disabled window gets no mouse messages at all, so left as-is uosc never
    /// appears, the timeline cannot be clicked, and mpv reports
    /// mouse-pos {x:0, y:0, hover:false} forever.
    ///
    /// Re-enabling is better than forwarding events ourselves: mpv then gets
    /// real WM_MOUSEMOVE/WM_MOUSEWHEEL with correct hover tracking, so uosc
    /// behaves exactly as it does in a standalone mpv. It does not cost us the
    /// keyboard — focus still cannot cross into another process's child window,
    /// which is why the host relays keys (see PlayerWindow).
    ///
    /// Re-checked on every tick rather than once, because mpv disables the
    /// window again whenever it recreates it (a VO reinit, for instance).
    ///
    /// Disabling it again is how an open menu gets dismissed by a click on the
    /// video. WinForms closes a dropdown from a message filter on its own
    /// message loop, and a click on an enabled cross-process child never
    /// reaches that loop — so the dropdown just stayed open. Turning the child
    /// off for the duration of the menu sends the click to our panel instead,
    /// which restores the normal behaviour rather than imitating it.
    /// </summary>
    public static void SetEnabled(IntPtr child, bool enabled)
    {
        if (child != IntPtr.Zero && IsWindowEnabled(child) != enabled) EnableWindow(child, enabled);
    }

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool bRepaint);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hWnd, bool enable);
}
