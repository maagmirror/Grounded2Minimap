using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.MappingsProvider.Usmap;
using SkiaSharp;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;

// Modes:
//   map  <paksDir> <project> <widgetGamePath> <w> <h> <out.png>
//        Used by the minimap: finds the map texture referenced by the level's map background
//        widget (from the game's LevelData) and writes it as PNG. Exit code 0 = ok.
//   icons <paksDir> <project> <outDir> <texGamePath>...
//        Extracts UI textures (marker icons) as <outDir>/<TextureName>.png. Exit code 0 if all ok,
//        5 if some failed (the rest are still written).
//   list <filter> [GAME_x] [paksDir]
//   tex  <assetPath> [GAME_x] [outDir] [paksDir]
//   raw  <assetPath> [GAME_x] <out> [paksDir]
//   bc7  <raw> <offset> <out.png> <w> <h>
var mode = args.Length > 0 ? args[0] : "list";

if (mode == "bc7")
{
    var raw = File.ReadAllBytes(args[1]);
    int off = int.Parse(args[2]); int w = int.Parse(args[4]); int h = int.Parse(args[5]);
    SavePng(Decode("BC7", raw.AsSpan(off, w * h).ToArray(), w, h), w, h, args[3]);
    return 0;
}

string paks = mode is "map" or "icons" ? args[1] : (args.Length > 4 ? args[4] : Environment.GetEnvironmentVariable("GROUNDED2_PAKS"));
if (string.IsNullOrEmpty(paks) || !Directory.Exists(paks)) { Console.Error.WriteLine("paks dir not found: " + paks); return 2; }
var game = mode is not ("map" or "icons") && args.Length > 2 && args[2].StartsWith("GAME_") && Enum.TryParse<EGame>(args[2], out var g) ? g : EGame.GAME_UE5_LATEST;

var oodlePath = Path.Combine(AppContext.BaseDirectory, OodleHelper.OODLE_NAME_CURRENT);
if (!File.Exists(oodlePath)) OodleHelper.DownloadOodleDll(ref oodlePath);
OodleHelper.Initialize(oodlePath);

var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(game), StringComparer.OrdinalIgnoreCase);
var usmap = Path.Combine(AppContext.BaseDirectory, "Mappings.usmap");
if (File.Exists(usmap)) provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap, StringComparer.OrdinalIgnoreCase);
provider.Initialize();
provider.Mount();
Console.WriteLine($"Files: {provider.Files.Count}");

if (mode == "map") return ExtractMap(args[2], args[3], int.Parse(args[4]), int.Parse(args[5]), args[6]);
if (mode == "icons") return ExtractIcons(args[2], args[3], args.Skip(4).ToArray());

if (mode == "list")
{
    var filter = args.Length > 1 ? args[1] : "map";
    foreach (var k in provider.Files.Keys.Where(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k))
        Console.WriteLine(k);
}
else if (mode == "tex")
{
    var outDir = args.Length > 3 ? args[3] : ".";
    var pkg = provider.LoadPackage(args[1]);
    foreach (var obj in pkg.GetExports())
    {
        if (obj is not UTexture2D tex) { Console.WriteLine("export " + obj.ExportType + " " + obj.Name); continue; }
        var bmp = tex.Decode();
        if (bmp == null) { Console.WriteLine("decode failed " + tex.Name); continue; }
        var outFile = Path.Combine(outDir, tex.Name + ".png");
        using var sk = bmp.ToSkBitmap();
        using var data = sk.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outFile, data.ToArray());
        Console.WriteLine($"saved {outFile} {bmp.Width}x{bmp.Height}");
    }
}
else if (mode == "raw")
{
    var bytes = provider.SaveAsset(args[1]);
    File.WriteAllBytes(args[3], bytes);
    Console.WriteLine($"raw {bytes.Length}");
}
return 0;

// "/Game/UI/Map/UI_MapBGBackyard" -> "<project>/Content/UI/Map/UI_MapBGBackyard.uasset"
string GamePathToFile(string project, string gamePath)
{
    var p = gamePath.Split('.')[0];
    if (p.StartsWith("/Game/")) p = project + "/Content/" + p["/Game/".Length..];
    return p.TrimStart('/') + ".uasset";
}

int ExtractMap(string project, string widgetPath, int w, int h, string outPng)
{
    // Packages are unversioned (no .usmap available), so read the widget's raw bytes and pick
    // the referenced texture paths out of its name table.
    var widgetFile = GamePathToFile(project, widgetPath);
    if (!provider.Files.ContainsKey(widgetFile)) { Console.Error.WriteLine("widget not found: " + widgetFile); return 3; }
    var widgetRaw = Encoding.ASCII.GetString(provider.SaveAsset(widgetFile));
    var candidates = Regex.Matches(widgetRaw, @"/Game/[A-Za-z0-9_/\-]+").Select(m => GamePathToFile(project, m.Value))
        .Distinct().Where(f => provider.Files.ContainsKey(f)).OrderByDescending(f => provider.Files[f].Size).ToList();
    Console.WriteLine("candidates: " + string.Join(", ", candidates));

    foreach (var file in candidates)
    {
        var raw = provider.SaveAsset(file);
        if (!TryDecodeTexture(raw, w, h, out var rgba, out int tw, out int th, out string fmt)) continue;
        Console.WriteLine($"{file}: {fmt} {tw}x{th}");
        SavePng(rgba, tw, th, outPng);
        Console.WriteLine("saved " + outPng);
        return 0;
    }
    Console.Error.WriteLine("no map texture found");
    return 4;
}

