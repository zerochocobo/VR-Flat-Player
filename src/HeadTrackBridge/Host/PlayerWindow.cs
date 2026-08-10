using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using HeadTrackBridge.Mpv;

namespace HeadTrackBridge.Host;

/// <summary>
/// The player window: a native menu bar over a panel that mpv draws into.
///
/// The menu bar is the whole reason this class exists. mpv can only ever paint
/// inside its own video surface, so every control it offers — the stock OSC,
/// uosc, our vrmenu — is drawn on top of the picture. A conventional desktop
/// player has a real menu bar above the video, and the only way to get one is
/// to own the top-level window and hand mpv a child of it via --wid.
///
/// What that costs, and how each cost is paid:
///   - keyboard focus does not cross the process boundary into mpv's child, so
///     the host relays keys over IPC (see <see cref="KeyMap"/>);
///   - mpv cannot fullscreen a child window, so the host mirrors mpv's
///     `fullscreen` property instead of letting mpv act on it;
///   - the child does not always follow the parent's size, so
///     <see cref="MpvChild"/> corrects it on a timer.
/// Mouse input needs nothing: mouse messages go to the window under the cursor
/// regardless of focus, so uosc keeps working untouched.
/// </summary>
public sealed class PlayerWindow : Form
{
    private readonly PlayerSession _session;
    private readonly UiStrings _t;
    private readonly Panel _video;
    private readonly MenuStrip _menu;
    private readonly System.Windows.Forms.Timer _fitTimer;

    // Mirrored from mpv rather than tracked independently: uosc, the mode panel
    // and mpv's own key bindings can all change these behind our back, and a
    // menu showing a stale checkmark is worse than no checkmark.
    private bool _muted;
    private bool _fullscreen;
    private bool _loopFile;
    private double _speed = 1.0;
    private JsonElement? _trackList;
    private long? _audioTrack;
    private long? _subTrack;
    private JsonElement? _playlist;
    private int? _playlistPos;

    private Rectangle _windowedBounds;
    private FormWindowState _windowedState = FormWindowState.Normal;

    public PlayerWindow(PlayerSession session)
    {
        _session = session;
        // The same instance the OSD toasts use; Program.Main resolves it once.
        _t = UiStrings.Current;

        Text = AppInfo.Name;
        Icon = AppInfo.Icon;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        KeyPreview = true;

        _video = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            // The panel is a pure host for mpv's child window. Letting WinForms
            // paint it produces a black flash on every resize on top of whatever
            // mpv is drawing.
            TabStop = false,
        };

        _menu = BuildMenu();

        // Order matters: docked controls fill in reverse order of addition, so
        // the panel must be added first for the menu to sit above it.
        Controls.Add(_video);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _fitTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _fitTimer.Tick += (_, _) => OnFitTick();
        _video.Resize += (_, _) => MpvChild.FitToParent(_video.Handle, wantEnabled: !_menuOpen);

        // While a menu is open: a press must not also drag the view (the drag
        // watcher polls the physical mouse and cannot see menus), and clicks on
        // the video have to reach us so they can dismiss the dropdown.
        _menu.MenuActivate += (_, _) => SetMenuOpen(true);
        _menu.MenuDeactivate += (_, _) => SetMenuOpen(false);

        // The hotkey path (Ctrl+Shift+B) reaches the session directly, so the
        // offer to restart has to come from here rather than from the menu item.
        _session.RendererChanged += _ => RunOnUi(OfferRestart);

        // Picking a decoder the renderer cannot feed is the one dead end in
        // these two menus, and the fix is in the other one. Offer it directly
        // rather than expecting the user to work out the connection.
        _session.DecoderNeedsRenderer += (decoder, renderer) => RunOnUi(() =>
        {
            // One question, not two. The user asked for a decoder; that it also
            // needs a different renderer and a restart is our problem, and
            // making them confirm each step in turn is asking them to
            // understand a coupling they should never have had to learn.
            var body = string.Format(_t["dec.switchBody"],
                                     _t[decoder.Key].Trim(), renderer.GpuApi, _t[renderer.Key]);
            if (MessageBox.Show(this, body, _t["dec.switchTitle"],
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _session.SetRenderer(renderer);
            Restart();
        });

        DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                Send("loadfile", files[0]);
        };
    }

    /// <summary>The window mpv should draw into. Valid once the form is shown.</summary>
    public IntPtr VideoHandle => _video.Handle;

    /// <summary>
    /// Sizes in physical pixels, from the DPI of the monitor we actually landed
    /// on.
    ///
    /// Not in the constructor: WinForms does not rescale a ClientSize set before
    /// the handle exists, so on the 300% display this is developed on a
    /// "1280x760" window came out 1280x760 *physical* — a third of the intended
    /// size, with correctly-scaled menu text crammed into it.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var scale = DeviceDpi / 96.0;
        var work = Screen.FromControl(this).WorkingArea;

