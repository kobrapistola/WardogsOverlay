using System.Diagnostics;
using static WarDogsOverlay.Native;

namespace WarDogsOverlay;

/// <summary>
/// Finds the game window, owns one <see cref="SegmentOverlay"/> per enabled segment,
/// and keeps them positioned and shown/hidden as the game moves, loses focus or restarts.
/// </summary>
internal sealed class OverlayManager : IDisposable
{
    private readonly List<SegmentOverlay> _overlays = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly WinEventDelegate _foregroundProc; // held so the GC can't collect the callback
    private IntPtr _foregroundHook;

    private IntPtr _game;
    private uint _gamePid;
    private bool _shown;

    // Last mapping from reference space to screen space (used to convert drags back).
    private Point _origin;
    private double _sx = 1, _sy = 1;

    public OverlayProfile Profile { get; private set; }
    public bool Running { get; private set; }
    public bool EditMode { get; private set; }
    public bool UserHidden { get; private set; }
    public Segment? Selected { get; private set; }
    public IntPtr GameWindow => _game;
    public string GameDescription { get; private set; } = "";

    public event Action? StatusChanged;
    public event Action<Segment>? SegmentEdited;
    public event Action<Segment>? SegmentSelected;

    public OverlayManager(OverlayProfile profile)
    {
        Profile = profile;
        _foregroundProc = OnForegroundChanged;
        _timer.Tick += (_, _) => Tick();
    }

    public void SetProfile(OverlayProfile profile)
    {
        Profile = profile;
        Selected = null;
        DisposeOverlays();
        _game = IntPtr.Zero; // process names may differ between profiles
        _gamePid = 0;
        GameDescription = "";
        if (Running) Tick();
        StatusChanged?.Invoke();
    }

    public void Start()
    {
        if (Running) return;
        Running = true;
        if (_foregroundHook == IntPtr.Zero)
            _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _timer.Start();
        Tick();
        StatusChanged?.Invoke();
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        _timer.Stop();
        DisposeOverlays();
        _game = IntPtr.Zero;
        _gamePid = 0;
        GameDescription = "";
        StatusChanged?.Invoke();
    }

    public void SetEditMode(bool on)
    {
        EditMode = on;
        foreach (var o in _overlays) o.SetEditMode(on, o.Segment == Selected);
        UpdateLayout();
        StatusChanged?.Invoke();
    }

    public void ToggleHidden()
    {
        UserHidden = !UserHidden;
        UpdateLayout();
        StatusChanged?.Invoke();
    }

    public void Select(Segment? seg)
    {
        Selected = seg;
        if (EditMode)
        {
            foreach (var o in _overlays) o.SetEditMode(true, o.Segment == seg);
            UpdateLayout();
        }
    }

    /// <summary>Recreates overlay windows (call after segments are added, removed or enabled/disabled).</summary>
    public void Rebuild()
    {
        DisposeOverlays();
        if (!Running || _game == IntPtr.Zero) return;

        foreach (var seg in Profile.Segments.Where(s => s.Enabled))
        {
            var o = new SegmentOverlay(seg);
            _ = o.Handle; // create the window so the thumbnail has a destination
            if (!o.Attach(_game)) { o.CloseForGood(); o.Dispose(); continue; }
            o.SelectedByUser += OnOverlaySelected;
            o.Moved += OnOverlayMoved;
            o.ScaleNudged += OnOverlayScaled;
            o.Nudged += OnOverlayNudged;
            o.SetEditMode(EditMode, seg == Selected);
            _overlays.Add(o);
        }
        _shown = false;
        UpdateLayout();
    }

    /// <summary>Re-applies positions, crops and visibility (call after any value edit).</summary>
    public void UpdateLayout()
    {
        if (!Running || _game == IntPtr.Zero || _overlays.Count == 0) return;

        bool visible = !UserHidden && IsWindow(_game) && !IsIconic(_game) &&
                       (EditMode || !Profile.AutoHideWhenGameNotFocused || IsGameForeground());
        if (!visible)
        {
            if (_shown) foreach (var o in _overlays) o.Hide();
            _shown = false;
            return;
        }

        if (!GetClientRect(_game, out var cr)) return;
        var pt = new POINT();
        if (!ClientToScreen(_game, ref pt)) return;
        int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
        if (cw <= 0 || ch <= 0) return;

        _origin = new Point(pt.X, pt.Y);
        _sx = (double)cw / Math.Max(1, Profile.ReferenceWidth);
        _sy = (double)ch / Math.Max(1, Profile.ReferenceHeight);

        foreach (var o in _overlays)
        {
            var s = o.Segment;
            var src = Map(s.Source, Point.Empty);
            var dest = Map(s.Dest, _origin);
            byte alpha = (byte)Math.Clamp((int)Math.Round(s.Opacity * 2.55), 25, 255);
            o.Apply(src, dest, alpha);
            if (!o.Visible) o.Show();
        }
        _shown = true;
    }

