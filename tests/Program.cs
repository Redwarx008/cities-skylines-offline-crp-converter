using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using OfflineCrpConverter;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {++passed}: {name}");
}

void Reject(Image<Rgba32> image, string name)
{
    try { TreeMaterialColorConverter.Analyze(image, false); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new InvalidOperationException(name);
}

foreach (var color in new[] { new Rgba32(12, 35, 8), new Rgba32(24, 3, 8),
    new Rgba32(3, 6, 24), new Rgba32(14, 14, 14), new Rgba32(1, 1, 1) })
{
    using var image = new Image<Rgba32>(8, 1);
    for (var x = 0; x < 8; x++) image[x, 0] = x < 4 ? color : new Rgba32(255, 255, 255, 0);
    var analysis = TreeMaterialColorConverter.Analyze(image, false);
    Check(analysis.VisibleTexels == 4 && analysis.ShadowGamma is > 0 and < 1,
        $"Only visible pixels drive adaptation: {color}");
    TreeMaterialColorConverter.ApplyAutomatic(image, analysis, false);
    var pixel = image[0, 0];
    var peak = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
    Check(peak is >= 90 and <= 140 && pixel.A == 255 && image[7, 0].A == 0,
        $"Dark green/red/blue/gray lift without neon or alpha changes: {color}");
    Check(Math.Sign(color.R - color.G) == Math.Sign(pixel.R - pixel.G) &&
        Math.Sign(color.G - color.B) == Math.Sign(pixel.G - pixel.B), "Channel ordering preserved");
}

using (var bright = new Image<Rgba32>(3, 1))
{
    var palette = new[] { new Rgba32(100, 180, 80), new Rgba32(210, 40, 30), new Rgba32(20, 40, 230) };
    for (var x = 0; x < 3; x++) bright[x, 0] = palette[x];
    var analysis = TreeMaterialColorConverter.Analyze(bright, false);
    TreeMaterialColorConverter.ApplyAutomatic(bright, analysis, false);
    Check(analysis.ShadowGamma == 1 && analysis.Saturation == 1 &&
        Enumerable.Range(0, 3).All(x => bright[x, 0] == palette[x]), "Bright multicolor identity");
}
using (var empty = new Image<Rgba32>(2, 1)) Reject(empty, "All-transparent input rejected");
using (var black = new Image<Rgba32>(2, 1, new Rgba32(0, 0, 0, 255))) Reject(black, "Black visible input rejected");
using (var threshold = new Image<Rgba32>(2, 1))
{
    threshold[0, 0] = new Rgba32(20, 30, 10, 127);
    threshold[1, 0] = new Rgba32(20, 30, 10, 128);
    Check(TreeMaterialColorConverter.Analyze(threshold, false).VisibleTexels == 1, "Alpha cutoff matches glTF MASK");
}
using (var linear = new Image<Rgba32>(1, 1, new Rgba32(64, 100, 40, 191)))
{
    var analysis = TreeMaterialColorConverter.Analyze(linear, true);
    TreeMaterialColorConverter.ApplyAutomatic(linear, analysis, true);
    Check(analysis.ShadowGamma == 1 && Math.Abs(linear[0, 0].G - 168) <= 1 && linear[0, 0].A == 191,
        "ForceLinear encoded to sRGB exactly once");
}

static byte[] Png(Image<Rgba32> image)
{
    using var stream = new MemoryStream();
    image.SaveAsPng(stream);
    return stream.ToArray();
}

using var diffuse = new Image<Rgba32>(2, 1);
diffuse[0, 0] = new Rgba32(12, 35, 8, 255);
diffuse[1, 0] = new Rgba32(255, 255, 255, 255);
using var mask = new Image<Rgba32>(2, 1);
mask[0, 0] = new Rgba32(0, 0, 0, 0);
mask[1, 0] = new Rgba32(0, 0, 0, 255);
var source = new CrpTexture(false, Png(diffuse));
var cutout = new CrpTexture(false, Png(mask));
var png = TextureConverter.CreateBaseColorPng(source, cutout, CutoutTextureChannel.Alpha, null, true, out var stats);
using (var merged = Image.Load<Rgba32>(png))
    Check(stats!.VisibleTexels == 1 && merged[0, 0].A == 255 && merged[1, 0].A == 0 && stats.ShadowGamma < 1,
        "XYCA alpha merged before visible-pixel analysis");

CrpPackage Package(string shader) => new()
{
    SourcePath = "generated fixture, no workshop asset",
    MainAssetName = "fixture", MainObjectName = "fixture", MainObjectChecksum = "object",
    Meshes = new Dictionary<string, CrpMesh> { ["mesh"] = new(
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [], [new[] { 0, 1, 2 }]) },
    Textures = new Dictionary<string, CrpTexture> { ["diffuse"] = source, ["cutout"] = cutout },
    Materials = new Dictionary<string, CrpMaterial> { ["material"] = new(shader,
        new Dictionary<string, Vector4>(), new Dictionary<string, Vector4>(), new Dictionary<string, float>(),
        shader == "Custom/Trees/Default" ? new Dictionary<string, string> { ["_MainTex"] = "diffuse", ["_XYCAMap"] = "cutout" }
            : new Dictionary<string, string> { ["_MainTex"] = "diffuse" }) },
    GameObjects = new Dictionary<string, CrpGameObject> { ["object"] = new("fixture", "object",
        CrpTransform.Identity, "mesh", ["material"], [], "TreeInfo", [], false) },
};

static byte[] EmbeddedPng(byte[] glb)
{
    var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
    using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
    var root = document.RootElement;
    var image = root.GetProperty("images")[0];
    var view = root.GetProperty("bufferViews")[image.GetProperty("bufferView").GetInt32()];
    var offset = view.GetProperty("byteOffset").GetInt32();
    return glb.AsSpan(28 + jsonLength + offset, view.GetProperty("byteLength").GetInt32()).ToArray();
}

var package = Package("Custom/Trees/Default");
var automaticGlb = GltfBuilder.Build(package, "fixture"); // Deliberately no color arguments.
var rawGlb = GltfBuilder.Build(package, "fixture", preserveTreeColors: true);
GlbValidator.Validate(automaticGlb, package, "fixture");
using (var autoImage = Image.Load<Rgba32>(EmbeddedPng(automaticGlb)))
using (var rawImage = Image.Load<Rgba32>(EmbeddedPng(rawGlb)))
    Check(autoImage[0, 0].G > rawImage[0, 0].G && rawImage[0, 0] == diffuse[0, 0] &&
        autoImage[1, 0].A == rawImage[1, 0].A, "Actual default GLB builder adapts; explicit raw preserves RGB and alpha");
var prop = Package("Custom/Props/Prop/Default");
Check(GltfBuilder.Build(prop, "fixture").SequenceEqual(GltfBuilder.Build(prop, "fixture", preserveTreeColors: true)),
    "Non-tree GLB byte-identical");
var manual = TextureConverter.CreateBaseColorPng(source, cutout, CutoutTextureChannel.Alpha,
    new TreeColorCorrection(0, 1), false, out var manualStats);
using (var manualImage = Image.Load<Rgba32>(manual))
    Check(manualStats is null && manualImage[0, 0] == diffuse[0, 0] && manualImage[1, 0].A == 0,
        "Manual identity bypasses automatic policy");
Console.WriteLine($"TREE_COLOR_REGRESSIONS={passed}");