int ExtractIcons(string project, string outDir, string[] paths)
{
    Directory.CreateDirectory(outDir);
    int fails = 0;
    foreach (var path in paths)
    {
        try
        {
            var file = GamePathToFile(project, path);
            if (!provider.Files.ContainsKey(file)) { Console.Error.WriteLine("missing " + file); fails++; continue; }
            var raw = provider.SaveAsset(file);
            if (!TryDecodeTexture(raw, 0, 0, out var rgba, out int w, out int h, out string fmt)) { Console.Error.WriteLine("undecodable " + file); fails++; continue; }
            var name = path.Split('.')[0].Split('/').Last();
            SavePng(rgba, w, h, Path.Combine(outDir, name + ".png"));
            Console.WriteLine($"{name}: {fmt} {w}x{h}");
        }
        catch (Exception ex) { Console.Error.WriteLine(path + ": " + ex.Message); fails++; }
    }
    return fails == 0 ? 0 : 5;
}

// Texture packages here are unversioned, so the header can't be parsed without mappings.
// UI textures have a single mip stored at the end of the package (followed by a small trailer):
// with the pixel format from the name table, the size follows from the file length.
static bool TryDecodeTexture(byte[] raw, int w, int h, out byte[] rgba, out int tw, out int th, out string fmt)
{
    rgba = null; tw = th = 0; fmt = null;
    int pf = raw.AsSpan().IndexOf("PF_"u8);
    if (pf < 0) return false;
    int e = pf + 3;
    while (e < raw.Length && (char.IsLetterOrDigit((char)raw[e]) || raw[e] == '_') && e - pf < 24) e++;
    var fmtRaw = Encoding.ASCII.GetString(raw, pf + 3, e - pf - 3);
    fmt = new[] { "B8G8R8A8", "BC7", "BC5", "BC4", "DXT1", "DXT3", "DXT5" }.FirstOrDefault(fmtRaw.StartsWith);
    if (fmt == null) return false;
    long size;
    if (w > 0 && h > 0 && raw.Length >= MipSize(fmt, w, h) + 64) { tw = w; th = h; size = MipSize(fmt, w, h); }
    else
    {
        // square texture whose header is a few hundred bytes
        int side = (int)Math.Sqrt(Math.Max(0, raw.Length - 400) / BytesPerPixel(fmt));
        if (fmt != "B8G8R8A8") side &= ~3;
        while (side >= 4 && raw.Length - MipSize(fmt, side, side) < 300) side -= fmt == "B8G8R8A8" ? 1 : 4;
        if (side < 4 || raw.Length - MipSize(fmt, side, side) > 1200) return false;
        tw = th = side; size = MipSize(fmt, side, side);
    }
    int off = BestOffset(raw, fmt, (int)size);
    if (off < 0) return false;
    rgba = Decode(fmt, raw.AsSpan(off, (int)size).ToArray(), tw, th);
    return true;
}

static double BytesPerPixel(string fmt) => fmt switch { "B8G8R8A8" => 4, "DXT1" or "BC4" => 0.5, _ => 1 };
static long MipSize(string fmt, int w, int h) => (long)(w * (long)h * BytesPerPixel(fmt));

// Mip 0 sits at the end of the package followed by a small trailer; try the usual trailer sizes
// and keep the one whose blocks look valid (BC7: first byte of a block is never 0).
static int BestOffset(byte[] raw, string fmt, int size)
{
    int best = -1; double bestScore = -1;
    foreach (int trailer in new[] { 24, 0, 16, 20, 28, 32, 8, 4 })
    {
        int off = raw.Length - trailer - size;
        if (off < 0) continue;
        double score = 1;
        if (fmt == "BC7")
        {
            int ok = 0, n = 0;
            for (int b = 0; b < size; b += Math.Max(16, size / 4096 / 16 * 16)) { n++; if (raw[off + b] != 0) ok++; }
            score = n == 0 ? 0 : (double)ok / n;
        }
        if (score > bestScore + 0.02) { bestScore = score; best = off; }
        if (trailer == 24 && score > 0.98) return off;
    }
    return best;
}

static byte[] Decode(string fmt, byte[] data, int w, int h) => fmt switch
{
    "BC7" => BCDecoder.BC7(data, w, h, 1),
    "DXT1" => BCDecoder.BC1(data, w, h, 1),
    "DXT3" => BCDecoder.BC2(data, w, h, 1),
    "DXT5" => BCDecoder.BC3(data, w, h, 1),
    "BC4" => BCDecoder.BC4(data, w, h, 1),
    "BC5" => BCDecoder.BC5(data, w, h, 1),
    _ => BgraToRgba(data),
};

static byte[] BgraToRgba(byte[] d)
{
    for (int i = 0; i + 3 < d.Length; i += 4) (d[i], d[i + 2]) = (d[i + 2], d[i]);
    return d;
}

static void SavePng(byte[] rgba, int w, int h, string outPng)
{
    using var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    System.Runtime.InteropServices.Marshal.Copy(rgba, 0, sk.GetPixels(), Math.Min(rgba.Length, w * h * 4));
    using var data = sk.Encode(SKEncodedImageFormat.Png, 100);
    var tmp = outPng + ".tmp";
    File.WriteAllBytes(tmp, data.ToArray());
    File.Move(tmp, outPng, true);
}
