using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OfflineCrpConverter;

internal static class SurfaceTextureConverter
{
    public static bool SupportsShader(string shader)
        => shader.StartsWith("Custom/Buildings/Building/", StringComparison.Ordinal) ||
            shader.StartsWith("Custom/Props/Prop/", StringComparison.Ordinal) ||
            shader is "Custom/Net/RoadBridge" or "Custom/Net/Road";

    public static bool UsesNormalMap(string shader) => shader != "Custom/Net/Road";

    public static void ValidateStaticBridgeMask(CrpTexture packed, bool requireOpaque = false)
    {
        if (!packed.ForceLinear)
        {
            throw new InvalidDataException("APRMap 必须是线性数据贴图");
        }
        using var image = TextureConverter.DecodeImage(packed.ImageBytes);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                if (pixel.G != 255 || pixel.B != 0)
                {
                    throw new InvalidDataException("网络 APRMap 使用地图主题铺装/道路替换，不能仅用建筑贴图静态导出");
                }
                if (requireOpaque && pixel.R != 0)
                {
                    throw new InvalidDataException("基础道路 APR 透明通道混合底层地形，不能当普通透明材质静态导出");
                }
            }
        }
    }

    public static byte[] CreateNormalSpecularPng(CrpTexture packed)
        => CreateNormalDataPng(packed, includeSpecular: true);

    public static byte[] CreateNormalPng(CrpTexture packed)
        => CreateNormalDataPng(packed, includeSpecular: false);

    private static byte[] CreateNormalDataPng(CrpTexture packed, bool includeSpecular)
    {
        if (!packed.ForceLinear)
        {
            throw new InvalidDataException("XYS/XYCA 法线数据必须是线性贴图，不能按 sRGB 猜测通道");
        }
        using var source = TextureConverter.DecodeImage(packed.ImageBytes);
        // A fresh image avoids propagating color-profile/gamma metadata to linear data.
        using var converted = new Image<Rgba32>(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source[x, y];
                var nx = pixel.R / 255f * 2 - 1;
                var ny = pixel.G / 255f * 2 - 1;
                var lengthSquared = nx * nx + ny * ny;
                if (lengthSquared > 1)
                {
                    var inverseLength = 1 / MathF.Sqrt(lengthSquared);
                    nx *= inverseLength;
                    ny *= inverseLength;
                }
                var nz = MathF.Sqrt(MathF.Max(0, 1 - nx * nx - ny * ny));
                // The game importer packs XYS.B = 1 - source specularity.
                // glTF normalTexture ignores A; KHR_materials_specular reads linear A.
                converted[x, y] = new Rgba32(Encode(nx), Encode(ny), Encode(nz), includeSpecular ? (byte)(255 - pixel.B) : (byte)255);
            }
        }
        using var output = new MemoryStream();
        converted.SaveAsPng(output);
        return output.ToArray();
    }

    private static byte Encode(float value)
        => (byte)Math.Clamp((int)MathF.Round((value * .5f + .5f) * 255), 0, 255);
}
