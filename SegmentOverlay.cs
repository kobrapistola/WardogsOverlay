using static WarDogsOverlay.Native;

namespace WarDogsOverlay;

/// <summary>
/// One borderless, always-on-top window showing a live DWM thumbnail of a cropped region
/// of the game. Click-through in play mode; draggable/scalable in edit mode.
/// </summary>
internal sealed class SegmentOverlay : Form
{
    private const int EditFrame = 2;

    private IntPtr _thumb;
    private bool _edit;
    private bool _allowClose;

    private bool _applied;
    private Rectangle _lastSrc, _lastDest;
    private byte _lastAlpha = 255;

    private bool _dragging;
    private Point _dragCursor, _dragOrigin;

    public Segment Segment { get; }

    public event Action<SegmentOverlay>? SelectedByUser;
    public event Action<SegmentOverlay, Rectangle>? Moved;     // new screen bounds after a drag
    public event Action<SegmentOverlay, double>? ScaleNudged;  // +/- scale step
    public event Action<SegmentOverlay, int, int>? Nudged;     // dx, dy in reference px

    public SegmentOverlay(Segment segment)
    {
        Segment = segment;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Text = segment.Name;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
    }

    public bool Attach(IntPtr gameWindow)
    {
        Detach();
        if (DwmRegisterThumbnail(Handle, gameWindow, out var thumb) != 0) return false;
        _thumb = thumb;
        _applied = false;
        return true;
    }

    public void Detach()
    {
        if (_thumb == IntPtr.Zero) return;
        DwmUnregisterThumbnail(_thumb);
        _thumb = IntPtr.Zero;
    }

    /// <summary>Switches between click-through play mode and interactive edit mode.</summary>
    public void SetEditMode(bool edit, bool selected)
    {
        _edit = edit;
        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        if (edit) ex &= ~(long)(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        else ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // In edit mode the window background shows as a frame around the inset thumbnail.
        BackColor = !edit ? Color.Black : selected ? Color.White : Segment.DrawColor;
        Cursor = edit ? Cursors.SizeAll : Cursors.Default;
        _applied = false;
        Invalidate();
    }

    /// <summary>Positions the window (screen px) and points the thumbnail at the source crop (game client px).</summary>
    public void Apply(Rectangle source, Rectangle dest, byte alpha)
    {
        if (_applied && source == _lastSrc && dest == _lastDest && alpha == _lastAlpha) return;

        if (!_dragging)
            SetWindowPos(Handle, HWND_TOPMOST, dest.X, dest.Y, dest.Width, dest.Height, SWP_NOACTIVATE);

        if (alpha != _lastAlpha || !_applied)
            SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);

        if (_thumb != IntPtr.Zero)
        {
            int inset = _edit ? EditFrame : 0;
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY |
                          DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT(inset, inset,
                    Math.Max(inset + 1, dest.Width - inset), Math.Max(inset + 1, dest.Height - inset)),
                rcSource = RECT.From(source),
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = true,
            };
            DwmUpdateThumbnailProperties(_thumb, ref props);
        }

        _lastSrc = source;
        _lastDest = dest;
        _lastAlpha = alpha;
        _applied = true;
    }

    public void EnsureTopmost() =>
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 on a focused overlay in edit mode shouldn't destroy it.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Detach();
        base.OnFormClosed(e);
    }

    // ---- Edit-mode interaction -------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_edit || e.Button != MouseButtons.Left) return;
        SelectedByUser?.Invoke(this);
        _dragging = true;
        _dragCursor = Cursor.Position;
        _dragOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var c = Cursor.Position;
        SetWindowPos(Handle, HWND_TOPMOST,
            _dragOrigin.X + c.X - _dragCursor.X, _dragOrigin.Y + c.Y - _dragCursor.Y,
            0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        Moved?.Invoke(this, Bounds);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_edit || e.Delta == 0) return;
        SelectedByUser?.Invoke(this);
        double step = (ModifierKeys & Keys.Shift) != 0 ? 0.01 : 0.05;
        ScaleNudged?.Invoke(this, Math.Sign(e.Delta) * step);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_edit)
        {
            int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: Nudged?.Invoke(this, -step, 0); return true;
                case Keys.Right: Nudged?.Invoke(this, step, 0); return true;
                case Keys.Up: Nudged?.Invoke(this, 0, -step); return true;
                case Keys.Down: Nudged?.Invoke(this, 0, step); return true;
                case Keys.Add:
                case Keys.Oemplus: ScaleNudged?.Invoke(this, 0.05); return true;
                case Keys.Subtract:
                case Keys.OemMinus: ScaleNudged?.Invoke(this, -0.05); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
