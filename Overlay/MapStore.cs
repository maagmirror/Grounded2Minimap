using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

// Map image for the level being played. Images are extracted from the game's .pak files by
// extractor\Extractor.exe and cached in maps\<Level>.png. When a game update changes the paks,
// the image is extracted again in the background (the old one stays on screen meanwhile).
// The main map (Augusta_Main) ships embedded in the exe so the first run works offline.
public sealed class MapStore
{
    public sealed class MapImage
    {
        public Bitmap Bitmap;
        public string Level;
    }

    sealed class Stamp
    {
        public string Paks { get; set; }
        public string Widget { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    const string EmbeddedLevel = "Augusta_Main";
    static readonly Color Bg = Color.FromArgb(255, 24, 28, 22);

    readonly Action<string> log;
    readonly string dir = Path.Combine(AppContext.BaseDirectory, "maps");
    readonly HashSet<string> extracting = new();
    readonly HashSet<string> failed = new();
    readonly HashSet<string> checkedLevels = new();
    volatile MapImage ready;    // set by the extraction thread, picked up by the UI thread

    public MapStore(Action<string> log) { this.log = log; }

    // UI thread: image to use for this level right now (may start a background extraction).
    public MapImage Get(LevelInfo level, string gameExe, MapImage current)
    {
        var r = ready;
        if (r != null) { ready = null; if (level == null || r.Level == level.Name) return r; }
        string name = level?.Name ?? EmbeddedLevel;
        if (level != null && checkedLevels.Add(level.Name)) CheckFresh(level, gameExe);
        if (current != null && current.Level == name) return current;

        MapImage img = null;
        string png = Path.Combine(dir, name + ".png");
        string external = Path.Combine(AppContext.BaseDirectory, "map.png");   // manual override
        if (File.Exists(external) && name == EmbeddedLevel) img = Load(File.OpenRead(external), name);
        else if (File.Exists(png)) img = Load(File.OpenRead(png), name);
        else if (name == EmbeddedLevel) img = Load(typeof(MapStore).Assembly.GetManifestResourceStream("map.png"), name);
        return img ?? new MapImage { Level = name, Bitmap = null };
    }

    // Called periodically: re-extract when the cached image is missing or the paks changed.
    public void CheckFresh(LevelInfo level, string gameExe)
    {
        if (level == null || string.IsNullOrEmpty(level.MapWidget) || string.IsNullOrEmpty(gameExe)) return;
        lock (extracting) { if (extracting.Contains(level.Name) || failed.Contains(level.Name)) return; }
        var paks = PaksDir(gameExe, out string project);
        if (paks == null) return;
        var want = new Stamp { Paks = PaksStamp(paks), Widget = level.MapWidget, W = level.TexW, H = level.TexH };
        string png = Path.Combine(dir, level.Name + ".png"), json = Path.Combine(dir, level.Name + ".json");
        try
        {
            if (File.Exists(png) && File.Exists(json))
            {
                var have = JsonSerializer.Deserialize<Stamp>(File.ReadAllText(json));
                if (have != null && have.Paks == want.Paks && have.Widget == want.Widget && have.W == want.W && have.H == want.H) return;
            }
        }
        catch { }
        lock (extracting) extracting.Add(level.Name);
        new Thread(() => Extract(level, paks, project, want, png, json)) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
    }

    void Extract(LevelInfo level, string paks, string project, Stamp want, string png, string json)
    {
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "extractor", "Extractor.exe");
            if (!File.Exists(exe)) { log("extractor\\Extractor.exe is missing: the map can't be updated"); lock (extracting) failed.Add(level.Name); return; }
            Directory.CreateDirectory(dir);
            log($"Extracting the map of {level.Name}...");
            string tmp = png + ".new.png";
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "map", paks, project, level.MapWidget, level.TexW.ToString(), level.TexH.ToString(), tmp }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(180_000)) { try { p.Kill(); } catch { } }
            if (p.ExitCode != 0 || !File.Exists(tmp))
            {
                log($"Could not extract the map of {level.Name} (code {p.ExitCode}): {errTask.Result.Trim()}");
                lock (extracting) failed.Add(level.Name);
                return;
            }
            File.Move(tmp, png, true);
            File.WriteAllText(json, JsonSerializer.Serialize(want));
            log($"Map of {level.Name} updated");
            ready = Load(File.OpenRead(png), level.Name);
        }
        catch (Exception ex)
        {
            log($"Error extracting the map: {ex.Message}");
            lock (extracting) failed.Add(level.Name);
        }
        finally { lock (extracting) extracting.Remove(level.Name); }
    }

    // ...\<Project>\Binaries\Win64|WinGDK\Game.exe -> ...\<Project>\Content\Paks
    internal static string PaksDir(string gameExe, out string project)
    {
        project = null;
        var projDir = Directory.GetParent(gameExe)?.Parent?.Parent;
        if (projDir == null) return null;
        project = projDir.Name;
        var paks = Path.Combine(projDir.FullName, "Content", "Paks");
        return Directory.Exists(paks) ? paks : null;
    }

    internal static string PaksStamp(string paks)
    {
        long h = 17;
        foreach (var f in Directory.GetFiles(paks).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(f);
            h = h * 31 + fi.Length;
            h = h * 31 + fi.LastWriteTimeUtc.Ticks;
            foreach (char c in fi.Name.ToLowerInvariant()) h = h * 31 + c;
        }
        return h.ToString("X");
    }

    static MapImage Load(Stream s, string level)
    {
        using (s)
        using (var src = new Bitmap(s))
        {
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Bg);
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return new MapImage { Bitmap = bmp, Level = level };
        }
    }
}