        // A square video panel, three quarters of the screen at most.
        //
        // Square because nothing here is 16:9: a VR180 file is two square eyes
        // and you watch one of them, a 360 file is a sphere. Framing either
        // inside a letterbox wastes the top and bottom of the window on black
        // and gives away vertical field of view, the axis with least to give.
        //
        // Three quarters because a first run should neither fill the display
        // nor be a postage stamp. Half was tried and came out about 440 px
        // square on a 1080p screen at 125% scaling, too small to watch anything
        // in. The window remembers what you resize it to, so this is only ever
        // the starting point — but it is the one size the user has not chosen.
        //
        // The fraction is of the whole window, not the picture inside it: the
        // border, title bar and menu bar come out of the allowance first.
        // Taking it on the panel instead turned an 845 px square into a 1123 px
        // tall window against a 1020 px budget, which is what the first attempt
        // did.
        //
        // Square is the *panel*: the menu bar's height is added on top rather
        // than taken out of the picture.
        const double screenFraction = 0.75;
        var chrome = _menu.Height;
        var frame = SizeFromClientSize(Size.Empty);
        var side = (int)Math.Min(work.Width * screenFraction - frame.Width,
                                 work.Height * screenFraction - frame.Height - chrome);

        // Derived from the default, never a fixed number, because a minimum
        // larger than the default silently wins and the window comes out a
        // shape nobody asked for. Measured at 288 dpi: half the screen height
        // leaves a 948 px square, a flat 360-logical minimum is 1080 px, and
        // the "square" window opened 1044x948.
        var min = Math.Min((int)(360 * scale), side);
        MinimumSize = new Size(min, min * 2 / 3);

        if (RestorePlacement()) return;

