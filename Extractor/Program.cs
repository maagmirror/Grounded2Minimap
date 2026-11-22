using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.MappingsProvider.Usmap;
using SkiaSharp;
using CUE4Parse_Conversion.Textures;


var paks = @"F:\SteamLibrary\steamapps\common\Grounded2\Augusta\Content\Paks";
var mode = args.Length > 0 ? args[0] : "list";
var game = args.Length > 2 && args[2].StartsWith("GAME_") && Enum.TryParse<EGame>(args[2], out var g) ? g : EGame.GAME_UE5_LATEST;

var oodlePath = Path.Combine(AppContext.BaseDirectory, OodleHelper.OODLE_NAME_CURRENT);
if (!File.Exists(oodlePath)) OodleHelper.DownloadOodleDll(ref oodlePath);
OodleHelper.Initialize(oodlePath);

var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(game), StringComparer.OrdinalIgnoreCase);
var usmap = Path.Combine(AppContext.BaseDirectory, "Mappings.usmap");
if (File.Exists(usmap)) provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap, StringComparer.OrdinalIgnoreCase);
provider.Initialize();
provider.Mount();
Console.WriteLine($"Files: {provider.Files.Count}");

if (mode == "list")
{
    var filter = args.Length > 1 ? args[1] : "map";
    foreach (var k in provider.Files.Keys.Where(k => k.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k))
        Console.WriteLine(k);
}
else if (mode == "tex")
{
    var path = args[1];
    var outDir = args.Length > 3 ? args[3] : ".";
    var pkg = provider.LoadPackage(path);
    Console.WriteLine($"pkg ver ue5={pkg.Summary.FileVersionUE.FileVersionUE5} lic={pkg.Summary.FileVersionLicenseeUE} unversioned={pkg.Summary.bUnversioned}");
    foreach (var obj in pkg.GetExports())
    {
        if (obj is UTexture2D tex)
        {
            foreach (var pr in tex.Properties) Console.WriteLine($"  prop {pr.Name} = {pr.Tag}");
            Console.WriteLine($"fmt={tex.Format} mips={tex.PlatformData?.Mips?.Length} size={tex.PlatformData?.SizeX}x{tex.PlatformData?.SizeY} vt={tex.PlatformData?.VTData != null}");
            var bmp = tex.Decode();
            if (bmp == null) { Console.WriteLine("decode failed " + tex.Name); continue; }
            var outFile = Path.Combine(outDir, tex.Name + ".png");
            using var sk = bmp.ToSkBitmap();
            using var data = sk.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(outFile, data.ToArray());
            Console.WriteLine($"saved {outFile} {bmp.Width}x{bmp.Height}");
        }
        else Console.WriteLine("export " + obj.ExportType + " " + obj.Name);
    }
}




if (mode == "raw")
{
    var bytes = provider.SaveAsset(args[1]);
    File.WriteAllBytes(args[3], bytes);
    Console.WriteLine($"raw {bytes.Length}");
    foreach (var kv in provider.Files.Where(k => k.Key.StartsWith(args[1].Replace(".uasset", ""), StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine("sibling " + kv.Key + " " + kv.Value.Size);
}

if (mode == "bc7")
{
    // args: bc7 <raw> <offset> <out.png> <w> <h>
    var raw = File.ReadAllBytes(args[1]);
    int off = int.Parse(args[2]); int w = int.Parse(args[4]); int h = int.Parse(args[5]);
    var blk = raw.AsSpan(off, w * h).ToArray();
    var rgba = CUE4Parse_Conversion.Textures.BC.BCDecoder.BC7(blk, w, h, 1);
    Console.WriteLine($"decoded {rgba.Length}");
    using var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    System.Runtime.InteropServices.Marshal.Copy(rgba, 0, sk.GetPixels(), Math.Min(rgba.Length, w * h * 4));
    using var data = sk.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(args[3], data.ToArray());
    using var small = sk.Resize(new SKImageInfo(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.Medium);
    File.WriteAllBytes(args[3].Replace(".png", "_preview.png"), small.Encode(SKEncodedImageFormat.Png, 100).ToArray());
}


