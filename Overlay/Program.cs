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

    // App icon at a given size (tray = small icon size for the current DPI).
    public static Icon AppIcon(Size size)
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("app.ico");
        return s == null ? (Icon)SystemIcons.Application.Clone() : new Icon(s, size);
    }

    static readonly object logLock = new();
    static string LogPath => Path.Combine(AppContext.BaseDirectory, "minimap.log");

    // Appends to minimap.log; keeps the file small by trimming it to the last half when it grows.
    public static void Log(string line)
    {
        lock (logLock)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 256 * 1024)
                {
                    var keep = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, keep.Skip(keep.Length / 2));
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

public sealed class Config
{
    // --- Minimap
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
    // Size of markers, places, friends and creatures on the minimap (the big map draws them 30% larger).
    public int IconSize { get; set; } = 20;

    // --- What to show
    public bool Smooth { get; set; } = true;
    public bool ShowCompass { get; set; } = true;
    public bool ShowTrail { get; set; } = true;
    public int TrailSeconds { get; set; } = 180;
    public bool ShowFriends { get; set; } = true;
    public bool ShowMarkers { get; set; } = true;
    // MarkKind names turned off (Pin, Bed, Ping, Quest, Backpack, Pet, Collectible, Base, Other).
    public List<string> HiddenMarkKinds { get; set; } = new() { "Other" };
    public bool ShowPois { get; set; } = true;
    public bool PoiNames { get; set; } = false;
    public bool ShowCreatures { get; set; } = false;
    public bool CreatureNames { get; set; } = false;
    public bool OnlyHostileCreatures { get; set; } = false;
    // Creatures are hostile unless their name contains one of these words (editable here).
    public List<string> PassiveCreatures { get; set; } = new() { "Aphid", "Firefly", "Butterfly", "Snail", "Weevil", "WaterFlea", "Tadpole", "Gnat", "Grub", "Mite", "Caterpillar", "Moth", "Worm" };
    public List<string> NeutralCreatures { get; set; } = new() { "Ladybug", "Ant", "Bee", "Stinkbug", "RustBeetle", "Cockroach", "RolyPoly", "PotatoBug", "ShieldBug", "Cricket", "Dragonfly" };

    // --- Big map (hold or toggle)
    public int BigMapPercent { get; set; } = 75;
    public double BigMapZoom { get; set; } = 90000;
    public bool BigMapHold { get; set; } = true;

    // --- Keys
    public string KeyToggle { get; set; } = "F6";
    public string KeyZoom { get; set; } = "F7";
    public string KeyRotate { get; set; } = "F8";
    public string KeySettings { get; set; } = "F9";
    public string KeyBigMap { get; set; } = "F10";

    // Fallback map bounds, only used if the level data can't be read from the game.
    public double MinX { get; set; } = -261500;
    public double MaxX { get; set; } = 73500;
    public double MinY { get; set; } = -137500;
    public double MaxY { get; set; } = 197500;

    public const double ZoomMin = 4000, ZoomMax = 400000;
    public const int SizeMin = 120, SizeMax = 700;
    public const int IconSizeMin = 12, IconSizeMax = 48;

    public void Clamp()
    {
        Zoom = Math.Clamp(Zoom, ZoomMin, ZoomMax);
        BigMapZoom = Math.Clamp(BigMapZoom, ZoomMin, ZoomMax);
        Size = Math.Clamp(Size, SizeMin, SizeMax);
        Opacity = Math.Clamp(Opacity, 0.2, 1.0);
        TrailSeconds = Math.Clamp(TrailSeconds, 10, 3600);
        IconSize = Math.Clamp(IconSize, IconSizeMin, IconSizeMax);
        BigMapPercent = Math.Clamp(BigMapPercent, 30, 95);
        if (ZoomLevels == null || ZoomLevels.Length == 0) ZoomLevels = new double[] { 12000, 24000, 48000, 96000 };
        HiddenMarkKinds ??= new(); PassiveCreatures ??= new(); NeutralCreatures ??= new();
    }

    public bool ShowsKind(MarkKind k) => ShowMarkers && !HiddenMarkKinds.Contains(k.ToString());
    public void SetKind(MarkKind k, bool on)
    {
        HiddenMarkKinds.Remove(k.ToString());
        if (!on) HiddenMarkKinds.Add(k.ToString());
    }

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