        ClientSize = new Size(side, side + chrome);
        CenterToScreen();
    }

    /// <summary>
    /// Put the window back where it was last closed, or return false if there is
    /// nothing usable saved.
    /// </summary>
    /// <remarks>
    /// The saved rectangle is checked against the monitors that exist *now*.
    /// Restoring blind is how a window ends up off-screen and unreachable after
    /// a laptop is undocked, and the user has no way to drag back something they
    /// cannot see.
    /// </remarks>
    private bool _restoring;

    private bool RestorePlacement()
    {
        var saved = _session.WindowPlacement;
        if (saved.Width < MinimumSize.Width || saved.Height < MinimumSize.Height) return false;

        var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
        var screen = Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.IntersectsWith(bounds));
        if (screen is null) return false;

        bounds = Fit(bounds, screen.WorkingArea);

        // Restoring must not count as the user placing the window. Setting
        // Bounds and WindowState here fires the same events a drag does, so
        // without this the startup path writes straight back over the memory it
        // just read — and whatever WinForms reports mid-restore becomes the new
        // remembered size.
        _restoring = true;
        try
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            if (saved.Maximized) WindowState = FormWindowState.Maximized;
        }
        finally { _restoring = false; }
        return true;
    }

    /// <summary>
    /// Shrink and nudge a rectangle until it fits entirely inside
    /// <paramref name="work"/>.
    /// </summary>
    /// <remarks>
    /// A remembered size is not a promise that the size is still reasonable. It
    /// can come from a bigger monitor, from a session before the taskbar moved,
    /// or from a display that is no longer attached — and a window that opens
    /// larger than the screen puts its own edges out of reach, so there is no
    /// way to drag it back to something usable.
    ///
    /// Size is clamped before position, and position last, so a window that was
    /// too big ends up fully on screen rather than merely narrower and still
    /// hanging off the bottom.
    /// </remarks>
    private static Rectangle Fit(Rectangle bounds, Rectangle work)
    {
        bounds.Width = Math.Min(bounds.Width, work.Width);
        bounds.Height = Math.Min(bounds.Height, work.Height);
        bounds.X = Math.Clamp(bounds.X, work.Left, work.Right - bounds.Width);
        bounds.Y = Math.Clamp(bounds.Y, work.Top, work.Bottom - bounds.Height);
        return bounds;
    }

    /// <summary>
    /// Save the placement as it changes rather than at exit.
    /// </summary>
    /// <remarks>
    /// Fullscreen is excluded deliberately. It resizes the window to the whole
    /// screen and un-maximises it on the way, so recording that would make every
    /// file watched fullscreen the new windowed size — and there would be no way
    /// back to a normal window short of editing the config.
    /// </remarks>
    private void RememberPlacement()
    {
        if (_restoring || _fullscreen || WindowState == FormWindowState.Minimized) return;

        var maximized = WindowState == FormWindowState.Maximized;
        var b = maximized ? RestoreBounds : Bounds;

        // Clamped on the way in as well as on the way out. Recording something
        // that will only be clamped later means the config disagrees with what
        // the player will actually do with it, which is the kind of thing that
        // makes a bug report impossible to read.
        b = Fit(b, Screen.FromControl(this).WorkingArea);
        _session.SaveWindowPlacement(b.X, b.Y, b.Width, b.Height, maximized);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        RememberPlacement();
    }

    // OnResizeEnd does not fire for maximise/restore — those are not drags.
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (WindowState != FormWindowState.Normal) RememberPlacement();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RememberPlacement();
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _fitTimer.Start();

        // Worth printing every run: the size mpv is handed is the size it
        // renders at, so a DPI mistake here is a performance bug, not just a
        // cosmetic one, and it is invisible from the video itself.
        var work = Screen.FromControl(this).WorkingArea;
        Console.WriteLine($"host window     : {DeviceDpi} dpi, video panel {_video.Width}x{_video.Height} px, " +
                          $"window {Bounds.Width}x{Bounds.Height}, work area {work.Width}x{work.Height}");

        // Says where the size came from. Without it a report of "the window
        // opened too big" cannot be told apart from "the window remembered a
        // size I set", and those need opposite fixes.
        var s = _session.WindowPlacement;
        if (s.Width <= 0)
        {
            Console.WriteLine("                  no remembered placement; opened square");
        }
        else
        {
            // Says both numbers when they differ. Printing only the remembered
            // one next to a window that is a different size is worse than
            // printing nothing — it reads like the restore failed.
            var asked = $"{s.Width}x{s.Height} at {s.X},{s.Y}";
            var applied = Bounds.Size == new Size(s.Width, s.Height) && Bounds.Location == new Point(s.X, s.Y)
                ? "" : $" -> {Bounds.Width}x{Bounds.Height} at {Bounds.X},{Bounds.Y} (clamped to the screen)";
            Console.WriteLine($"                  remembered {asked}{applied}{(s.Maximized ? ", maximised" : "")}");
        }
    }

    /// <summary>Call once the session is up, from any thread.</summary>
    public void OnSessionReady()
    {
        if (_session.Ipc is { } ipc) ipc.PropertyChanged += OnMpvProperty;
        RunOnUi(() => _menu.Enabled = true);
    }

    /// <summary>Close the window from a background thread.</summary>
    public void RequestClose() => RunOnUi(Close);

    private int _fitTicks;
    private bool _warnedNoChild;
    private bool _menuOpen;

    private void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        _session.SuppressDrag = open;
        MpvChild.SetEnabled(MpvChild.Find(_video.Handle), !open);
    }

    /// <summary>
    /// Also the one place that would notice --wid failing. mpv falls back to its
    /// own top-level window in that case, so the symptom is a black host frame
    /// with the video playing in a separate window — confusing enough to be
    /// worth an explicit message.
    /// </summary>
    private void OnFitTick()
    {
        var attached = MpvChild.FitToParent(_video.Handle, wantEnabled: !_menuOpen);
        _fitTicks++;

        // 8s, not 3: a cold mpv start on a large file takes longer than you
        // would guess, and an early false alarm here sends you debugging a
        // window that was about to appear.
        if (attached || _warnedNoChild || _fitTicks < 32 || !_session.IsReady) return;
        _warnedNoChild = true;
        Console.WriteLine();
        Console.WriteLine("  [host] mpv did not attach to the player window after 8s.");
        Console.WriteLine("         Check --wid in the launch line above, and mpv-last-run.log.");
        Console.WriteLine("         Run with --detached to fall back to mpv's own window.");
    }

    // ------------------------------------------------------- mpv -> host ---

    private void OnMpvProperty(string name, JsonElement? value)
    {
        switch (name)
        {
            case "media-title":
                var title = value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
                RunOnUi(() => Text = string.IsNullOrWhiteSpace(title)
                    ? AppInfo.Name
                    : $"{title} — {AppInfo.Name}");
                break;

            case "fullscreen":
                var fs = value?.ValueKind == JsonValueKind.True;
                RunOnUi(() => ApplyFullscreen(fs));
                break;

            case "mute": _muted = value?.ValueKind == JsonValueKind.True; break;
            case "loop-file":
                // mpv reports this as `false`, `inf`, or a repeat count.
                _loopFile = value is { } lv && lv.ValueKind != JsonValueKind.False;
                break;
            case "speed": _speed = value?.ValueKind == JsonValueKind.Number ? value.Value.GetDouble() : 1.0; break;
            case "track-list": _trackList = value; break;
            case "aid": _audioTrack = AsTrackId(value); break;
            case "sid": _subTrack = AsTrackId(value); break;
            case "playlist": _playlist = value; break;
            case "playlist-pos":
                // -1 while nothing is loaded.
                var pos = value?.ValueKind == JsonValueKind.Number ? value.Value.GetInt32() : -1;
                _playlistPos = pos < 0 ? null : pos;
                break;
        }
    }

    /// <summary>aid/sid are a number when a track is selected and `false` when not.</summary>
    private static long? AsTrackId(JsonElement? v) =>
        v is { ValueKind: JsonValueKind.Number } e ? e.GetInt64() : null;

    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { /* closing */ }
        catch (InvalidOperationException) { /* handle died between the checks */ }
    }

    // ------------------------------------------------------ host -> mpv ---

    private void Send(params object[] command) => _ = _session.Ipc?.SendAsync(command);

    private void SetProperty(string name, object value) => _ = _session.Ipc?.SetPropertyAsync(name, value);

    private void Key(string mpvKeyName) => _ = _session.Ipc?.KeyPressAsync(mpvKeyName);

    /// <summary>
    /// Everything typed at the window goes to mpv, so input.conf stays the one
    /// place keys are bound and uosc keeps its own bindings.
    ///
    /// base first: that lets a real host shortcut (Ctrl+O) win, and it is also
    /// what routes Alt+F to the menu bar. Only what nobody claimed is relayed.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (base.ProcessCmdKey(ref msg, keyData)) return true;

        // Alt belongs to the menu bar — Alt on its own opens it, Alt+letter
        // picks an entry — so it is not relayed. Alt+arrow is the exception:
        // nothing in the menu can be reached with an arrow, and those four are
        // the keyboard look bindings. Without this they were dropped here and
        // never reached the binding at all.
        var arrow = (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down;
        if ((keyData & Keys.Alt) != 0 && !arrow) return false;

        if (_menu.ContainsFocus) return false;

        var name = KeyMap.ToMpvName(keyData);
        if (name is null) return false;
        Key(name);
        return true;
    }

    private const int WmMouseWheel = 0x020A;

    /// <summary>
    /// Scroll wheel over the video zooms, by narrowing or widening the field of
    /// view.
    /// </summary>
    /// <remarks>
    /// Handled here and not by mpv, and it has to be: mpv's window is a
    /// <c>WS_DISABLED</c> child, so it receives no mouse input whatsoever and a
    /// WHEEL_UP binding in input.conf could never fire. This is the same reason
    /// drag-to-look is done host-side.
    ///
    /// Windows delivers the wheel to the focused window rather than the one
    /// under the cursor, so this arrives at the form no matter which child the
    /// pointer is over — hence the check that it really is over the video and
    /// not the menu bar.
    ///
    /// Up narrows the field of view, which is what "zoom in" means everywhere
    /// else: the subject gets bigger.
    /// </remarks>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel && !_menuOpen && _session.Mode is { } mode)
        {
            // Position comes from the message, not from Cursor.Position. It is
            // where the pointer was when the wheel turned, which is the thing
            // being asked about; the live cursor may have moved on by now. It
            // also means the path can be exercised by posting the message.
            var delta = (short)((long)m.WParam >> 16);
            var lp = (long)m.LParam;
            var where = new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
            var over = _video.RectangleToScreen(_video.ClientRectangle).Contains(where);
            if (delta != 0 && over)
            {
                _ = mode.AdjustFovAsync(delta > 0 ? -FovStepDegrees : FovStepDegrees);
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>Degrees of field of view per wheel notch.</summary>
    private const double FovStepDegrees = 5;

    // -------------------------------------------------------- fullscreen ---

    /// <summary>
    /// mpv owns the fullscreen *state* (so uosc's button and mpv's `f` binding
    /// both work), and the host owns the *effect*. Everything that toggles it
    /// writes the mpv property and waits for the change to come back here.
    /// </summary>
    private void ApplyFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;

        SuspendLayout();
        if (on)
        {
            _windowedState = WindowState;
            _windowedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _menu.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            // Explicit screen bounds rather than Maximized: a maximized
            // borderless form still leaves the taskbar on top.
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            _menu.Visible = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = _windowedBounds;
            WindowState = _windowedState;
        }
        ResumeLayout(performLayout: true);

        MpvChild.FitToParent(_video.Handle, wantEnabled: !_menuOpen);
    }

    // -------------------------------------------------------------- menu ---

    private MenuStrip BuildMenu()
    {
        // Disabled until the session connects: every handler here needs IPC,
        // and mpv takes a moment to come up.
        var menu = new MenuStrip { Enabled = false };

        menu.Items.Add(FileMenu());
        menu.Items.Add(PlaybackMenu());
        menu.Items.Add(AudioMenu());
        menu.Items.Add(SubtitleMenu());
        menu.Items.Add(VrMenu());
        menu.Items.Add(CameraMenu());
        menu.Items.Add(ViewMenu());
        menu.Items.Add(HelpMenu());
        return menu;
    }

    private ToolStripMenuItem FileMenu()
    {
        var open = Item("file.open", OpenFile);
        open.ShortcutKeys = Keys.Control | Keys.O;

        return Menu("menu.file",
            open,
            Item("file.openUrl", OpenUrl),
            new ToolStripSeparator(),
            Item("file.close", () => Send("stop")),
            new ToolStripSeparator(),
            Item("file.exit", Close));
    }

    private ToolStripMenuItem PlaybackMenu()
    {
        var loop = Item("play.loop", () => SetProperty("loop-file", _loopFile ? "no" : "inf"));

        var speed = Menu("play.speed",
            Item("play.speedDown", () => Send("multiply", "speed", 1 / 1.25)),
            Item("play.speedUp", () => Send("multiply", "speed", 1.25)),
            Item("play.speedReset", () => SetProperty("speed", 1.0)));

        var decoder = DecoderMenu();
        var renderer = RendererMenu();

        var prevFile = Shortcut(Item("play.prevFile", () => Send("playlist-prev", "weak")), "<");
        var nextFile = Shortcut(Item("play.nextFile", () => Send("playlist-next", "weak")), ">");

        var menu = Menu("menu.playback",
            Shortcut(Item("play.playPause", () => Send("cycle", "pause")), "Space"),
            Item("play.stop", () => Send("stop")),
            new ToolStripSeparator(),
            prevFile,
            nextFile,
            new ToolStripSeparator(),
            Item("play.back10", () => Send("seek", -10)),
            Item("play.forward30", () => Send("seek", 30)),
            Item("play.prevFrame", () => Send("frame-back-step")),
            Item("play.nextFrame", () => Send("frame-step")),
            new ToolStripSeparator(),
            speed,
            loop,
            new ToolStripSeparator(),
            decoder,
            renderer);

        menu.DropDownOpening += (_, _) =>
        {
            loop.Checked = _loopFile;
            speed.Text = $"{_t["play.speed"]}  ({_speed.ToString("0.##", CultureInfo.CurrentCulture)}x)";
            UpdateFileStepItems(prevFile, nextFile);

            // The label carries what mpv is actually doing, which is not always
            // what was asked for: a decoder that fails to initialise falls back
            // to software silently, and this is the only place that shows.
            _ = ShowActiveBackendsAsync(decoder, renderer);
        };
        return menu;
    }

    private async Task ShowActiveBackendsAsync(ToolStripMenuItem decoder, ToolStripMenuItem renderer)
    {
        var dec = await _session.GetActiveDecoderAsync();
        var ren = await _session.GetActiveRendererAsync();
        RunOnUi(() =>
        {
            decoder.Text = $"{_t["menu.decoder"]}  ({dec ?? "?"})";
            renderer.Text = $"{_t["menu.renderer"]}  ({ren ?? "?"})";
        });
    }

    /// <summary>
    /// Hardware decoding. Applied immediately — mpv reinitialises the decoder
    /// on the next frame — so the user can tell straight away whether a choice
    /// helped, rather than restarting the player to find out.
    /// </summary>
    private ToolStripMenuItem DecoderMenu()
    {
        var menu = Menu("menu.decoder");
        foreach (var d in VideoBackends.Decoders)
        {
            var choice = d;
            var item = Item(choice.Key, () => _ = _session.SetDecoderAsync(choice));
            menu.DropDownItems.Add(item);
        }

        menu.DropDownOpening += (_, _) =>
        {
            var configured = _session.Config.Mpv.Hwdec;
            var api = _session.CurrentGpuApi;

            for (var i = 0; i < VideoBackends.Decoders.Length; i++)
            {
                var d = VideoBackends.Decoders[i];
                var item = (ToolStripMenuItem)menu.DropDownItems[i];
                item.Checked = d.MpvValue.Equals(configured, StringComparison.OrdinalIgnoreCase);

                // Say up front which entries cannot work with the renderer in
                // use. Annotated rather than disabled: the reason is the useful
                // part, and a greyed row with no explanation just looks broken.
                item.Text = VideoBackends.Supports(d, api)
                    ? _t[d.Key]
                    : $"{_t[d.Key]}  — {string.Format(_t["dec.needs"], d.RequiresApi.Split(',')[0])}";
            }
        };
        return menu;
    }

    /// <summary>
    /// The renderer ladder, for the machine whose driver cannot do the default.
    /// This is the likeliest reason someone cannot play anything at all, and
    /// until now the only fix was editing mpv.conf by hand.
    /// </summary>
    private ToolStripMenuItem RendererMenu()
    {
        var menu = Menu("menu.renderer");
        foreach (var r in VideoBackends.Renderers)
        {
            var choice = r;
            menu.DropDownItems.Add(Item(choice.Key, () =>
            {
                _session.SetRenderer(choice);
                OfferRestart();
            }));
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(_t["ren.note"]) { Enabled = false });

        menu.DropDownOpening += (_, _) =>
        {
            var selected = _session.CurrentRendererIndex;
            for (var i = 0; i < VideoBackends.Renderers.Length; i++)
                ((ToolStripMenuItem)menu.DropDownItems[i]).Checked = i == selected;
        };
        return menu;
    }

    /// <summary>
    /// A renderer only takes effect when mpv next starts, so offer to do that
    /// now rather than leaving the user to work out that nothing happened.
    ///
    /// Restarting the whole player rather than just mpv: the alternative is
    /// tearing down and rebuilding the IPC connection, the mode controller and
    /// every observer mid-session, and a half-restarted player is a far worse
    /// failure than a one-second relaunch.
    /// </summary>
    private void OfferRestart()
    {
        if (MessageBox.Show(this, _t["ren.restartBody"], _t["ren.restartTitle"],
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        Restart();
    }

    /// <summary>Relaunch with the same file, and close this instance.</summary>
    private void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        try
        {
            var media = _session.CurrentMedia;
            var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(media)) info.ArgumentList.Add(media);
            System.Diagnostics.Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, _t["ren.restartTitle"],
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Close();
    }

    private ToolStripMenuItem AudioMenu()
    {
        var tracks = Menu("audio.track");
        var mute = Item("audio.mute", () => Send("cycle", "mute"));

        var menu = Menu("menu.audio",
            tracks,
            new ToolStripSeparator(),
            Item("audio.volUp", () => Send("add", "volume", 5)),
            Item("audio.volDown", () => Send("add", "volume", -5)),
            mute);

        menu.DropDownOpening += (_, _) =>
        {
            mute.Checked = _muted;
            FillTrackMenu(tracks, "audio", "aid", _audioTrack, canDisable: true);
        };
        return menu;
    }

    private ToolStripMenuItem SubtitleMenu()
    {
        var tracks = Menu("sub.track");

        var menu = Menu("menu.subtitle",
            tracks,
            new ToolStripSeparator(),
            Item("sub.load", LoadSubtitle));

        menu.DropDownOpening += (_, _) =>
            FillTrackMenu(tracks, "sub", "sid", _subTrack, canDisable: true);
        return menu;
    }

    private ToolStripMenuItem VrMenu()
    {
        var geometry = Menu("vr.geometry");
        var stereo = Menu("vr.stereo");
        var eye = Menu("vr.eye");

        var menu = Menu("menu.vr",
            geometry, stereo, eye,
            new ToolStripSeparator(),
            Item("vr.fovIn", () => _ = _session.Mode?.AdjustFovAsync(-5)),
            Item("vr.fovOut", () => _ = _session.Mode?.AdjustFovAsync(5)),
            Item("vr.fovReset", () => _ = _session.Mode?.SetFovAsync(120)),
            new ToolStripSeparator(),
            Shortcut(Item("vr.resetView", () => _session.Mapper.ResetView()), "Ctrl+Shift+V"),
            // Home, not Ctrl+Shift+R. Both are bound; the menu advertises the
            // one worth learning, and this is the action reached for most.
            Shortcut(Item("vr.recenter", () => _session.Mapper.RequestRecenter()), "Home"),
            new ToolStripSeparator(),
            Shortcut(Item("vr.panel", () => Key("TAB")), "Tab"));

        menu.DropDownOpening += (_, _) =>
        {
            var m = _session.Mode;
            if (m is null) return;

            geometry.DropDownItems.Clear();
            foreach (var (value, _) in ViewMode.Geometries)
            {
                var g = value;
                var it = Item(ViewMode.Key(g), () => _ = _session.Mode?.SetGeometryAsync(g));
                it.Checked = m.Geometry == g;
                geometry.DropDownItems.Add(it);
            }

            stereo.DropDownItems.Clear();
            foreach (var (value, _) in ViewMode.Stereos)
            {
                var s = value;
                var it = Item(ViewMode.Key(s), () => _ = _session.Mode?.SetStereoAsync(s));
                it.Checked = m.Stereo == s;
                // Greyed rather than hidden: "180 over/under does not exist" is
                // information, and a menu that changes length is hard to learn.
                it.Enabled = ViewMode.IsSupported(m.Geometry, s);
                stereo.DropDownItems.Add(it);
            }

            eye.DropDownItems.Clear();
            foreach (var value in new[] { Eye.Left, Eye.Right, Eye.Both })
            {
                var ey = value;
                var it = Item(ViewMode.Key(ey), () => _ = _session.Mode?.SetEyeAsync(ey));
                it.Checked = m.Eye == ey;
                it.Enabled = m.Stereo != Stereo.Mono;
                eye.DropDownItems.Add(it);
            }
            eye.Enabled = m.Stereo != Stereo.Mono;
            geometry.Text = $"{_t["vr.geometry"]}  ({_t[ViewMode.Key(m.Geometry)]})";
        };
        return menu;
    }

    /// <summary>
    /// Everything the camera drives. Its own top-level menu rather than a
    /// corner of the VR one because the camera is a distinct piece of hardware
    /// with its own failure modes, and because gesture control will land here
    /// too — at which point "VR" would be covering three unrelated things.
    /// </summary>
    private ToolStripMenuItem CameraMenu()
    {
        var tracking = Item("vr.tracking", () =>
        {
            if (!_session.SetTrackingEnabled(!_session.TrackingEnabled))
                MessageBox.Show(this, _t["cam.startFailedBody"], _t["vr.tracking"],
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });

        // Sensitivity has to be adjustable while watching: how far the view
        // swings for a given head turn depends on the screen, how far away you
        // sit, and taste. Nobody gets it right from a config file.
        var sensitivity = Menu("vr.sensitivity",
            Shortcut(Item("vr.sensLower", () => _session.AdjustGain(1 / 1.15)), "Ctrl+["),
            Shortcut(Item("vr.sensHigher", () => _session.AdjustGain(1.15)), "Ctrl+]"),
            new ToolStripSeparator(),
            Item("vr.sensReset", _session.ResetGain));

        var menu = Menu("menu.camera",
            Item("cam.test", RunCameraTest),
            new ToolStripSeparator(),
            Shortcut(tracking, "Ctrl+Shift+H"),
            sensitivity);

        menu.DropDownOpening += (_, _) =>
        {
            // Always selectable. It used to be greyed unless a source was
            // already running, which made it impossible to ever switch on:
            // turning it on is what starts the camera.
            tracking.Enabled = true;
            tracking.Checked = _session.TrackingEnabled;

            // The number is the point: "Less Sensitive" with nothing to compare
            // against gives no sense of how far you have moved or how far is left.
            sensitivity.Text = $"{_t["vr.sensitivity"]}  ({_session.YawGain:F0}°)";
        };
        return menu;
    }

    /// <summary>
    /// Opens the camera diagnostic as a second process.
    ///
    /// Not in this one: the diagnostic is an OpenCV HighGui window with its own
    /// message loop, which does not coexist with a WinForms UI thread. A
    /// separate process also means a camera or driver that hangs takes the
    /// diagnostic down and not the player.
    ///
    /// A camera can only be opened once, so this cannot run while the player is
    /// already tracking with it — say so rather than letting the child fail
    /// with a device error the user never sees.
    /// </summary>
    private void RunCameraTest()
    {
        if (_session.Camera is not null)
        {
            MessageBox.Show(this, _t["cam.busyBody"], _t["cam.test"],
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null) return;

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true };
            info.ArgumentList.Add("--camera-preview");
            System.Diagnostics.Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, _t["cam.test"],
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Selectable shapes for the video panel, as width : height.
    ///
    /// Labelled with the numbers themselves, which need no translation — only
    /// the submenu's own title goes through the string tables.
    /// </summary>
    private static readonly (string Label, double Ratio)[] Shapes =
    [
        ("1:1", 1.0),
        ("4:3", 4.0 / 3),
        ("3:2", 3.0 / 2),
        ("16:10", 16.0 / 10),
        ("16:9", 16.0 / 9),
    ];

    private ToolStripMenuItem ViewMenu()
    {
        var fullscreen = Item("view.fullscreen", () => Send("cycle", "fullscreen"));
        var onTop = Item("view.onTop", () => { TopMost = !TopMost; });

        var shape = new ToolStripMenuItem(_t["view.shape"]);
        var shapeItems = new List<(ToolStripMenuItem Item, double Ratio)>();
        foreach (var (label, ratio) in Shapes)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => ApplyShape(ratio);
            shape.DropDownItems.Add(item);
            shapeItems.Add((item, ratio));
        }

        var menu = Menu("menu.view",
            Shortcut(fullscreen, "F"),
            onTop,
            new ToolStripSeparator(),
            shape,
            new ToolStripSeparator(),
            Shortcut(Item("view.stats", () => Send("script-binding", "stats/display-page-1-toggle")), "Ctrl+Shift+I"));

        menu.DropDownOpening += (_, _) =>
        {
            fullscreen.Checked = _fullscreen;
            onTop.Checked = TopMost;

            // Ticked when the panel is already that shape. A percent of slack,
            // because a window dragged to 1:1 by hand lands a pixel or two off
            // and an entry that is never ticked is worse than no tick at all.
            var current = _video.Height > 0 ? _video.Width / (double)_video.Height : 0;
            foreach (var (item, ratio) in shapeItems)
                item.Checked = Math.Abs(current - ratio) < ratio * 0.01;
        };
        return menu;
    }

    /// <summary>
    /// Reshape the window so the video panel has the given width : height.
    /// </summary>
    /// <remarks>
    /// The panel, not the window: the menu bar's height is added on top rather
    /// than taken out of the picture, so "1:1" means the video is square rather
    /// than the window being square with a letterboxed video inside it.
    ///
    /// Width is kept and height derived, so picking a shape does not also
    /// resize. When the result would not fit, both are scaled down together --
    /// clamping only the offending axis would silently hand back a different
    /// ratio than the one just asked for.
    /// </remarks>
    private void ApplyShape(double ratio)
    {
        if (_fullscreen) return;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;

        var chrome = _menu.Height;
        var work = Screen.FromControl(this).WorkingArea;
        var frame = new Size(Width - ClientSize.Width, Height - ClientSize.Height);

        double w = _video.Width;
        var h = w / ratio;

        var k = Math.Min(1.0, Math.Min((work.Width - frame.Width) / w,
                                       (work.Height - frame.Height - chrome) / h));
        w *= k;
        h *= k;

        ClientSize = new Size((int)Math.Round(w), (int)Math.Round(h) + chrome);
        RememberPlacement();
    }

    private ToolStripMenuItem HelpMenu() => Menu("menu.help",
        Item("help.keys", () => MessageBox.Show(this, _t["keys.body"], _t["help.keys"],
                                                MessageBoxButtons.OK, MessageBoxIcon.None)),
        Item("help.about", () => MessageBox.Show(
            this,
            AppInfo.NameAndVersion + Environment.NewLine + Environment.NewLine + _t["about.body"],
            _t["help.about"], MessageBoxButtons.OK, MessageBoxIcon.None)));

    // --------------------------------------------------------- menu plumbing ---

    private ToolStripMenuItem Menu(string key, params ToolStripItem[] children)
    {
        var item = new ToolStripMenuItem(_t[key]);
        item.DropDownItems.AddRange(children);
        return item;
    }

    private ToolStripMenuItem Item(string key, Action onClick)
    {
        var item = new ToolStripMenuItem(_t[key]);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// Shows a key next to a menu entry without binding it. The binding lives in
    /// mpv's input.conf; setting ShortcutKeys here would make WinForms swallow
    /// the key before <see cref="ProcessCmdKey"/> could relay it, so the two
    /// would drift apart the moment input.conf changed.
    /// </summary>
    private static ToolStripMenuItem Shortcut(ToolStripMenuItem item, string display)
    {
        item.ShortcutKeyDisplayString = display;
        return item;
    }

    /// <summary>
    /// Rebuilds an audio or subtitle track submenu from mpv's track-list.
    /// Rebuilt on every open rather than kept in sync: tracks only change when
    /// the file does, and a stale list here would silently select the wrong id.
    /// </summary>
    private void FillTrackMenu(ToolStripMenuItem parent, string type, string property,
                               long? selected, bool canDisable)
    {
        parent.DropDownItems.Clear();

        if (canDisable)
        {
            var off = new ToolStripMenuItem(_t["dlg.trackNone"]) { Checked = selected is null };
            off.Click += (_, _) => SetProperty(property, "no");
            parent.DropDownItems.Add(off);
        }

        if (_trackList is not { ValueKind: JsonValueKind.Array } list)
        {
            parent.Enabled = false;
            return;
        }

        var found = 0;
        foreach (var track in list.EnumerateArray())
        {
            if (!track.TryGetProperty("type", out var t) || t.GetString() != type) continue;
            if (!track.TryGetProperty("id", out var idEl)) continue;

            var id = idEl.GetInt64();
            var item = new ToolStripMenuItem(DescribeTrack(track, id)) { Checked = selected == id };
            item.Click += (_, _) => SetProperty(property, id);
            parent.DropDownItems.Add(item);
            found++;
        }

        // "Off" alone is not a choice worth opening a submenu for.
        parent.Enabled = found > 0;
    }

    /// <summary>
    /// Greys Previous/Next File at the ends of the folder playlist.
    ///
    /// No list of the folder's files here on purpose: uosc's playlist panel
    /// already browses the same mpv playlist, and a second copy in the menu bar
    /// was one more thing to keep in step for no new capability.
    /// </summary>
    private void UpdateFileStepItems(ToolStripMenuItem prev, ToolStripMenuItem next)
    {
        if (_playlist is not { ValueKind: JsonValueKind.Array } list)
        {
            prev.Enabled = next.Enabled = false;
            return;
        }

        var count = list.GetArrayLength();
        var here = _playlistPos ?? -1;

        prev.Enabled = here > 0;
        next.Enabled = here >= 0 && here < count - 1;
    }

    private static string DescribeTrack(JsonElement track, long id)
    {
        var lang = Str(track, "lang");
        var codec = Str(track, "codec");

        // For a subtitle found beside the video, the filename beats mpv's title.
        // mpv titles an auto-loaded sub with whatever follows the video's base
        // name, which gives "srt" for movie.srt and "zh.srt" for movie.zh.srt --
        // measured, not guessed. The filename is what the user recognises.
        var external = track.TryGetProperty("external", out var ex) && ex.ValueKind == JsonValueKind.True;
        var file = external ? Str(track, "external-filename") : null;
        var title = (file is not null ? Path.GetFileName(file) : null) ?? Str(track, "title");
        if (string.IsNullOrWhiteSpace(title)) title = null;

        var label = (title, lang) switch
        {
            (not null, not null) => $"{title} [{lang}]",
            (not null, null) => title,
            (null, not null) => lang!,
            _ => codec ?? $"#{id}",
        };
        return $"{id}: {label.Replace("&", "&&")}";

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
    }

    // ------------------------------------------------------------ dialogs ---

    private void OpenFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = _t["file.open"],
            Filter = $"{_t["dlg.videoFiles"]}|*.mp4;*.mkv;*.webm;*.mov;*.m4v;*.avi;*.ts;*.wmv;*.flv" +
                     $"|{_t["dlg.allFiles"]}|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Send("loadfile", dlg.FileName);
    }

    private void LoadSubtitle()
    {
        using var dlg = new OpenFileDialog
        {
            Title = _t["sub.load"],
            Filter = $"{_t["dlg.subFiles"]}|*.srt;*.ass;*.ssa;*.sub;*.vtt|{_t["dlg.allFiles"]}|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Send("sub-add", dlg.FileName, "select");
    }

    private void OpenUrl()
    {
        var url = PromptForUrl();
        if (!string.IsNullOrWhiteSpace(url)) Send("loadfile", url.Trim());
    }

    /// <summary>
    /// A one-field dialog, built by hand because WinForms has no input box and
    /// pulling in Microsoft.VisualBasic for one prompt is not worth it.
    /// </summary>
    private string? PromptForUrl()
    {
        using var form = new Form
        {
            Text = _t["dlg.urlTitle"],
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(520, 118),
        };
        var label = new Label { Text = _t["dlg.urlPrompt"], AutoSize = true, Location = new Point(12, 15) };
        var box = new TextBox { Location = new Point(12, 38), Width = 496, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        var ok = new Button { Text = _t["dlg.ok"], DialogResult = DialogResult.OK, Location = new Point(332, 76), AutoSize = true };
        var cancel = new Button { Text = _t["dlg.cancel"], DialogResult = DialogResult.Cancel, Location = new Point(428, 76), AutoSize = true };

        form.Controls.AddRange([label, box, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    // ------------------------------------------------------------ shutdown ---

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fitTimer.Stop();
            _fitTimer.Dispose();
            if (_session.Ipc is { } ipc) ipc.PropertyChanged -= OnMpvProperty;
        }
        base.Dispose(disposing);
    }
}
