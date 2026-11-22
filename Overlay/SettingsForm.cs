// Settings window: every change applies live to the minimap and is saved to minimap.json.
public sealed class SettingsForm : Form
{
    readonly Config cfg;
    readonly Overlay overlay;
    bool syncing;
    readonly bool quiet;
    public bool Quiet => quiet;
    protected override bool ShowWithoutActivation => quiet;

    readonly TrackBar tbSize = new() { Minimum = Config.SizeMin, Maximum = Config.SizeMax, TickFrequency = 50, SmallChange = 10, LargeChange = 50 };
    readonly TrackBar tbZoom = new() { Minimum = 0, Maximum = 100, TickFrequency = 10 };
    readonly TrackBar tbOpacity = new() { Minimum = 20, Maximum = 100, TickFrequency = 10 };
    readonly TrackBar tbBigSize = new() { Minimum = 30, Maximum = 95, TickFrequency = 5 };
    readonly TrackBar tbBigZoom = new() { Minimum = 0, Maximum = 100, TickFrequency = 10 };
    readonly TrackBar tbIcons = new() { Minimum = Config.IconSizeMin, Maximum = Config.IconSizeMax, TickFrequency = 4, SmallChange = 1, LargeChange = 4 };
    readonly Label lbSize = new(), lbZoom = new(), lbOpacity = new(), lbBigSize = new(), lbBigZoom = new(), lbIcons = new();
    readonly RadioButton rbRound = new() { Text = L.T("Round", "Redondo"), AutoSize = true };
    readonly RadioButton rbSquare = new() { Text = L.T("Square", "Cuadrado"), AutoSize = true };
    readonly CheckBox cbRotate = Check(L.T("Rotate the map with the camera", "Girar el mapa con la cámara"));
    readonly CheckBox cbFocus = Check(L.T("Hide when the game isn't focused (Alt+Tab)", "Ocultar cuando no estoy en el juego (Alt+Tab)"));
    readonly CheckBox cbSmooth = Check(L.T("Smooth movement", "Movimiento suave"));
    readonly CheckBox cbCompass = Check(L.T("Compass (N, E, S, W)", "Brújula (N, E, S, O)"));
    readonly CheckBox cbTrail = Check(L.T("Trail of where you've been", "Rastro de por dónde pasaste"));
    readonly NumericUpDown nuTrail = new() { Minimum = 10, Maximum = 3600, Increment = 30, Width = 70 };
    readonly CheckBox cbFriends = Check(L.T("Friends", "Amigos"));
    readonly CheckBox cbMarkers = Check(L.T("Markers from the game map", "Marcadores del mapa del juego"));
    readonly Dictionary<MarkKind, CheckBox> cbKinds = new();
    readonly CheckBox cbPois = Check(L.T("Discovered places", "Lugares descubiertos"));
    readonly CheckBox cbPoiNames = Check(L.T("With names", "Con nombre"));
    readonly CheckBox cbCreatures = Check(L.T("Nearby creatures", "Bichos cercanos"));
    readonly CheckBox cbCreatureNames = Check(L.T("With names", "Con nombre"));
    readonly CheckBox cbOnlyHostile = Check(L.T("Hostile only", "Solo hostiles"));
    readonly CheckBox cbBigHold = Check(L.T("Hold the key (otherwise press to open and close)", "Mantener apretada (si no, apretar para abrir y cerrar)"));
    readonly NumericUpDown nuRight = new() { Minimum = 0, Maximum = 2000, Width = 70 };
    readonly NumericUpDown nuTop = new() { Minimum = 0, Maximum = 2000, Width = 70 };
    readonly ComboBox cbLang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat };
    static readonly string[] LangCodes = { "auto", "en", "es" };
    readonly ComboBox cbKeyToggle = KeyCombo(), cbKeyZoom = KeyCombo(), cbKeyRotate = KeyCombo(), cbKeySettings = KeyCombo(), cbKeyBig = KeyCombo();

    static readonly (MarkKind kind, string text)[] KindTexts =
    {
        (MarkKind.Pin, L.T("Your markers (with names)", "Tus marcadores (con nombre)")),
        (MarkKind.Bed, L.T("Beds and respawn points", "Camas y puntos de reaparición")),
        (MarkKind.Ping, L.T("Pings and marked resources", "Pings y recursos marcados")),
        (MarkKind.Quest, L.T("Quests and objectives", "Misiones y objetivos")),
        (MarkKind.Backpack, L.T("Dropped backpack or items", "Mochila u objetos tirados")),
        (MarkKind.Pet, L.T("Pet", "Mascota")),
        (MarkKind.Collectible, L.T("Collectibles", "Coleccionables")),
        (MarkKind.Base, L.T("Base alerts", "Alertas de la base")),
        (MarkKind.Other, L.T("Other (anything new the game adds)", "Otros (lo que el juego agregue)")),
    };

    static readonly Color BgCol = Color.FromArgb(32, 34, 30), FgCol = Color.FromArgb(235, 230, 215), Accent = Color.FromArgb(222, 196, 120), Muted = Color.FromArgb(170, 165, 150);

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };

    public SettingsForm(Config c, Overlay o)
    {
        cfg = c; overlay = o;
        Text = "Grounded 2 Minimap - " + L.T("Settings", "Configuración");
        Icon = Program.AppIcon(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        // --settings-pos=X,Y opens the window at that screen position without taking focus
        // (used by tools\screenshots.ps1 so it doesn't get in the way).
        var pos = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--settings-pos=", StringComparison.OrdinalIgnoreCase));
        if (pos != null && pos["--settings-pos=".Length..].Split(',') is [var px, var py] && int.TryParse(px, out int x) && int.TryParse(py, out int y))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
            TopMost = false;
            quiet = true;
        }
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = BgCol; ForeColor = FgCol;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(14);

        cbLang.Items.AddRange(new object[] { L.T("Automatic", "Automático"), "English", "Español" });
        foreach (var (k, txt) in KindTexts) cbKinds[k] = new CheckBox { Text = txt, AutoSize = true, Margin = new Padding(20, 3, 0, 0) };

        TableLayoutPanel NewTable()
        {
            var tp = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(0), Margin = new Padding(0, 0, 24, 0) };
            tp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return tp;
        }
        var left = NewTable();
        var right = NewTable();
        var t = left;
        void Header(string s)
        {
            var l = new Label { Text = s, AutoSize = true, ForeColor = Accent, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, t.RowCount == 0 ? 0 : 12, 0, 4) };
            t.Controls.Add(l, 0, t.RowCount); t.SetColumnSpan(l, 2); t.RowCount++;
        }
        void Row(string label, Control ctl)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) }, 0, t.RowCount);
            t.Controls.Add(ctl, 1, t.RowCount); t.RowCount++;
        }
        void Full(Control ctl) { t.Controls.Add(ctl, 0, t.RowCount); t.SetColumnSpan(ctl, 2); t.RowCount++; }
        Control WithLabel(TrackBar tb, Label lb)
        {
            tb.Width = 220; tb.AutoSize = false; tb.Height = 32; tb.BackColor = BgCol;
            lb.AutoSize = true; lb.Margin = new Padding(6, 6, 0, 0); lb.MinimumSize = new Size(64, 0);
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            p.Controls.Add(tb); p.Controls.Add(lb); return p;
        }
        Control Flow(int indent, params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(indent, 2, 0, 0) };
            p.Controls.AddRange(cs); return p;
        }
        Label Unit(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(4, 6, 0, 0), ForeColor = Muted };
        Label Hint(string s) => new() { Text = s, AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Muted, Margin = new Padding(20, 2, 0, 0) };

        // ---- left column: the minimap itself
        Header(L.T("Minimap", "Minimapa"));
        Row(L.T("Language", "Idioma"), cbLang);
        Row(L.T("Size", "Tamaño"), WithLabel(tbSize, lbSize));
        Row("Zoom", WithLabel(tbZoom, lbZoom));
        Row(L.T("Opacity", "Opacidad"), WithLabel(tbOpacity, lbOpacity));
        Row(L.T("Icons", "Íconos"), WithLabel(tbIcons, lbIcons));
        Row(L.T("Shape", "Forma"), Flow(0, rbRound, rbSquare));
        Full(cbRotate);
        Full(cbFocus);
        Full(cbSmooth);

        Header(L.T("Position (from the top-right corner)", "Posición (desde la esquina superior derecha)"));
        Row(L.T("Right margin", "Margen derecho"), Flow(0, nuRight, Unit("px")));
        Row(L.T("Top margin", "Margen arriba"), Flow(0, nuTop, Unit("px")));

        Header(L.T("Big map", "Mapa grande"));
        Row(L.T("Size", "Tamaño"), WithLabel(tbBigSize, lbBigSize));
        Row("Zoom", WithLabel(tbBigZoom, lbBigZoom));
        Full(cbBigHold);

        Header(L.T("Keys", "Teclas"));
        Row(L.T("Show / hide", "Mostrar / ocultar"), cbKeyToggle);
        Row(L.T("Change zoom", "Cambiar zoom"), cbKeyZoom);
        Row(L.T("Rotate with camera", "Girar con la cámara"), cbKeyRotate);
        Row(L.T("Big map", "Mapa grande"), cbKeyBig);
        Row(L.T("Open this window", "Abrir esta ventana"), cbKeySettings);

        // ---- right column: what to draw
        t = right;
        Header(L.T("What to show", "Qué mostrar"));
        Full(cbCompass);
        Full(Flow(0, cbTrail, Unit(L.T("for the last", "de los últimos")), nuTrail, Unit(L.T("seconds", "segundos"))));
        Full(cbFriends);
        Full(cbMarkers);
        Full(Hint(L.T("The same things the game's map shows. Markers hidden in the game are hidden here too.", "Lo mismo que muestra el mapa del juego. Si el juego oculta un marcador, acá tampoco aparece.")));
        foreach (var (k, _) in KindTexts) Full(cbKinds[k]);
        Full(Flow(0, cbPois, cbPoiNames));
        Full(Hint(L.T("Only the ones you have already discovered.", "Solo los que ya descubriste.")));
        Full(Flow(0, cbCreatures, cbCreatureNames, cbOnlyHostile));
        Full(Hint(L.T("Red triangle = hostile, orange diamond = neutral, green circle = passive. The lists can be edited in minimap.json.", "Triángulo rojo = hostil, rombo naranja = neutral, círculo verde = pacífico. Las listas se pueden editar en minimap.json.")));

        var cols = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        cols.Controls.Add(left); cols.Controls.Add(right);

        var btnReset = new Button { Text = L.T("Reset", "Restablecer"), AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0) };
        var btnClose = new Button { Text = L.T("Done", "Listo"), AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.Black };
        btnReset.Click += (_, _) => ResetDefaults();
        btnClose.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(btnClose); buttons.Controls.Add(btnReset);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(cols, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
        AcceptButton = btnClose;

        SyncFromConfig();

        tbSize.ValueChanged += (_, _) => Apply(() => cfg.Size = tbSize.Value, layout: true);
        tbZoom.ValueChanged += (_, _) => Apply(() => cfg.Zoom = SliderToZoom(tbZoom.Value));
        tbOpacity.ValueChanged += (_, _) => Apply(() => cfg.Opacity = tbOpacity.Value / 100.0, layout: true);
        cbLang.SelectedIndexChanged += (_, _) =>
        {
            if (syncing) return;
            cfg.Language = LangCodes[Math.Max(0, cbLang.SelectedIndex)];
            cfg.Save();
            L.Init(cfg.Language);
            overlay.ReopenSettings();
        };
        tbIcons.ValueChanged += (_, _) => Apply(() => cfg.IconSize = tbIcons.Value);
        tbBigSize.ValueChanged += (_, _) => Apply(() => cfg.BigMapPercent = tbBigSize.Value, layout: true);
        tbBigZoom.ValueChanged += (_, _) => Apply(() => cfg.BigMapZoom = SliderToZoom(tbBigZoom.Value));
        rbRound.CheckedChanged += (_, _) => Apply(() => cfg.Round = rbRound.Checked, layout: true);
        cbRotate.CheckedChanged += (_, _) => Apply(() => cfg.RotateWithCamera = cbRotate.Checked);
        cbFocus.CheckedChanged += (_, _) => Apply(() => cfg.OnlyWhenGameFocused = cbFocus.Checked);
        cbSmooth.CheckedChanged += (_, _) => Apply(() => cfg.Smooth = cbSmooth.Checked, layout: true);
        cbCompass.CheckedChanged += (_, _) => Apply(() => cfg.ShowCompass = cbCompass.Checked);
        cbTrail.CheckedChanged += (_, _) => Apply(() => cfg.ShowTrail = cbTrail.Checked);
        nuTrail.ValueChanged += (_, _) => Apply(() => cfg.TrailSeconds = (int)nuTrail.Value);
        cbFriends.CheckedChanged += (_, _) => Apply(() => cfg.ShowFriends = cbFriends.Checked);
        cbMarkers.CheckedChanged += (_, _) => Apply(() => cfg.ShowMarkers = cbMarkers.Checked);
        foreach (var (k, cb) in cbKinds) cb.CheckedChanged += (_, _) => Apply(() => cfg.SetKind(k, cb.Checked));
        cbPois.CheckedChanged += (_, _) => Apply(() => cfg.ShowPois = cbPois.Checked);
        cbPoiNames.CheckedChanged += (_, _) => Apply(() => cfg.PoiNames = cbPoiNames.Checked);
        cbCreatures.CheckedChanged += (_, _) => Apply(() => cfg.ShowCreatures = cbCreatures.Checked);
        cbCreatureNames.CheckedChanged += (_, _) => Apply(() => cfg.CreatureNames = cbCreatureNames.Checked);
        cbOnlyHostile.CheckedChanged += (_, _) => Apply(() => cfg.OnlyHostileCreatures = cbOnlyHostile.Checked);
        cbBigHold.CheckedChanged += (_, _) => Apply(() => cfg.BigMapHold = cbBigHold.Checked);
        nuRight.ValueChanged += (_, _) => Apply(() => cfg.MarginRight = (int)nuRight.Value, layout: true);
        nuTop.ValueChanged += (_, _) => Apply(() => cfg.MarginTop = (int)nuTop.Value, layout: true);
        cbKeyToggle.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeyToggle = (string)cbKeyToggle.SelectedItem);
        cbKeyZoom.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeyZoom = (string)cbKeyZoom.SelectedItem);
        cbKeyRotate.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeyRotate = (string)cbKeyRotate.SelectedItem);
        cbKeySettings.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeySettings = (string)cbKeySettings.SelectedItem);
        cbKeyBig.SelectedIndexChanged += (_, _) => Apply(() => cfg.KeyBigMap = (string)cbKeyBig.SelectedItem);
    }

    static ComboBox KeyCombo()
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat };
        cb.Items.Add("None");
        for (int i = 1; i <= 12; i++) cb.Items.Add("F" + i);
        foreach (var k in new[] { "Insert", "Home", "End", "PageUp", "PageDown", "Pause", "Scroll", "Capital", "Oemtilde", "Tab", "XButton1", "XButton2", "MButton" }) cb.Items.Add(k);
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
        lbBigSize.Text = $"{cfg.BigMapPercent}% " + L.T("height", "alto");
        lbIcons.Text = $"{cfg.IconSize} px";
        lbBigZoom.Text = $"{24000 / cfg.BigMapZoom * 100:0}%";
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
        tbBigSize.Value = cfg.BigMapPercent;
        tbIcons.Value = cfg.IconSize;
        cbLang.SelectedIndex = Math.Max(0, Array.IndexOf(LangCodes, cfg.Language));
        tbBigZoom.Value = Math.Clamp(ZoomToSlider(cfg.BigMapZoom), 0, 100);
        rbRound.Checked = cfg.Round; rbSquare.Checked = !cfg.Round;
        cbRotate.Checked = cfg.RotateWithCamera;
        cbFocus.Checked = cfg.OnlyWhenGameFocused;
        cbSmooth.Checked = cfg.Smooth;
        cbCompass.Checked = cfg.ShowCompass;
        cbTrail.Checked = cfg.ShowTrail;
        nuTrail.Value = Math.Clamp(cfg.TrailSeconds, 10, 3600);
        cbFriends.Checked = cfg.ShowFriends;
        cbMarkers.Checked = cfg.ShowMarkers;
        foreach (var (k, cb) in cbKinds) cb.Checked = !cfg.HiddenMarkKinds.Contains(k.ToString());
        cbPois.Checked = cfg.ShowPois;
        cbPoiNames.Checked = cfg.PoiNames;
        cbCreatures.Checked = cfg.ShowCreatures;
        cbCreatureNames.Checked = cfg.CreatureNames;
        cbOnlyHostile.Checked = cfg.OnlyHostileCreatures;
        cbBigHold.Checked = cfg.BigMapHold;
        nuRight.Value = Math.Clamp(cfg.MarginRight, 0, 2000);
        nuTop.Value = Math.Clamp(cfg.MarginTop, 0, 2000);
        SelectKey(cbKeyToggle, cfg.KeyToggle);
        SelectKey(cbKeyZoom, cfg.KeyZoom);
        SelectKey(cbKeyRotate, cfg.KeyRotate);
        SelectKey(cbKeySettings, cfg.KeySettings);
        SelectKey(cbKeyBig, cfg.KeyBigMap);
        UpdateLabels();
        syncing = false;
    }

    void ResetDefaults()
    {
        // Keep the creature word lists and fallback bounds the user may have edited by hand.
        var d = new Config
        {
            PassiveCreatures = cfg.PassiveCreatures, NeutralCreatures = cfg.NeutralCreatures, Language = cfg.Language,
            MinX = cfg.MinX, MaxX = cfg.MaxX, MinY = cfg.MinY, MaxY = cfg.MaxY, ZoomLevels = cfg.ZoomLevels,
        };
        foreach (var p in typeof(Config).GetProperties().Where(p => p.CanWrite)) p.SetValue(cfg, p.GetValue(d));
        cfg.Save();
        SyncFromConfig();
        overlay.ApplyLayout();
    }
}
