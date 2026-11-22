using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "GroundedMinimapOverlay", out bool first);
        if (!first) { MessageBox.Show("El minimapa ya esta abierto (icono en la bandeja).", "Grounded 2 Minimap"); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new Overlay(Config.Load()));
    }
}

public sealed class Config
{
    public int Size { get; set; } = 260;
    public int MarginRight { get; set; } = 24;
    public int MarginTop { get; set; } = 24;
    public double Opacity { get; set; } = 0.92;
    public bool Round { get; set; } = true;
    public bool RotateWithCamera { get; set; } = false;
    public bool OnlyWhenGameFocused { get; set; } = true;
    // Zoom = world units visible across the minimap (smaller = closer).
    public double Zoom { get; set; } = 24000;
    public double[] ZoomLevels { get; set; } = { 12000, 24000, 48000, 96000 };
    public string KeyToggle { get; set; } = "F6";
    public string KeyZoom { get; set; } = "F7";
    public string KeyRotate { get; set; } = "F8";
    public string KeySettings { get; set; } = "F9";

    public const double ZoomMin = 4000, ZoomMax = 150000;
    public const int SizeMin = 120, SizeMax = 700;

    public void Clamp()
    {
        Zoom = Math.Clamp(Zoom, ZoomMin, ZoomMax);
        Size = Math.Clamp(Size, SizeMin, SizeMax);
        Opacity = Math.Clamp(Opacity, 0.2, 1.0);
        if (ZoomLevels == null || ZoomLevels.Length == 0) ZoomLevels = new double[] { 12000, 24000, 48000, 96000 };
    }
    // World bounds of the map texture (from the game's LevelData for Augusta_Main).
    public double MinX { get; set; } = -261500;
    public double MaxX { get; set; } = 73500;
    public double MinY { get; set; } = -137500;
    public double MaxY { get; set; } = 197500;

    static string PathFile => Path.Combine(AppContext.BaseDirectory, "minimap.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Config Load()
    {
        Config c = null;
        try { if (File.Exists(PathFile)) c = JsonSerializer.Deserialize<Config>(File.ReadAllText(PathFile)); }
        catch { }
        c ??= new Config();
        c.Clamp(); c.Save(); return c;
    }
    public void Save() { try { File.WriteAllText(PathFile, JsonSerializer.Serialize(this, Opts)); } catch { } }
}

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
    readonly Bitmap map;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly NotifyIcon tray;
    bool userHidden;
    Snapshot last;
    bool dirty = true;
    SettingsForm settings;
    readonly Dictionary<int, bool> keyDown = new();
    int focusCheck;
    string lastStatus;

    static readonly Color Bg = Color.FromArgb(255, 24, 28, 22);
    static readonly Color Ring = Color.FromArgb(255, 222, 196, 120);

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
        ApplyLayout();

        map = LoadMap();

        tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = true, Text = "Grounded 2 Minimap" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Configuracion...", null, (_, _) => OpenSettings());
        menu.Items.Add("Mostrar/ocultar", null, (_, _) => userHidden = !userHidden);
        menu.Items.Add("Zoom siguiente", null, (_, _) => CycleZoom());
        menu.Items.Add("Rotar con camara", null, (_, _) => ToggleRotate());
        menu.Items.Add("-");
        menu.Items.Add("Salir", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => OpenSettings();
        tray.ShowBalloonTip(3000, "Grounded 2 Minimap", $"Activo. {cfg.KeyToggle}: mostrar/ocultar, {cfg.KeyZoom}: zoom, {cfg.KeyRotate}: rotar, {cfg.KeySettings}: configuracion.", ToolTipIcon.Info);

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            Shown += (_, _) => OpenSettings();
        reader.Start();
        timer.Tick += (_, _) => OnTick();
        timer.Start();
    }

