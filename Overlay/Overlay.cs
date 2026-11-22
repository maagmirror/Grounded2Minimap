using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public sealed class Overlay : Form
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    readonly Config cfg;
    readonly GameReader reader = new();
    readonly MapStore maps = new(Program.Log);
    readonly IconStore icons = new(Program.Log);
    MapStore.MapImage map;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    readonly NotifyIcon tray;
    bool userHidden;
    Snapshot last;
    bool dirty = true;
    SettingsForm settings;
    readonly Dictionary<int, bool> keyDown = new();
    int focusCheck;
    string lastStatus;

    // Smoothed view state (what is drawn), eased toward the latest snapshot.
    double vx, vy, vyaw;
    bool haveView;
    long lastTicks = Environment.TickCount64;

    // Trail of recent positions; NaN entries break the line (teleports, respawns).
    readonly List<(double X, double Y, long T)> trail = new();
    string trailLevel;

    bool big;           // big map currently shown
    bool bigToggled;    // toggle mode state

    static readonly Color Bg = Color.FromArgb(255, 24, 28, 22);
    static readonly Color Ring = Color.FromArgb(255, 222, 196, 120);
    static readonly Color TrailCol = Color.FromArgb(255, 255, 236, 170);
    readonly Font font = new("Segoe UI", 8f, FontStyle.Bold);
    readonly Font fontSmall = new("Segoe UI", 7.5f, FontStyle.Regular);
    readonly Font fontCompass = new("Segoe UI", 8.5f, FontStyle.Bold);

    public Overlay(Config c)
    {
        cfg = c;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Bg;
        Text = "Grounded 2 Minimap";
        reader.Log = Program.Log;
        ApplyLayout();

        map = maps.Get(null, null, null);

        tray = new NotifyIcon { Icon = Program.AppIcon(SystemInformation.SmallIconSize), Visible = true, Text = "Grounded 2 Minimap" };
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Settings...", "Configuración..."), null, (_, _) => OpenSettings());
        menu.Items.Add(L.T("Show / hide", "Mostrar / ocultar"), null, (_, _) => userHidden = !userHidden);
        menu.Items.Add(L.T("Next zoom", "Zoom siguiente"), null, (_, _) => CycleZoom());
        menu.Items.Add(L.T("Rotate with camera", "Girar con la cámara"), null, (_, _) => ToggleRotate());
        menu.Items.Add("-");
        menu.Items.Add(L.T("Exit", "Salir"), null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => OpenSettings();
        if (!demo) tray.ShowBalloonTip(3000, "Grounded 2 Minimap", L.T($"Running. {cfg.KeyToggle}: show/hide, {cfg.KeyZoom}: zoom, {cfg.KeyRotate}: rotate, {cfg.KeyBigMap}: big map, {cfg.KeySettings}: settings.", $"Activo. {cfg.KeyToggle}: mostrar/ocultar, {cfg.KeyZoom}: zoom, {cfg.KeyRotate}: girar, {cfg.KeyBigMap}: mapa grande, {cfg.KeySettings}: configuración."), ToolTipIcon.Info);

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--settings-pos=", StringComparison.OrdinalIgnoreCase)))
            Shown += (_, _) => OpenSettings();
        if (demo)
        {
            demoSnap = Demo.Snapshot();
            trail.AddRange(Demo.Trail(demoSnap, Environment.TickCount64));
            trailLevel = demoSnap.Level.Name;
        }
        else reader.Start();
        timer.Tick += (_, _) => { try { OnTick(); } catch (Exception ex) { LogOnce("tick", ex); } };
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered + click-through + no taskbar/alt-tab + never steals focus + topmost
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8;
            return cp;
        }
    }

    // Effective view settings (the big map overrides the minimap's).
    int ViewSize => big ? BigSize() : cfg.Size;
    bool ViewRound => !big && cfg.Round;
    double ViewZoom => big ? cfg.BigMapZoom : cfg.Zoom;
    bool ViewRotate => !big && cfg.RotateWithCamera;

    int BigSize()
    {
        var area = GameArea();
        return Math.Max(200, Math.Min(area.Width, area.Height) * cfg.BigMapPercent / 100);
    }

    readonly bool demo = Demo.On;
    Snapshot demoSnap;
    string PlayerIcon => demo ? Demo.PlayerIcon : reader.PlayerIcon;

    Rectangle GameArea()
    {
        if (demo) return Demo.Area();
        if (lastGameWnd != IntPtr.Zero && GetClientRect(lastGameWnd, out var rc) && rc.R > 0)
        {
            var tl = new Point(0, 0);
            ClientToScreen(lastGameWnd, ref tl);
            return new Rectangle(tl.X, tl.Y, rc.R, rc.B);
        }
        return Screen.PrimaryScreen.Bounds;
    }

    // Called by the settings window and at startup: size, shape, opacity.
    public void ApplyLayout()
    {
        cfg.Clamp();
        timer.Interval = cfg.Smooth ? 16 : 33;
        Opacity = big ? Math.Max(cfg.Opacity, 0.95) : cfg.Opacity;
        int size = ViewSize;
        ClientSize = new Size(size, size);
        var old = Region;
        if (ViewRound)
        {
            using var p = new GraphicsPath();
            p.AddEllipse(0, 0, size, size);
            Region = new Region(p);
        }
        else Region = null;
        old?.Dispose();
        PlaceNow();
        dirty = true;
        Invalidate();
    }

    public void Redraw() { dirty = true; Invalidate(); }

    // Rebuilds the settings window (after a language change).
    public void ReopenSettings()
    {
        BeginInvoke(() =>
        {
            if (settings != null && !settings.IsDisposed) settings.Close();
            OpenSettings();
        });
    }

    void OpenSettings()
    {
        if (settings == null || settings.IsDisposed)
        {
            settings = new SettingsForm(cfg, this);
            settings.FormClosed += (_, _) => { cfg.Save(); settings = null; };
            settings.Show();
        }
        if (!settings.Quiet) settings.Activate();
    }

    void CycleZoom()
    {
        var next = cfg.ZoomLevels.OrderBy(z => z).FirstOrDefault(z => z > cfg.Zoom + 1, cfg.ZoomLevels.Min());
        cfg.Zoom = next; cfg.Save(); dirty = true;
        settings?.SyncFromConfig();
    }
    void ToggleRotate() { cfg.RotateWithCamera = !cfg.RotateWithCamera; cfg.Save(); dirty = true; settings?.SyncFromConfig(); }

    IntPtr lastGameWnd;
    void PlaceNow()
    {
        var a = GameArea();
        int size = ViewSize;
        Location = big
            ? new Point(a.X + (a.Width - size) / 2, a.Y + (a.Height - size) / 2)
            : new Point(a.Right - size - cfg.MarginRight, a.Y + cfg.MarginTop);
    }

    bool Pressed(string keyName)
    {
        if (!Enum.TryParse<Keys>(keyName, true, out var k) || k == Keys.None) return false;
        int vk = (int)k;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        keyDown.TryGetValue(vk, out bool was);
        keyDown[vk] = down;
        return down && !was;
    }
    static bool Held(string keyName) => Enum.TryParse<Keys>(keyName, true, out var k) && k != Keys.None && (GetAsyncKeyState((int)k) & 0x8000) != 0;

    void OnTick()
    {
        // Visibility + placement follow the game window.
        bool gameFocused = false;
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out int fgPid);
        if (reader.GamePid != 0 && fgPid == reader.GamePid) gameFocused = true;

        bool wantBig = false;
        if (gameFocused || !cfg.OnlyWhenGameFocused)
        {
            if (Pressed(cfg.KeyToggle)) userHidden = !userHidden;
            if (Pressed(cfg.KeyZoom)) CycleZoom();
            if (Pressed(cfg.KeyRotate)) ToggleRotate();
            if (Pressed(cfg.KeySettings)) OpenSettings();
            if (cfg.BigMapHold) { wantBig = Held(cfg.KeyBigMap); bigToggled = false; }
            else { if (Pressed(cfg.KeyBigMap)) bigToggled = !bigToggled; wantBig = bigToggled; }
        }

        focusCheck++;
        if (gameFocused && focusCheck % 15 == 0 && fg != lastGameWnd)
        {
            lastGameWnd = fg;
            PlaceNow();
        }
        else if (!demo && !gameFocused && focusCheck % 60 == 0 && reader.GamePid != 0)
        {
            // Not in front (e.g. "hide on Alt+Tab" is off): still follow the game's window.
            try
            {
                var h = System.Diagnostics.Process.GetProcessById(reader.GamePid).MainWindowHandle;
                if (h != IntPtr.Zero && h != lastGameWnd) { lastGameWnd = h; PlaceNow(); }
            }
            catch { }
        }

        var s = demo ? demoSnap : reader.Snap;
        if (demo) { gameFocused = true; wantBig = Demo.Big; }
        if (wantBig != big && (s.Valid || !wantBig)) { big = wantBig; ApplyLayout(); }

        bool settingsOpen = settings != null && !settings.IsDisposed;
        // While the settings window is open, keep the minimap visible as a live preview.
        bool show = (!userHidden || settingsOpen || big) && (s.Valid || settingsOpen) && (gameFocused || settingsOpen || !cfg.OnlyWhenGameFocused);
        if (!s.Valid && settingsOpen && last != null) s = last;

        if (reader.Status != lastStatus)
        {
            lastStatus = reader.Status;
            Program.Log(lastStatus);
        }
        var tip = "Grounded 2 Minimap - " + reader.Status;
        tray.Text = tip.Length > 63 ? tip[..63] : tip;

        if (!demo) icons.Ensure(reader.IconPaths, reader.GameExePath);

        // Map image for the current level (may switch when the level changes or an update finishes).
        var m = maps.Get(s.Level, reader.GameExePath, map);
        if (m != map) { if (map?.Bitmap != null && map != m) map.Bitmap.Dispose(); map = m; dirty = true; }

        if (show != Visible)
        {
            if (show) { Show(); PlaceNow(); }
            else Hide();
        }
        if (!s.Valid) return;

        long now = Environment.TickCount64;
        double dt = Math.Clamp((now - lastTicks) / 1000.0, 0, 0.25);
        lastTicks = now;
        if (!demo) UpdateTrail(s, now);
        if (!show) { haveView = false; return; }

        // Ease the drawn position toward the latest reading.
        bool jump = !haveView || Math.Abs(s.X - vx) + Math.Abs(s.Y - vy) > 5000;
        if (!cfg.Smooth || jump) { vx = s.X; vy = s.Y; vyaw = s.Yaw; haveView = true; }
        else
        {
            double a = 1 - Math.Exp(-dt * 14);
            vx += (s.X - vx) * a; vy += (s.Y - vy) * a;
            double dyaw = ((s.Yaw - vyaw) % 360 + 540) % 360 - 180;
            vyaw += dyaw * a;
        }

        bool lists = last == null || last.Friends != s.Friends || last.Marks != s.Marks || last.Creatures != s.Creatures || last.Pois != s.Pois;
        double unitsPerPx = ViewZoom / Math.Max(1, ViewSize);
        if (dirty || lists || last == null || Math.Abs(lastDrawX - vx) > unitsPerPx * 0.35 || Math.Abs(lastDrawY - vy) > unitsPerPx * 0.35 || Math.Abs(lastDrawYaw - vyaw) > 0.3)
        {
            last = s; dirty = false;
            Invalidate();
        }
    }
    double lastDrawX, lastDrawY, lastDrawYaw;

    void UpdateTrail(Snapshot s, long now)
    {
        string lvl = s.Level?.Name ?? "";
        if (lvl != trailLevel) { trail.Clear(); trailLevel = lvl; }
        long maxAge = cfg.TrailSeconds * 1000L;
        int drop = 0;
        while (drop < trail.Count && now - trail[drop].T > maxAge) drop++;
        if (drop > 0) trail.RemoveRange(0, drop);
        if (!cfg.ShowTrail) return;
        if (trail.Count > 0)
        {
            var p = trail[^1];
            double d = double.IsNaN(p.X) ? double.MaxValue : Math.Sqrt((p.X - s.X) * (p.X - s.X) + (p.Y - s.Y) * (p.Y - s.Y));
            if (d < 120) return;
            if (d > 5000 && !double.IsNaN(p.X)) trail.Add((double.NaN, double.NaN, now));
        }
        trail.Add((s.X, s.Y, now));
    }

    readonly HashSet<string> loggedErrors = new();
    void LogOnce(string where, Exception ex)
    {
        if (loggedErrors.Add(where + ex.GetType().Name)) Program.Log($"Error in {where}: {ex}");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { Paint(e.Graphics); } catch (Exception ex) { LogOnce("paint", ex); }
    }

    void Paint(Graphics g)
    {
        g.Clear(Bg);
        var s = last;
        if (s == null || !s.Valid) return;
        lastDrawX = vx; lastDrawY = vy; lastDrawYaw = vyaw;

        var lv = s.Level;
        double minX = lv?.MinX ?? cfg.MinX, maxX = lv?.MaxX ?? cfg.MaxX, minY = lv?.MinY ?? cfg.MinY, maxY = lv?.MaxY ?? cfg.MaxY;
        float W = ClientSize.Width, H = ClientSize.Height, cx = W / 2, cy = H / 2;
        double scale = W / ViewZoom;                       // screen px per world unit
        float rot = ViewRotate ? (float)-(vyaw + 90) : 0f;
        double rad = rot * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        PointF ToScreen(double wx, double wy)
        {
            double dx = (wx - vx) * scale, dy = (wy - vy) * scale;
            return new PointF((float)(cx + dx * cos - dy * sin), (float)(cy + dx * sin + dy * cos));
        }
        float iconSize = cfg.IconSize * (big ? 1.3f : 1f);
        float ks = iconSize / 20f;                         // scale for the drawn (fallback) shapes
        float lim = W / 2 - Math.Max(9, iconSize / 2 + 2);
        bool Inside(PointF p) => ViewRound ? Dist(p, cx, cy) <= lim : Math.Abs(p.X - cx) <= lim && Math.Abs(p.Y - cy) <= lim;
        PointF Clamp(PointF p)
        {
            float dx = p.X - cx, dy = p.Y - cy;
            float k = ViewRound ? lim / MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy)) : lim / MathF.Max(1, MathF.Max(MathF.Abs(dx), MathF.Abs(dy)));
            return k < 1 ? new PointF(cx + dx * k, cy + dy * k) : p;
        }

        // --- map
        var bmp = map?.Bitmap;
        if (bmp != null)
        {
            double mapScaleX = bmp.Width / (maxX - minX), mapScaleY = bmp.Height / (maxY - minY);
            float px = (float)((vx - minX) * mapScaleX), py = (float)((vy - minY) * mapScaleY);
            g.InterpolationMode = big ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var st = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(rot);
            g.ScaleTransform((float)(scale / mapScaleX), (float)(scale / mapScaleY));
            float r = (float)(ViewZoom * 0.75 * mapScaleX);
            var src = RectangleF.Intersect(new RectangleF(px - r, py - r, 2 * r, 2 * r), new RectangleF(0, 0, bmp.Width, bmp.Height));
            if (src.Width > 0 && src.Height > 0)
                g.DrawImage(bmp, new RectangleF(src.X - px, src.Y - py, src.Width, src.Height), src, GraphicsUnit.Pixel);
            g.Restore(st);
        }
        else
        {
            using var b = new SolidBrush(Color.FromArgb(160, 235, 230, 215));
            var msg = L.T("No map available for this level", "Mapa no disponible para este nivel");
            var sz = g.MeasureString(msg, fontSmall);
            g.DrawString(msg, fontSmall, b, cx - sz.Width / 2, cy + 22);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        bool labels = big;

        // --- trail
        if (cfg.ShowTrail && trail.Count > 1)
        {
            long now = Environment.TickCount64, maxAge = cfg.TrailSeconds * 1000L;
            using var pen = new Pen(TrailCol, big ? 2.5f : 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 1; i < trail.Count; i++)
            {
                var a = trail[i - 1]; var b = trail[i];
                if (double.IsNaN(a.X) || double.IsNaN(b.X)) continue;
                double age = (now - b.T) / (double)maxAge;
                pen.Color = Color.FromArgb((int)(40 + 150 * Math.Clamp(1 - age, 0, 1)), TrailCol);
                g.DrawLine(pen, ToScreen(a.X, a.Y), ToScreen(b.X, b.Y));
            }
            var lastPt = trail[^1];
            if (!double.IsNaN(lastPt.X)) { pen.Color = Color.FromArgb(190, TrailCol); g.DrawLine(pen, ToScreen(lastPt.X, lastPt.Y), new PointF(cx, cy)); }
        }

        // --- discovered points of interest
        if (cfg.ShowPois)
            foreach (var p in s.Pois)
            {
                var q = ToScreen(p.X, p.Y);
                if (!Inside(q)) continue;
                var ib = icons.Get(p.Icon);
                if (ib != null) Icons.Image(g, ib, q, iconSize * 0.85f, Color.Empty, 1f);
                else Scaled(g, q, ks, o => Icons.Poi(g, o));
                if (labels || cfg.PoiNames) Label(g, p.Name, q, fontSmall, Color.FromArgb(240, 232, 210), dx: ib != null ? iconSize * 0.45f : 8 * ks);
            }

        // --- creatures
        if (cfg.ShowCreatures)
            foreach (var c in s.Creatures)
            {
                var temper = Temper(c.Name);
                if (cfg.OnlyHostileCreatures && temper != 2) continue;
                var q = ToScreen(c.X, c.Y);
                if (!Inside(q)) continue;
                Scaled(g, q, ks, o => Icons.Creature(g, o, temper));
                if (cfg.CreatureNames) Label(g, c.Name, q, fontSmall, Icons.CreatureColor(temper), dx: 6 * ks + 2);
            }

        // --- game markers (what the in-game map shows)
        foreach (var mk in s.Marks.OrderBy(k => k.Kind == MarkKind.Pin ? 1 : 0))
        {
            if (!cfg.ShowsKind(mk.Kind)) continue;
            var q = ToScreen(mk.X, mk.Y);
            bool inside = Inside(q);
            // Things you navigate to stay on the edge pointing the way; the rest only when in view.
            bool pinToEdge = mk.Kind is MarkKind.Pin or MarkKind.Bed or MarkKind.Ping or MarkKind.Pet or MarkKind.Backpack or MarkKind.Quest;
            if (!inside && !pinToEdge) continue;
            if (!inside) q = Clamp(q);
            var mb = icons.Get(mk.Icon);
            if (mb != null) Icons.Image(g, mb, q, iconSize, mk.Color, inside ? 1f : 0.8f);
            else Scaled(g, q, ks, o => Icons.Mark(g, o, mk, inside ? 1f : 0.8f));
            if (inside && !string.IsNullOrEmpty(mk.Name) && (labels || mk.Kind == MarkKind.Pin))
                Label(g, mk.Name, q, font, Color.White, dx: mb != null ? iconSize * 0.55f : 8 * ks);
        }

        // --- friends
        if (cfg.ShowFriends)
            foreach (var f in s.Friends)
            {
                var q = ToScreen(f.X, f.Y);
                bool inside = Inside(q);
                if (!inside) q = Clamp(q);
                var pb = icons.Get(PlayerIcon);
                if (pb != null) Icons.Image(g, pb, q, iconSize, Color.Empty, inside ? 1f : 0.85f);
                else
                {
                    Scaled(g, q, ks, o =>
                    {
                        g.FillEllipse(Brushes.Black, o.X - 6, o.Y - 6, 12, 12);
                        g.FillEllipse(Brushes.DeepSkyBlue, o.X - 4.5f, o.Y - 4.5f, 9, 9);
                    });
                }
                if (inside && !string.IsNullOrEmpty(f.Name)) Label(g, f.Name, q, font, Color.FromArgb(255, 150, 215, 255), dx: pb != null ? iconSize * 0.55f : 8 * ks);
            }

        // --- compass
        if (cfg.ShowCompass)
        {
            float rr = (ViewRound ? W / 2 : W / 2 * 0.98f) - 12;
            string[] names = { "N", "E", "S", L.T("W", "O") };
            for (int i = 0; i < 4; i++)
            {
                // North = -Y in world (up on the map image); E = +X
                double a = rad + i * Math.PI / 2;
                float nx = cx + (float)(rr * Math.Sin(a)), ny = cy - (float)(rr * Math.Cos(a));
                using var bg = new SolidBrush(Color.FromArgb(i == 0 ? 220 : 150, 0, 0, 0));
                float d = i == 0 ? 16 : 13;
                g.FillEllipse(bg, nx - d / 2, ny - d / 2, d, d);
                var sz = g.MeasureString(names[i], fontCompass);
                using var tb = new SolidBrush(i == 0 ? Color.FromArgb(255, 255, 110, 80) : Color.FromArgb(230, 235, 230, 215));
                g.DrawString(names[i], i == 0 ? fontCompass : fontSmall, tb, nx - sz.Width / 2 + (i == 0 ? 0.5f : 1.5f), ny - sz.Height / 2 + (i == 0 ? 0 : 1));
            }
        }

        // --- player arrow
        float ang = ViewRotate ? -90f : (float)vyaw;
        var st2 = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(ang);
        var arrow = new[] { new PointF(11, 0), new PointF(-7, -7), new PointF(-3, 0), new PointF(-7, 7) };
        g.FillPolygon(Brushes.OrangeRed, arrow);
        using (var pen = new Pen(Color.White, 1.8f)) g.DrawPolygon(pen, arrow);
        g.Restore(st2);

        // --- frame
        using (var ring = new Pen(Ring, big ? 3f : 4f))
        {
            if (ViewRound) g.DrawEllipse(ring, 2, 2, W - 4, H - 4);
            else g.DrawRectangle(ring, 2, 2, W - 4, H - 4);
        }
        if (big && lv != null)
        {
            var title = GameReader.Humanize(lv.Name, Array.Empty<string>());
            Label(g, title, new PointF(10, 12), font, Ring, left: true);
        }
    }

    // Draws a fixed-size shape centered on p, scaled by k (shapes are designed for the default 20 px).
    static void Scaled(Graphics g, PointF p, float k, Action<PointF> draw)
    {
        var st = g.Save();
        g.TranslateTransform(p.X, p.Y);
        g.ScaleTransform(k, k);
        draw(PointF.Empty);
        g.Restore(st);
    }

    static float Dist(PointF p, float x, float y) => MathF.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));

    // 0 = passive, 1 = neutral, 2 = hostile (unknown creatures count as hostile)
    int Temper(string name)
    {
        string n = (name ?? "").Replace(" ", "");
        if (cfg.PassiveCreatures.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))) return 0;
        if (cfg.NeutralCreatures.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase))) return 1;
        return 2;
    }

    static void Label(Graphics g, string text, PointF at, Font f, Color c, bool left = false, float dx = 8)
    {
        float x = left ? at.X : at.X + dx, y = at.Y - 8;
        using var shadow = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
        using var br = new SolidBrush(c);
        g.DrawString(text, f, shadow, x + 1, y + 1);
        g.DrawString(text, f, br, x, y);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        reader.Stop();
        timer.Stop();
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.Dispose();
        map?.Bitmap?.Dispose();
        base.OnFormClosed(e);
    }
}

