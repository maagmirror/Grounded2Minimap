// Settings window: every change applies live to the minimap and is saved to minimap.json.
public sealed class SettingsForm : Form
{
    readonly Config cfg;
    readonly Overlay overlay;
    bool syncing;

    readonly TrackBar tbSize = new() { Minimum = Config.SizeMin, Maximum = Config.SizeMax, TickFrequency = 50, SmallChange = 10, LargeChange = 50 };
    readonly TrackBar tbZoom = new() { Minimum = 0, Maximum = 100, TickFrequency = 10 };
    readonly TrackBar tbOpacity = new() { Minimum = 20, Maximum = 100, TickFrequency = 10 };
    readonly Label lbSize = new(), lbZoom = new(), lbOpacity = new();
    readonly RadioButton rbRound = new() { Text = "Redondo", AutoSize = true };
    readonly RadioButton rbSquare = new() { Text = "Cuadrado", AutoSize = true };
    readonly CheckBox cbRotate = new() { Text = "Girar el mapa con la camara", AutoSize = true };
    readonly CheckBox cbFocus = new() { Text = "Ocultar cuando no estoy en el juego (Alt+Tab)", AutoSize = true };
    readonly NumericUpDown nuRight = new() { Minimum = 0, Maximum = 2000, Width = 70 };
    readonly NumericUpDown nuTop = new() { Minimum = 0, Maximum = 2000, Width = 70 };
    readonly ComboBox cbKeyToggle = KeyCombo(), cbKeyZoom = KeyCombo(), cbKeyRotate = KeyCombo(), cbKeySettings = KeyCombo();

    static readonly Color BgCol = Color.FromArgb(32, 34, 30), FgCol = Color.FromArgb(235, 230, 215), Accent = Color.FromArgb(222, 196, 120);

