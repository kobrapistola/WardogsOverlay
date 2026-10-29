using System.Diagnostics;
using System.Drawing.Drawing2D;
using static WarDogsOverlay.Native;

namespace WarDogsOverlay;

/// <summary>Control panel: profile picker, start/stop, edit mode, layout preview and segment editor.</summary>
internal sealed class MainForm : Form
{
    private const int HotkeyId = 0x5744;

    private static readonly string[] Palette =
        { "#C80014", "#FFE628", "#14A0FF", "#1EC85A", "#E628E6", "#FF8C1E", "#28E6E6", "#F0F0F0" };

    private OverlayProfile _profile;
    private readonly OverlayManager _mgr;
    private Image? _previewImage;
    private bool _dirty, _loading, _hotkeyOk;

    // Preview mapping (reference px -> preview px), refreshed on every paint.
    private float _pk = 1, _pox, _poy;

    private ComboBox _profiles = null!;
    private Button _start = null!, _stop = null!;
    private CheckBox _edit = null!;
    private Label _status = null!, _sizeInfo = null!, _help = null!;
    private PictureBox _preview = null!;
    private CheckedListBox _list = null!;
    private TextBox _name = null!, _color = null!;
    private Panel _swatch = null!;
    private Button _pickColor = null!;
    private NumericUpDown _srcX = null!, _srcY = null!, _srcW = null!, _srcH = null!;
    private NumericUpDown _dstX = null!, _dstY = null!, _scale = null!, _opacity = null!;
    private TextBox _procNames = null!, _winTitle = null!;
    private NumericUpDown _refW = null!, _refH = null!;
    private CheckBox _autoHide = null!;

    public MainForm()
    {
        ProfileStore.EnsureDefault();
        var names = ProfileStore.List();
        _profile = names.Count > 0 ? SafeLoad(names[0]) : ProfileStore.LoadEmbeddedDefault();

        _mgr = new OverlayManager(_profile);
        _mgr.StatusChanged += UpdateStatus;
        _mgr.SegmentEdited += OnSegmentEditedFromOverlay;
        _mgr.SegmentSelected += SelectSegment;

        BuildUi();
        ReloadProfileList(_profile.Name);
        BindProfile();
        UpdateStatus();
        UpdateTitle();
    }

    private Segment? Current =>
        _list.SelectedIndex >= 0 && _list.SelectedIndex < _profile.Segments.Count
            ? _profile.Segments[_list.SelectedIndex]
            : null;

    // ==================================================================================
    // UI construction
    // ==================================================================================

    private void BuildUi()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(920, 880);
        MinimumSize = new Size(760, 700);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // --- Row 0: profiles ---
        var profileBar = Flow();
        _profiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        _profiles.SelectedIndexChanged += (_, _) => OnProfileChosen();
        profileBar.Controls.Add(Lbl("Profile"));
        profileBar.Controls.Add(_profiles);
        profileBar.Controls.Add(Btn("Save", (_, _) => SaveProfile()));
        profileBar.Controls.Add(Btn("Save as…", (_, _) => SaveProfileAs()));
        profileBar.Controls.Add(Btn("Reset to default", (_, _) => ResetToDefault()));
        profileBar.Controls.Add(Btn("Preview image…", (_, _) => ChoosePreviewImage()));
        profileBar.Controls.Add(Btn("Open folder", (_, _) => OpenProfilesFolder()));

        // --- Row 1: run controls ---
        var runBar = Flow();
        _start = Btn("Start", (_, _) => _mgr.Start());
        _stop = Btn("Stop", (_, _) => { _edit.Checked = false; _mgr.Stop(); });
        _edit = new CheckBox { Text = "Edit layout", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(3) };
        _edit.CheckedChanged += (_, _) =>
        {
            if (_edit.Checked && !_mgr.Running) _mgr.Start();
            _mgr.SetEditMode(_edit.Checked);
        };
        _status = new Label { AutoSize = true, Margin = new Padding(10, 8, 3, 3) };
        runBar.Controls.AddRange(new Control[] { _start, _stop, _edit, _status });