// Small vector icons, readable on top of the map at any size. Every kind has its own shape,
// not only its own color.
static class Icons
{
    static readonly Pen Outline = new(Color.FromArgb(230, 10, 12, 10), 1.6f) { LineJoin = LineJoin.Round };

    public static Color KindColor(MarkKind k) => k switch
    {
        MarkKind.Pin => Color.FromArgb(255, 255, 196, 60),
        MarkKind.Bed => Color.FromArgb(255, 150, 200, 255),
        MarkKind.Ping => Color.FromArgb(255, 90, 240, 200),
        MarkKind.Quest => Color.FromArgb(255, 255, 225, 70),
        MarkKind.Backpack => Color.FromArgb(255, 230, 160, 80),
        MarkKind.Pet => Color.FromArgb(255, 255, 160, 215),
        MarkKind.Collectible => Color.FromArgb(255, 200, 170, 255),
        MarkKind.Base => Color.FromArgb(255, 255, 80, 70),
        _ => Color.FromArgb(255, 220, 220, 220),
    };

    public static void Mark(Graphics g, PointF p, MapMark mk, float alpha)
    {
        var col = mk.Color.IsEmpty ? KindColor(mk.Kind) : mk.Color;
        using var br = new SolidBrush(Color.FromArgb((int)(255 * alpha), col));
        float x = p.X, y = p.Y;
        switch (mk.Kind)
        {
            case MarkKind.Pin:
                {
                    // teardrop: tip at the exact spot
                    using var path = new GraphicsPath();
                    path.AddArc(x - 6, y - 17, 12, 12, 135, 270);
                    path.AddLine(x + 4.2f, y - 7, x, y);
                    path.CloseFigure();
                    g.FillPath(br, path); g.DrawPath(Outline, path);
                    g.FillEllipse(Brushes.White, x - 2.2f, y - 13.2f, 4.4f, 4.4f);
                    break;
                }
            case MarkKind.Bed:
                {
                    var body = new RectangleF(x - 7, y - 3, 14, 6);
                    g.FillRectangle(br, body); g.DrawRectangle(Outline, body.X, body.Y, body.Width, body.Height);
                    var pillow = new RectangleF(x - 7, y - 7, 5, 4);
                    g.FillRectangle(Brushes.White, pillow); g.DrawRectangle(Outline, pillow.X, pillow.Y, pillow.Width, pillow.Height);
                    break;
                }
            case MarkKind.Ping:
                {
                    var d = new[] { new PointF(x, y - 7), new PointF(x + 6, y), new PointF(x, y + 7), new PointF(x - 6, y) };
                    g.FillPolygon(br, d); g.DrawPolygon(Outline, d);
                    break;
                }
            case MarkKind.Quest:
                {
                    var star = Star(x, y, 7.5f, 3.3f, 5);
                    g.FillPolygon(br, star); g.DrawPolygon(Outline, star);
                    break;
                }
            case MarkKind.Backpack:
                {
                    var r = new RectangleF(x - 5.5f, y - 5, 11, 11);
                    using var path = Rounded(r, 3);
                    g.FillPath(br, path); g.DrawPath(Outline, path);
                    g.DrawArc(Outline, x - 3, y - 8.5f, 6, 6, 180, 180);
                    break;
                }
            case MarkKind.Pet:
                {
                    g.FillEllipse(br, x - 4.5f, y - 2, 9, 7); g.DrawEllipse(Outline, x - 4.5f, y - 2, 9, 7);
                    foreach (var (ox, oy) in new[] { (-5f, -6f), (-1.6f, -8.5f), (2f, -8.5f) })
                    { g.FillEllipse(br, x + ox + 0.5f, y + oy, 3.2f, 3.2f); g.DrawEllipse(Outline, x + ox + 0.5f, y + oy, 3.2f, 3.2f); }
                    break;
                }
            case MarkKind.Collectible:
                {
                    var star = Star(x, y, 6.5f, 2.2f, 4);
                    g.FillPolygon(br, star); g.DrawPolygon(Outline, star);
                    break;
                }
            case MarkKind.Base:
                {
                    var t = new[] { new PointF(x, y - 8), new PointF(x + 7.5f, y + 5.5f), new PointF(x - 7.5f, y + 5.5f) };
                    g.FillPolygon(br, t); g.DrawPolygon(Outline, t);
                    g.FillRectangle(Brushes.Black, x - 0.9f, y - 3.5f, 1.8f, 4.5f);
                    g.FillRectangle(Brushes.Black, x - 0.9f, y + 2, 1.8f, 1.8f);
                    break;
                }
            default:
                g.FillEllipse(br, x - 3.5f, y - 3.5f, 7, 7); g.DrawEllipse(Outline, x - 3.5f, y - 3.5f, 7, 7);
                break;
        }
    }