    static Bitmap LoadMap()
    {
        var ext = Path.Combine(AppContext.BaseDirectory, "map.png");
        using Stream s = File.Exists(ext) ? File.OpenRead(ext)
            : typeof(Overlay).Assembly.GetManifestResourceStream("map.png");
        using var src = new Bitmap(s);
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Bg);
            g.DrawImage(src, 0, 0, src.Width, src.Height);
        }
        return bmp;
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

    // Called by the settings window and at startup: size, shape, opacity.
    public void ApplyLayout()
    {
        cfg.Clamp();
        Opacity = cfg.Opacity;
        ClientSize = new Size(cfg.Size, cfg.Size);
        var old = Region;
        if (cfg.Round)
        {
            using var p = new GraphicsPath();
            p.AddEllipse(0, 0, cfg.Size, cfg.Size);
            Region = new Region(p);
        }
        else Region = null;
        old?.Dispose();
        PlaceNow();
        dirty = true;
        Invalidate();
    }

    public void Redraw() { dirty = true; Invalidate(); }

    void OpenSettings()
    {
        if (settings == null || settings.IsDisposed)
        {
            settings = new SettingsForm(cfg, this);
            settings.FormClosed += (_, _) => { cfg.Save(); settings = null; };
            settings.Show();
        }
        settings.Activate();
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
        if (lastGameWnd != IntPtr.Zero && GetClientRect(lastGameWnd, out var rc))
        {
            var tl = new Point(0, 0);
            ClientToScreen(lastGameWnd, ref tl);
            Location = new Point(tl.X + rc.R - cfg.Size - cfg.MarginRight, tl.Y + cfg.MarginTop);
        }
        else PlaceOnPrimary();
    }

    bool Pressed(string keyName)
    {
        if (!Enum.TryParse<Keys>(keyName, true, out var k)) return false;
        int vk = (int)k;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        keyDown.TryGetValue(vk, out bool was);
        keyDown[vk] = down;
        return down && !was;
    }

    void OnTick()
    {
        // Visibility + placement follow the game window.
        bool gameFocused = false;
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out int fgPid);
        if (reader.GamePid != 0 && fgPid == reader.GamePid) gameFocused = true;

        if (gameFocused || !cfg.OnlyWhenGameFocused)
        {
            if (Pressed(cfg.KeyToggle)) userHidden = !userHidden;
            if (Pressed(cfg.KeyZoom)) CycleZoom();
            if (Pressed(cfg.KeyRotate)) ToggleRotate();
            if (Pressed(cfg.KeySettings)) OpenSettings();
        }

        if (gameFocused && ++focusCheck % 15 == 0)
        {
            lastGameWnd = fg;
            PlaceNow();
        }

        var s = reader.Snap;
        bool settingsOpen = settings != null && !settings.IsDisposed;
        // While the settings window is open, keep the minimap visible as a live preview.
        bool show = (!userHidden || settingsOpen) && (s.Valid || settingsOpen) && (gameFocused || settingsOpen || !cfg.OnlyWhenGameFocused);
        if (!s.Valid && settingsOpen && last != null) s = last;
        var tip = "Grounded 2 Minimap - " + reader.Status;
        if (reader.Status != lastStatus)
        {
            lastStatus = reader.Status;
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "minimap.log"), $"{DateTime.Now:HH:mm:ss} {lastStatus}{Environment.NewLine}"); } catch { }
        }
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        if (show != Visible)
        {
            if (show) { Show(); if (Location.X == 0 && Location.Y == 0) PlaceNow(); }
            else Hide();
        }
        if (!show) return;

        // Redraw only when something changed.
        if (dirty || last == null || Math.Abs(last.X - s.X) > 5 || Math.Abs(last.Y - s.Y) > 5 || Math.Abs(last.Yaw - s.Yaw) > 0.5 || last.Friends != s.Friends)
        {
            last = s; dirty = false;
            Invalidate();
        }
    }

    void PlaceOnPrimary()
    {
        var wa = Screen.PrimaryScreen.Bounds;
        Location = new Point(wa.Right - cfg.Size - cfg.MarginRight, wa.Top + cfg.MarginTop);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Bg);
        var s = last;
        if (s == null || !s.Valid) return;

        float W = ClientSize.Width, H = ClientSize.Height, cx = W / 2, cy = H / 2;
        double unitsPerPx = (cfg.MaxX - cfg.MinX) / map.Width;
        float px = (float)((s.X - cfg.MinX) / (cfg.MaxX - cfg.MinX) * map.Width);
        float py = (float)((s.Y - cfg.MinY) / (cfg.MaxY - cfg.MinY) * map.Height);
        double zoomUnits = cfg.Zoom;
        float spanPx = (float)(zoomUnits / unitsPerPx);       // map pixels visible across the minimap
        float scale = W / spanPx;
        float rot = cfg.RotateWithCamera ? (float)-(s.Yaw + 90) : 0f;

        // --- map
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var st = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(rot);
        g.ScaleTransform(scale, scale);
        float r = spanPx * 0.75f;
        var src = RectangleF.Intersect(new RectangleF(px - r, py - r, 2 * r, 2 * r), new RectangleF(0, 0, map.Width, map.Height));
        if (src.Width > 0 && src.Height > 0)
            g.DrawImage(map, new RectangleF(src.X - px, src.Y - py, src.Width, src.Height), src, GraphicsUnit.Pixel);
        g.Restore(st);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        double rad = rot * Math.PI / 180;
        PointF ToScreen(double wx, double wy)
        {
            double dx = ((wx - cfg.MinX) / (cfg.MaxX - cfg.MinX) * map.Width - px) * scale;
            double dy = ((wy - cfg.MinY) / (cfg.MaxY - cfg.MinY) * map.Height - py) * scale;
            return new PointF((float)(cx + dx * Math.Cos(rad) - dy * Math.Sin(rad)), (float)(cy + dx * Math.Sin(rad) + dy * Math.Cos(rad)));
        }

        // --- friends
        using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
        foreach (var f in s.Friends)
        {
            var p = ToScreen(f.X, f.Y);
            float lim = W / 2 - 8;
            float ddx = p.X - cx, ddy = p.Y - cy, d = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (d > lim) { p = new PointF(cx + ddx / d * lim, cy + ddy / d * lim); } // clamp to edge
            g.FillEllipse(Brushes.Black, p.X - 6, p.Y - 6, 12, 12);
            g.FillEllipse(Brushes.DeepSkyBlue, p.X - 4.5f, p.Y - 4.5f, 9, 9);
            if (!string.IsNullOrEmpty(f.Name) && d <= lim)
            {
                g.DrawString(f.Name, font, Brushes.Black, p.X + 7, p.Y - 7);
                g.DrawString(f.Name, font, Brushes.White, p.X + 6, p.Y - 8);
            }
        }

        // --- north marker (when rotating)
        if (cfg.RotateWithCamera)
        {
            // North = -Y in world (up on the map image)
            float nx = cx + (float)((W / 2 - 12) * Math.Sin(rad)), ny = cy - (float)((W / 2 - 12) * Math.Cos(rad));
            g.FillEllipse(Brushes.Black, nx - 8, ny - 8, 16, 16);
            g.DrawString("N", font, Brushes.White, nx - 5, ny - 7);
        }

        // --- player arrow
        float ang = cfg.RotateWithCamera ? -90f : (float)s.Yaw;
        var st2 = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(ang);
        var arrow = new[] { new PointF(11, 0), new PointF(-7, -7), new PointF(-3, 0), new PointF(-7, 7) };
        g.FillPolygon(Brushes.OrangeRed, arrow);
        using (var pen = new Pen(Color.White, 1.8f)) g.DrawPolygon(pen, arrow);
        g.Restore(st2);

        // --- frame
        using (var ring = new Pen(Ring, 4f))
        {
            if (cfg.Round) g.DrawEllipse(ring, 2, 2, W - 4, H - 4);
            else g.DrawRectangle(ring, 2, 2, W - 4, H - 4);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        reader.Stop();
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        map.Dispose();
        base.OnFormClosed(e);
    }
}