        // --- Row 2: preview ---
        _preview = new PictureBox { Dock = DockStyle.Fill, MinimumSize = new Size(400, 180), Cursor = Cursors.Hand };
        _preview.Paint += PaintPreview;
        _preview.Resize += (_, _) => _preview.Invalidate();
        _preview.MouseDown += OnPreviewClick;

        // --- Row 3: segment list + editor ---
        var bottom = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var listCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        listCol.Controls.Add(Lbl("Segments (checkbox = enabled)"));
        _list = new CheckedListBox { Width = 250, Height = 190, CheckOnClick = false, IntegralHeight = false };
        _list.SelectedIndexChanged += (_, _) => OnListSelection();
        _list.ItemCheck += OnItemCheck;
        listCol.Controls.Add(_list);
        var listBtns = Flow();
        listBtns.Controls.Add(Btn("Add", (_, _) => AddSegment()));
        listBtns.Controls.Add(Btn("Duplicate", (_, _) => DuplicateSegment()));
        listBtns.Controls.Add(Btn("Remove", (_, _) => RemoveSegment()));
        listCol.Controls.Add(listBtns);

        var editor = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Margin = new Padding(16, 0, 0, 0) };
        for (int i = 0; i < 4; i++) editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _name = new TextBox { Width = 320 };
        _name.TextChanged += (_, _) => OnEditorChanged();
        _color = new TextBox { Width = 90 };
        _color.TextChanged += (_, _) => OnEditorChanged();
        _swatch = new Panel { Size = new Size(22, 22), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3, 4, 3, 3) };
        _pickColor = Btn("Pick…", (_, _) => PickColor());
        var colorRow = Flow();
        colorRow.Controls.AddRange(new Control[] { _color, _swatch, _pickColor });

        _srcX = Num(0, 20000); _srcY = Num(0, 20000);
        _srcW = Num(1, 20000); _srcH = Num(1, 20000);
        _dstX = Num(-20000, 20000); _dstY = Num(-20000, 20000);
        _scale = Num(5, 300); _opacity = Num(10, 100);
        _sizeInfo = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3) };

        AddRow(editor, "Name", _name, span: true);
        AddRow(editor, "Border color", colorRow, span: true);
        AddPair(editor, "Source X", _srcX, "Source Y", _srcY);
        AddPair(editor, "Source width", _srcW, "Source height", _srcH);
        AddPair(editor, "Clone center X", _dstX, "Clone center Y", _dstY);
        AddPair(editor, "Scale %", _scale, "Opacity %", _opacity);
        AddRow(editor, "", _sizeInfo, span: true);

        bottom.Controls.Add(listCol, 0, 0);
        bottom.Controls.Add(editor, 1, 0);

        // --- Row 4: game & behaviour ---
        var game = new GroupBox { Text = "Game and behavior", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var gameGrid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill };
        for (int i = 0; i < 4; i++) gameGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _procNames = new TextBox { Width = 320 };
        _procNames.TextChanged += (_, _) => OnGlobalChanged();
        _winTitle = new TextBox { Width = 160 };
        _winTitle.TextChanged += (_, _) => OnGlobalChanged();
        _refW = NumG(320, 16000); _refH = NumG(200, 8000);
        _autoHide = new CheckBox { Text = "Hide overlays when the game isn't the active window", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        _autoHide.CheckedChanged += (_, _) => OnGlobalChanged();
        AddPair(gameGrid, "Process names", _procNames, "Window title", _winTitle);
        AddPair(gameGrid, "Reference width", _refW, "Reference height", _refH);
        AddRow(gameGrid, "", _autoHide, span: true);
        game.Controls.Add(gameGrid);

        // --- Row 5: help ---
        _help = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 0), MaximumSize = new Size(880, 0) };

        root.Controls.Add(profileBar, 0, 0);
        root.Controls.Add(runBar, 0, 1);
        root.Controls.Add(_preview, 0, 2);
        root.Controls.Add(bottom, 0, 3);
        root.Controls.Add(game, 0, 4);
        root.Controls.Add(_help, 0, 5);
        Controls.Add(root);

        ResumeLayout(false);
        PerformLayout();
    }

    private static FlowLayoutPanel Flow() =>
        new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };

    private static Label Lbl(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

    private static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
        b.Click += onClick;
        return b;
    }

    private NumericUpDown Num(int min, int max)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Width = 90, TextAlign = HorizontalAlignment.Right };
        n.ValueChanged += (_, _) => OnEditorChanged();
        return n;
    }

    private NumericUpDown NumG(int min, int max)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Width = 90, TextAlign = HorizontalAlignment.Right };
        n.ValueChanged += (_, _) => OnGlobalChanged();
        return n;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control c, bool span)
    {
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(Lbl(label), 0, row);
        t.Controls.Add(c, 1, row);
        if (span) t.SetColumnSpan(c, 3);
    }

    private static void AddPair(TableLayoutPanel t, string l1, Control c1, string l2, Control c2)
    {
        int row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(Lbl(l1), 0, row);
        t.Controls.Add(c1, 1, row);
        var second = Lbl(l2);
        second.Margin = new Padding(16, 7, 3, 3);
        t.Controls.Add(second, 2, row);
        t.Controls.Add(c2, 3, row);
    }

    private static void SetNum(NumericUpDown n, decimal v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);

    // ==================================================================================
    // Profile handling
    // ==================================================================================

    private OverlayProfile SafeLoad(string name)
    {
        try { return ProfileStore.Load(name); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read profile \"{name}\":\n{ex.Message}\n\nLoading the built-in default instead.",
                "WarDogs Overlay", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            var d = ProfileStore.LoadEmbeddedDefault();
            d.Name = name;
            return d;
        }
    }

    private void ReloadProfileList(string select)
    {
        _loading = true;
        _profiles.Items.Clear();
        foreach (var n in ProfileStore.List()) _profiles.Items.Add(n);
        if (!_profiles.Items.Contains(select)) _profiles.Items.Add(select);
        _profiles.SelectedItem = select;
        _loading = false;
    }

    private void BindProfile()
    {
        _loading = true;

        _previewImage?.Dispose();
        _previewImage = null;
        var img = ProfileStore.PreviewPathFor(_profile.Name);
        if (File.Exists(img))
        {
            try { using var tmp = Image.FromFile(img); _previewImage = new Bitmap(tmp); }
            catch { /* bad image: draw without it */ }
        }

        _procNames.Text = string.Join(", ", _profile.ProcessNames);
        _winTitle.Text = _profile.WindowTitle;
        SetNum(_refW, _profile.ReferenceWidth);
        SetNum(_refH, _profile.ReferenceHeight);
        _autoHide.Checked = _profile.AutoHideWhenGameNotFocused;

        _loading = false;
        RefreshList(_profile.Segments.Count > 0 ? 0 : -1);
    }

    private void OnProfileChosen()
    {
        if (_loading || _profiles.SelectedItem is not string name) return;
        if (name == _profile.Name) return;
        if (!ConfirmDiscard())
        {
            _loading = true;
            _profiles.SelectedItem = _profile.Name;
            _loading = false;
            return;
        }
        _profile = SafeLoad(name);
        _mgr.SetProfile(_profile);
        BindProfile();
        _dirty = false;
        UpdateTitle();
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, $"Save changes to \"{_profile.Name}\"?", "WarDogs Overlay",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) SaveProfile();
        return true;
    }

    private void SaveProfile()
    {
        try
        {
            ProfileStore.Save(_profile);
            _dirty = false;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save:\n{ex.Message}", "WarDogs Overlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveProfileAs()
    {
        var name = Prompt(this, "Save profile as", "Profile name:", _profile.Name + " copy");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = ProfileStore.Sanitize(name);
        if (File.Exists(ProfileStore.PathFor(name)) &&
            MessageBox.Show(this, $"\"{name}\" already exists. Overwrite it?", "WarDogs Overlay",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var oldPreview = ProfileStore.PreviewPathFor(_profile.Name);
        var newPreview = ProfileStore.PreviewPathFor(name);
        if (File.Exists(oldPreview) && oldPreview != newPreview) File.Copy(oldPreview, newPreview, true);

        _profile.Name = name;
        SaveProfile();
        ReloadProfileList(name);
    }

    private void ResetToDefault()
    {
        if (MessageBox.Show(this, $"Replace \"{_profile.Name}\" with the built-in default layout?\n(Nothing is written until you click Save.)",
                "WarDogs Overlay", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var d = ProfileStore.LoadEmbeddedDefault();
        d.Name = _profile.Name;
        _profile = d;
        _mgr.SetProfile(_profile);
        BindProfile();
        MarkDirty();
    }

    private void ChoosePreviewImage()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Pick a full-screen game screenshot for the preview",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var src = Image.FromFile(dlg.FileName);
            int w = Math.Min(1600, src.Width);
            int h = (int)Math.Round(src.Height * (w / (double)src.Width));
            using var small = new Bitmap(w, h);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            small.Save(ProfileStore.PreviewPathFor(_profile.Name), System.Drawing.Imaging.ImageFormat.Png);
            _previewImage?.Dispose();
            _previewImage = new Bitmap(small);
            _preview.Invalidate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't use that image:\n{ex.Message}", "WarDogs Overlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenProfilesFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProfileStore.Folder}\"") { UseShellExecute = true });

    private void OnGlobalChanged()
    {
        if (_loading) return;
        _profile.ProcessNames = _procNames.Text
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _profile.WindowTitle = _winTitle.Text.Trim();
        _profile.ReferenceWidth = (int)_refW.Value;
        _profile.ReferenceHeight = (int)_refH.Value;
        _profile.AutoHideWhenGameNotFocused = _autoHide.Checked;
        MarkDirty();
        _mgr.UpdateLayout();
        _preview.Invalidate();
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() =>
        Text = $"WarDogs HUD Overlay — {_profile.Name}{(_dirty ? " *" : "")}";

    // ==================================================================================
    // Segment list and editor
    // ==================================================================================

    private void RefreshList(int select)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var s in _profile.Segments) _list.Items.Add(s.Name, s.Enabled);
        _list.SelectedIndex = select >= 0 && select < _list.Items.Count ? select : -1;
        _loading = false;
        OnListSelection();
    }

    private void OnListSelection()
    {
        if (_loading) return;
        LoadEditor();
        _mgr.Select(Current);
        _preview.Invalidate();
    }

    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_loading || e.Index < 0 || e.Index >= _profile.Segments.Count) return;
        _profile.Segments[e.Index].Enabled = e.NewValue == CheckState.Checked;
        MarkDirty();
        // ItemCheck fires before the box updates; rebuild once it has.
        BeginInvoke(new Action(() => { _mgr.Rebuild(); _preview.Invalidate(); }));
    }

    private void LoadEditor()
    {
        var s = Current;
        _loading = true;
        foreach (Control c in new Control[] { _name, _color, _pickColor, _srcX, _srcY, _srcW, _srcH, _dstX, _dstY, _scale, _opacity })
            c.Enabled = s != null;
        if (s != null)
        {
            _name.Text = s.Name;
            _color.Text = s.Color;
            _swatch.BackColor = s.DrawColor;
            SetNum(_srcX, s.SrcX); SetNum(_srcY, s.SrcY);
            SetNum(_srcW, s.SrcW); SetNum(_srcH, s.SrcH);
            SetNum(_dstX, s.DestCenterX); SetNum(_dstY, s.DestCenterY);
            SetNum(_scale, (decimal)Math.Round(s.Scale * 100));
            SetNum(_opacity, s.Opacity);
        }
        else
        {
            _name.Text = "";
            _color.Text = "";
            _swatch.BackColor = SystemColors.Control;
        }
        _loading = false;
        UpdateSizeInfo();
    }

    private void OnEditorChanged()
    {
        if (_loading) return;
        var s = Current;
        if (s == null) return;

        s.Name = _name.Text;
        s.Color = _color.Text.Trim();
        s.SrcX = (int)_srcX.Value; s.SrcY = (int)_srcY.Value;
        s.SrcW = (int)_srcW.Value; s.SrcH = (int)_srcH.Value;
        s.DestCenterX = (int)_dstX.Value; s.DestCenterY = (int)_dstY.Value;
        s.Scale = (double)_scale.Value / 100.0;
        s.Opacity = (int)_opacity.Value;
        _swatch.BackColor = s.DrawColor;

        int i = _list.SelectedIndex;
        if (i >= 0 && (string)_list.Items[i] != s.Name)
        {
            _loading = true;
            _list.Items[i] = s.Name;
            _list.SetItemChecked(i, s.Enabled);
            _list.SelectedIndex = i;
            _loading = false;
        }

        MarkDirty();
        _mgr.Select(s);       // refreshes edit-mode frame colors
        _mgr.UpdateLayout();
        _preview.Invalidate();
        UpdateSizeInfo();
    }

    private void UpdateSizeInfo()
    {
        var s = Current;
        _sizeInfo.Text = s == null ? "" :
            $"Source {s.SrcW} × {s.SrcH}  →  clone {s.Dest.Width} × {s.Dest.Height} px (at reference resolution)";
    }

    private void OnSegmentEditedFromOverlay(Segment s)
    {
        if (s != Current) SelectSegment(s);
        else LoadEditor();
        MarkDirty();
        _preview.Invalidate();
    }

    private void SelectSegment(Segment s)
    {
        int idx = _profile.Segments.IndexOf(s);
        if (idx >= 0 && _list.SelectedIndex != idx) _list.SelectedIndex = idx;
    }

    private void AddSegment()
    {
        var s = new Segment
        {
            Name = $"Segment {_profile.Segments.Count + 1}",
            Color = Palette[_profile.Segments.Count % Palette.Length],
            SrcX = _profile.ReferenceWidth / 2 - 100,
            SrcY = _profile.ReferenceHeight / 2 - 50,
            DestCenterX = _profile.ReferenceWidth / 2,
            DestCenterY = _profile.ReferenceHeight / 2,
        };
        _profile.Segments.Add(s);
        RefreshList(_profile.Segments.Count - 1);
        _mgr.Rebuild();
        MarkDirty();
    }

    private void DuplicateSegment()
    {
        var cur = Current;
        if (cur == null) return;
        var s = ProfileStore.Clone(cur);
        s.Name = cur.Name + " copy";
        s.DestCenterX += 40;
        s.DestCenterY += 40;
        _profile.Segments.Insert(_list.SelectedIndex + 1, s);
        RefreshList(_list.SelectedIndex + 1);
        _mgr.Rebuild();
        MarkDirty();
    }

    private void RemoveSegment()
    {
        var cur = Current;
        if (cur == null) return;
        if (MessageBox.Show(this, $"Remove \"{cur.Name}\"?", "WarDogs Overlay",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int i = _list.SelectedIndex;
        _profile.Segments.RemoveAt(i);
        RefreshList(Math.Min(i, _profile.Segments.Count - 1));
        _mgr.Rebuild();
        MarkDirty();
    }

    private void PickColor()
    {
        var s = Current;
        if (s == null) return;
        using var dlg = new ColorDialog { Color = s.DrawColor, FullOpen = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var c = dlg.Color;
        _color.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // ==================================================================================
    // Preview
    // ==================================================================================

    private RectangleF ToPreview(Rectangle r) => new(_pox + r.X * _pk, _poy + r.Y * _pk, r.Width * _pk, r.Height * _pk);

    private void PaintPreview(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.Clear(Color.FromArgb(22, 22, 26));

        float rw = Math.Max(1, _profile.ReferenceWidth), rh = Math.Max(1, _profile.ReferenceHeight);
        _pk = Math.Min((_preview.Width - 4) / rw, (_preview.Height - 4) / rh);
        if (_pk <= 0) return;
        _pox = (_preview.Width - rw * _pk) / 2f;
        _poy = (_preview.Height - rh * _pk) / 2f;
        var frame = new RectangleF(_pox, _poy, rw * _pk, rh * _pk);

        if (_previewImage != null) g.DrawImage(_previewImage, frame);
        else using (var b = new SolidBrush(Color.FromArgb(40, 40, 46))) g.FillRectangle(b, frame);

        // Centered 16:9 area: the part of a 32:9 screen you can see without turning your head.
        float zoneW = Math.Min(rw, rh * 16f / 9f);
        using (var zp = new Pen(Color.FromArgb(110, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(zp, _pox + (rw - zoneW) / 2f * _pk, _poy, zoneW * _pk, rh * _pk);
        g.DrawRectangle(Pens.DimGray, frame.X, frame.Y, frame.Width, frame.Height);

        var cur = Current;
        foreach (var s in _profile.Segments)
        {
            var col = s.Enabled ? s.DrawColor : Color.FromArgb(90, s.DrawColor);
            var src = ToPreview(s.Source);
            var dst = ToPreview(s.Dest);

            // Draw the clone itself from the screenshot, like the real overlay will.
            if (_previewImage != null && s.Enabled)
            {
                float ix = _previewImage.Width / rw, iy = _previewImage.Height / rh;
                var imgSrc = new RectangleF(s.SrcX * ix, s.SrcY * iy, s.SrcW * ix, s.SrcH * iy);
                g.DrawImage(_previewImage, dst, imgSrc, GraphicsUnit.Pixel);
            }

            using (var link = new Pen(Color.FromArgb(s.Enabled ? 110 : 40, s.DrawColor), 1f))
                g.DrawLine(link, Center(src), Center(dst));
            using (var thin = new Pen(col, 1.5f) { DashStyle = DashStyle.Dot })
                g.DrawRectangle(thin, src.X, src.Y, src.Width, src.Height);
            bool sel = s == cur;
            using (var thick = new Pen(sel ? Color.White : col, sel ? 2.5f : 1.75f))
                g.DrawRectangle(thick, dst.X - 1, dst.Y - 1, dst.Width + 2, dst.Height + 2);

            if (sel)
            {
                using var f = new Font(Font.FontFamily, 8f);
                g.DrawString(s.Name, f, Brushes.White, dst.X, Math.Max(frame.Y, dst.Y - 16));
            }
        }

        static PointF Center(RectangleF r) => new(r.X + r.Width / 2f, r.Y + r.Height / 2f);
    }

    private void OnPreviewClick(object? sender, MouseEventArgs e)
    {
        for (int i = _profile.Segments.Count - 1; i >= 0; i--)
        {
            var s = _profile.Segments[i];
            if (ToPreview(s.Dest).Contains(e.Location) || ToPreview(s.Source).Contains(e.Location))
            {
                _list.SelectedIndex = i;
                return;
            }
        }
    }

    // ==================================================================================
    // Status, hotkey, lifetime
    // ==================================================================================

    private void UpdateStatus()
    {
        _start.Enabled = !_mgr.Running;
        _stop.Enabled = _mgr.Running;

        string text;
        Color color;
        if (!_mgr.Running)
        {
            text = "Stopped";
            color = SystemColors.GrayText;
        }
        else if (_mgr.GameWindow == IntPtr.Zero)
        {
            text = $"Waiting for the game ({string.Join(", ", _profile.ProcessNames)})…";
            color = Color.DarkOrange;
        }
        else
        {
            text = $"Cloning from {_mgr.GameDescription}";
            if (_mgr.UserHidden) text += " · hidden (Ctrl+Shift+W)";
            if (_mgr.EditMode) text += " · EDIT MODE";
            color = Color.ForestGreen;
        }
        _status.Text = text;
        _status.ForeColor = color;

        _help.Text =
            (_hotkeyOk ? "Ctrl+Shift+W hides/shows the overlays. " : "Ctrl+Shift+W is taken by another app, so the hide hotkey is off. ") +
            "Edit layout: drag a clone to move it, mouse wheel to resize (Shift = fine), arrow keys to nudge (Shift = 10 px). " +
            "Source and clone values can also be typed below; changes apply live. Run the game in borderless windowed mode.";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyOk = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.W);
        if (_status != null) UpdateStatus();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            _mgr.ToggleHidden();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _mgr.Dispose();
        _previewImage?.Dispose();
        base.OnFormClosed(e);
    }

    private static string? Prompt(IWin32Window owner, string title, string text, string value)
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };
        var box = new TextBox { Text = value, Width = 300 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        stack.Controls.AddRange(new Control[] { new Label { Text = text, AutoSize = true }, box, buttons });
        f.Controls.Add(stack);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