    private Rectangle Map(Rectangle r, Point offset) => new(
        offset.X + (int)Math.Round(r.X * _sx),
        offset.Y + (int)Math.Round(r.Y * _sy),
        Math.Max(1, (int)Math.Round(r.Width * _sx)),
        Math.Max(1, (int)Math.Round(r.Height * _sy)));

    private void Tick()
    {
        if (!Running) return;
        if (_game == IntPtr.Zero || !IsWindow(_game))
        {
            var found = FindGame(out var pid, out var desc);
            if (found != _game)
            {
                _game = found;
                _gamePid = pid;
                GameDescription = desc;
                Rebuild();
                StatusChanged?.Invoke();
                return;
            }
        }
        UpdateLayout();
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        UpdateLayout();
        if (_shown) foreach (var o in _overlays) o.EnsureTopmost();
    }

    private bool IsGameForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == _game) return true;
        if (fg == IntPtr.Zero || _gamePid == 0) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == _gamePid;
    }

    private IntPtr FindGame(out uint pid, out string description)
    {
        pid = 0;
        description = "";
        int self = Environment.ProcessId;

        foreach (var raw in Profile.ProcessNames)
        {
            var name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) continue;

            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    var h = p.MainWindowHandle;
                    if (p.Id != self && h != IntPtr.Zero && IsWindowVisible(h))
                    {
                        pid = (uint)p.Id;
                        description = $"{p.ProcessName}.exe (PID {p.Id})";
                        return h;
                    }
                }
                catch { /* process exited or access denied */ }
                finally { p.Dispose(); }
            }
        }

        var title = Profile.WindowTitle?.Trim() ?? "";
        if (title.Length == 0) return IntPtr.Zero;

        IntPtr hit = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (!GetTitle(h).Trim().Equals(title, StringComparison.OrdinalIgnoreCase)) return true;
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid == self) return true;
            hit = h;
            return false;
        }, IntPtr.Zero);

        if (hit != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hit, out pid);
            description = $"window \"{title}\" (PID {pid})";
        }
        return hit;
    }

    // ---- Edit-mode callbacks from overlays ------------------------------------------

    private void OnOverlaySelected(SegmentOverlay o)
    {
        if (Selected == o.Segment) return;
        Select(o.Segment);
        SegmentSelected?.Invoke(o.Segment);
    }

    private void OnOverlayMoved(SegmentOverlay o, Rectangle screenBounds)
    {
        var s = o.Segment;
        double cx = screenBounds.X + screenBounds.Width / 2.0 - _origin.X;
        double cy = screenBounds.Y + screenBounds.Height / 2.0 - _origin.Y;
        s.DestCenterX = (int)Math.Round(cx / _sx);
        s.DestCenterY = (int)Math.Round(cy / _sy);
        UpdateLayout();
        SegmentEdited?.Invoke(s);
    }

    private void OnOverlayScaled(SegmentOverlay o, double delta)
    {
        var s = o.Segment;
        s.Scale = Math.Clamp(Math.Round(s.Scale + delta, 2), 0.05, 3.0);
        UpdateLayout();
        SegmentEdited?.Invoke(s);
    }

    private void OnOverlayNudged(SegmentOverlay o, int dx, int dy)
    {
        var s = o.Segment;
        s.DestCenterX += dx;
        s.DestCenterY += dy;
        UpdateLayout();
        SegmentEdited?.Invoke(s);
    }

    private void DisposeOverlays()
    {
        foreach (var o in _overlays)
        {
            o.CloseForGood();
            o.Dispose();
        }
        _overlays.Clear();
        _shown = false;
    }

    public void Dispose()
    {
        Stop();
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        _timer.Dispose();
    }
}