    // Game icon (white artwork), multiplied by the marker's color like the game does.
    public static void Image(Graphics g, Bitmap b, PointF p, float size, Color tint, float alpha)
    {
        var dest = new Rectangle((int)Math.Round(p.X - size / 2), (int)Math.Round(p.Y - size / 2), (int)size, (int)size);
        if (tint.IsEmpty && alpha >= 0.999f) { g.DrawImage(b, dest); return; }
        float r = tint.IsEmpty ? 1 : tint.R / 255f, gg = tint.IsEmpty ? 1 : tint.G / 255f, bb = tint.IsEmpty ? 1 : tint.B / 255f;
        var cm = new ColorMatrix(new[]
        {
            new float[] { r, 0, 0, 0, 0 }, new float[] { 0, gg, 0, 0, 0 }, new float[] { 0, 0, bb, 0, 0 },
            new float[] { 0, 0, 0, alpha, 0 }, new float[] { 0, 0, 0, 0, 1 },
        });
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(cm);
        g.DrawImage(b, dest, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
    }

    public static void Poi(Graphics g, PointF p)
    {
        using var ring = new Pen(Color.FromArgb(235, 240, 232, 205), 2f);
        using var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        g.FillEllipse(bg, p.X - 5.5f, p.Y - 5.5f, 11, 11);
        g.DrawEllipse(ring, p.X - 4.5f, p.Y - 4.5f, 9, 9);
        g.FillEllipse(Brushes.White, p.X - 1.5f, p.Y - 1.5f, 3, 3);
    }

    public static Color CreatureColor(int temper) => temper switch
    {
        0 => Color.FromArgb(255, 200, 235, 140),
        1 => Color.FromArgb(255, 255, 190, 70),
        _ => Color.FromArgb(255, 255, 75, 60),
    };

    // passive = circle, neutral = diamond, hostile = triangle
    public static void Creature(Graphics g, PointF p, int temper)
    {
        using var br = new SolidBrush(CreatureColor(temper));
        float x = p.X, y = p.Y;
        if (temper == 0) { g.FillEllipse(br, x - 3.5f, y - 3.5f, 7, 7); g.DrawEllipse(Outline, x - 3.5f, y - 3.5f, 7, 7); }
        else if (temper == 1)
        {
            var d = new[] { new PointF(x, y - 4.5f), new PointF(x + 4.5f, y), new PointF(x, y + 4.5f), new PointF(x - 4.5f, y) };
            g.FillPolygon(br, d); g.DrawPolygon(Outline, d);
        }
        else
        {
            var t = new[] { new PointF(x, y - 5.5f), new PointF(x + 5, y + 4), new PointF(x - 5, y + 4) };
            g.FillPolygon(br, t); g.DrawPolygon(Outline, t);
        }
    }

    static PointF[] Star(float x, float y, float ro, float ri, int points)
    {
        var pts = new PointF[points * 2];
        for (int i = 0; i < pts.Length; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / points;
            float r = i % 2 == 0 ? ro : ri;
            pts[i] = new PointF(x + (float)(r * Math.Cos(a)), y + (float)(r * Math.Sin(a)));
        }
        return pts;
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
        p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
        p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        p.CloseFigure();
        return p;
    }
}
