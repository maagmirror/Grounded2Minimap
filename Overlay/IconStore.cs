using System.Diagnostics;

// Marker/place icons exactly as the game's map uses them. The texture list comes from the game
// (GameReader.IconPaths); missing ones are extracted from the .pak files into icons\<Name>.png.
// When a game update changes the paks, all icons are extracted again. Icons the game adds later
// are picked up automatically because the list is read from the game every time it attaches.
public sealed class IconStore
{
    readonly Action<string> log;
    readonly string dir = Path.Combine(AppContext.BaseDirectory, "icons");
    readonly Dictionary<string, Bitmap> loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
    string[] requested;
    volatile bool busy;
    volatile int version;
    int seenVersion;

    public IconStore(Action<string> log) { this.log = log; }

    static string NameOf(string path) => path.Split('.')[0].Split('/').Last();

    // UI thread: make sure every icon in the list is on disk (extraction runs in the background).
    public void Ensure(string[] paths, string gameExe)
    {
        if (paths == null || paths.Length == 0 || ReferenceEquals(paths, requested) || busy || string.IsNullOrEmpty(gameExe)) return;
        requested = paths;
        busy = true;
        new Thread(() => { try { Extract(paths, gameExe); } finally { busy = false; } })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
    }

    void Extract(string[] paths, string gameExe)
    {
        try
        {
            string paks = MapStore.PaksDir(gameExe, out string project);
            if (paks == null) return;
            Directory.CreateDirectory(dir);
            string stampFile = Path.Combine(dir, "stamp.txt"), stamp = MapStore.PaksStamp(paks);
            bool stale = !File.Exists(stampFile) || File.ReadAllText(stampFile).Trim() != stamp;
            var todo = paths.Where(p => stale || !File.Exists(Path.Combine(dir, NameOf(p) + ".png"))).ToList();
            if (todo.Count == 0) return;
            string exe = Path.Combine(AppContext.BaseDirectory, "extractor", "Extractor.exe");
            if (!File.Exists(exe)) { log("Falta extractor\\Extractor.exe: se usan iconos simples"); return; }
            log($"Extrayendo {todo.Count} iconos del juego...");
            bool allOk = true;
            foreach (var batch in todo.Chunk(80))
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "icons", paks, project, dir }.Concat(batch)) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                var err = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(180_000)) { try { p.Kill(); } catch { } }
                if (p.ExitCode != 0) { allOk = false; log($"Algunos iconos no se pudieron extraer: {err.Result.Trim().Replace(Environment.NewLine, "; ")}"); }
            }
            // Only remember the paks version once everything that can be extracted was.
            if (allOk) File.WriteAllText(stampFile, stamp);
            log("Iconos del juego listos");
        }
        catch (Exception ex) { log("Error extrayendo iconos: " + ex.Message); }
        finally { version++; }
    }

    // UI thread: the icon for a game texture path, or null if it isn't available (yet).
    public Bitmap Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (seenVersion != version)
        {
            // New extraction finished: drop cached bitmaps (they may have changed) and retry missing ones.
            seenVersion = version;
            foreach (var b in loaded.Values) b?.Dispose();
            loaded.Clear(); missing.Clear();
        }
        string name = NameOf(path);
        if (loaded.TryGetValue(name, out var bmp)) return bmp;
        if (missing.Contains(name)) return null;
        string file = Path.Combine(dir, name + ".png");
        try
        {
            if (File.Exists(file))
            {
                // Copy so the file isn't kept locked (it may be replaced by a later extraction).
                using var src = new Bitmap(file);
                bmp = new Bitmap(src);
                loaded[name] = bmp;
                return bmp;
            }
        }
        catch { }
        missing.Add(name);
        return null;
    }
}