    public SettingsForm(Config c, Overlay o)
    {
        cfg = c; overlay = o;
        Text = "Grounded 2 Minimap - Configuracion";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = BgCol; ForeColor = FgCol;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(14);

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        void Header(string s)
        {
            var l = new Label { Text = s, AutoSize = true, ForeColor = Accent, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, row == 0 ? 0 : 12, 0, 4) };
            t.Controls.Add(l, 0, row); t.SetColumnSpan(l, 2); row++;
        }
        void Row(string label, Control ctl)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) }, 0, row);
            t.Controls.Add(ctl, 1, row); row++;
        }
        Control WithLabel(TrackBar tb, Label lb)
        {
            tb.Width = 260; tb.AutoSize = false; tb.Height = 32; tb.BackColor = BgCol;
            lb.AutoSize = true; lb.Margin = new Padding(6, 6, 0, 0); lb.MinimumSize = new Size(80, 0);
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            p.Controls.Add(tb); p.Controls.Add(lb); return p;
        }
        Control Flow(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            p.Controls.AddRange(cs); return p;
        }

        Header("Minimapa");
        Row("Tamaño", WithLabel(tbSize, lbSize));
        Row("Zoom", WithLabel(tbZoom, lbZoom));
        Row("Opacidad", WithLabel(tbOpacity, lbOpacity));
        Row("Forma", Flow(rbRound, rbSquare));
        Row("", cbRotate);
        Row("", cbFocus);

        Header("Posicion (desde la esquina superior derecha)");
        Row("Margen derecho", Flow(nuRight, new Label { Text = "px", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));
        Row("Margen arriba", Flow(nuTop, new Label { Text = "px", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));

        Header("Teclas");
        Row("Mostrar / ocultar", cbKeyToggle);
        Row("Cambiar zoom", cbKeyZoom);
        Row("Girar con camara", cbKeyRotate);
        Row("Abrir esta ventana", cbKeySettings);

        var btnReset = new Button { Text = "Restablecer", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0) };
        var btnClose = new Button { Text = "Listo", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.Black };
        btnReset.Click += (_, _) => ResetDefaults();
        btnClose.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(btnClose); buttons.Controls.Add(btnReset);
        t.Controls.Add(buttons, 0, row); t.SetColumnSpan(buttons, 2);

        Controls.Add(t);
        AcceptButton = btnClose;

        SyncFromConfig();

        tbSize.ValueChanged += (_, _) => Apply(() => cfg.Size = tbSize.Value, layout: true);
        tbZoom.ValueChanged += (_, _) => Apply(() => cfg.Zoom = SliderToZoom(tbZoom.Value));
        tbOpacity.ValueChanged += (_, _) => Apply(() => cfg.Opacity = tbOpacity.Value / 100.0, layout: true);
        rbRound.CheckedChanged += (_, _) => Apply(() => cfg.Round = rbRound.Checked, layout: true);
        cbRotate.CheckedChanged += (_, _) => Apply(() => cfg.RotateWithCamera = cbRotate.Checked);
        cbFocus.CheckedChanged += (_, _) => Apply(() => cfg.OnlyWhenGameFocused = cbFocus.Checked);
        nuRight.ValueChanged += (_, _) => Apply(() => cfg.MarginRight = (int)nuRight.Value, layout: true);
        nuTop.ValueChanged += (_, _) => Apply(() => cfg.MarginTop = (int)nuTop.Value, layout: true);
        cbKeyToggle.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeyToggle = (string)cbKeyToggle.SelectedItem);
        cbKeyZoom.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeyZoom = (string)cbKeyZoom.SelectedItem);
        cbKeyRotate.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeyRotate = (string)cbKeyRotate.SelectedItem);
        cbKeySettings.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeySettings = (string)cbKeySettings.SelectedItem);
    }

    static ComboBox KeyCombo()
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat };
        cb.Items.Add("None");
        for (int i = 1; i <= 12; i++) cb.Items.Add("F" + i);
        foreach (var k in new[] { "Insert", "Home", "End", "PageUp", "PageDown", "Pause", "Scroll" }) cb.Items.Add(k);
        for (int i = 0; i <= 9; i++) cb.Items.Add("NumPad" + i);
        return cb;
    }

    // Slider is logarithmic so it feels even between close and far.
    static double SliderToZoom(int v) => Config.ZoomMin * Math.Pow(Config.ZoomMax / Config.ZoomMin, v / 100.0);
    static int ZoomToSlider(double z) => (int)Math.Round(Math.Log(z / Config.ZoomMin) / Math.Log(Config.ZoomMax / Config.ZoomMin) * 100);

    void Apply(Action change, bool layout = false)
    {
        if (syncing) return;
        change();
        cfg.Clamp();
        cfg.Save();
        UpdateLabels();
        if (layout) overlay.ApplyLayout(); else overlay.Redraw();
    }

    void UpdateLabels()
    {
        lbSize.Text = $"{cfg.Size} px";
        // Zoom shown relative to default (24000 units = 100%).
        lbZoom.Text = $"{24000 / cfg.Zoom * 100:0}%";
        lbOpacity.Text = $"{cfg.Opacity * 100:0}%";
    }

    static void SelectKey(ComboBox cb, string key)
    {
        int i = cb.Items.IndexOf(key);
        if (i < 0) { cb.Items.Add(key); i = cb.Items.Count - 1; }
        cb.SelectedIndex = i;
    }

    public void SyncFromConfig()
    {
        if (IsDisposed) return;
        syncing = true;
        tbSize.Value = cfg.Size;
        tbZoom.Value = Math.Clamp(ZoomToSlider(cfg.Zoom), 0, 100);
        tbOpacity.Value = (int)Math.Round(cfg.Opacity * 100);
        rbRound.Checked = cfg.Round; rbSquare.Checked = !cfg.Round;
        cbRotate.Checked = cfg.RotateWithCamera;
        cbFocus.Checked = cfg.OnlyWhenGameFocused;
        nuRight.Value = Math.Clamp(cfg.MarginRight, 0, 2000);
        nuTop.Value = Math.Clamp(cfg.MarginTop, 0, 2000);
        SelectKey(cbKeyToggle, cfg.KeyToggle);
        SelectKey(cbKeyZoom, cfg.KeyZoom);
        SelectKey(cbKeyRotate, cfg.KeyRotate);
        SelectKey(cbKeySettings, cfg.KeySettings);
        UpdateLabels();
        syncing = false;
    }

    void ResetDefaults()
    {
        var d = new Config();
        cfg.Size = d.Size; cfg.Zoom = d.Zoom; cfg.Opacity = d.Opacity; cfg.Round = d.Round;
        cfg.RotateWithCamera = d.RotateWithCamera; cfg.OnlyWhenGameFocused = d.OnlyWhenGameFocused;
        cfg.MarginRight = d.MarginRight; cfg.MarginTop = d.MarginTop;
        cfg.KeyToggle = d.KeyToggle; cfg.KeyZoom = d.KeyZoom; cfg.KeyRotate = d.KeyRotate; cfg.KeySettings = d.KeySettings;
        cfg.Save();
        SyncFromConfig();
        overlay.ApplyLayout();
    }
}
