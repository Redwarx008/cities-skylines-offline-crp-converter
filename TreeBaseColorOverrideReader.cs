using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace OfflineCrpConverter;

internal sealed record TreeBaseColorOverride(byte[] Png, Vector4 Factor);

internal static class TreeBaseColorOverrideReader
{
    public static IReadOnlyDictionary<string, TreeBaseColorOverride> Read(string path)
        => Read(File.ReadAllBytes(path));

    public static IReadOnlyDictionary<string, TreeBaseColorOverride> Read(byte[] bytes)
    {
        if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
        {
            throw new InvalidDataException("复用树木颜色的输入不是有效 GLB 2.0");
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, length));
        var root = document.RootElement;
        var binaryStart = 28 + length;
        var result = new Dictionary<string, TreeBaseColorOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in root.GetProperty("materials").EnumerateArray())
        {
            var pbr = material.GetProperty("pbrMetallicRoughness");
            if (!pbr.TryGetProperty("baseColorTexture", out var binding))
            {
                continue;
            }
            var texture = root.GetProperty("textures")[binding.GetProperty("index").GetInt32()];
            var image = root.GetProperty("images")[texture.GetProperty("source").GetInt32()];
            if (!image.TryGetProperty("bufferView", out var viewIndex) || image.GetProperty("mimeType").GetString() != "image/png")
            {
                throw new InvalidDataException("复用树木颜色只接受内嵌 PNG，不读取外部文件");
            }
            var view = root.GetProperty("bufferViews")[viewIndex.GetInt32()];
            var offset = view.TryGetProperty("byteOffset", out var start) ? start.GetInt32() : 0;
            var size = view.GetProperty("byteLength").GetInt32();
            var factor = pbr.TryGetProperty("baseColorFactor", out var color)
                ? new Vector4(color[0].GetSingle(), color[1].GetSingle(), color[2].GetSingle(), color[3].GetSingle()) : Vector4.One;
            if (offset < 0 || size <= 0 || (long)binaryStart + offset + size > bytes.Length ||
                !float.IsFinite(factor.X) || !float.IsFinite(factor.Y) || !float.IsFinite(factor.Z) || factor.W != 1)
            {
                throw new InvalidDataException("复用树木颜色的图像范围或基础色因子无效（不允许改变透明因子）");
            }
            result.Add(material.GetProperty("name").GetString()!, new TreeBaseColorOverride(bytes.AsSpan(binaryStart + offset, size).ToArray(), factor));
        }
        return result;
    }

    public static void ValidateAlpha(byte[] sourcePng, byte[] preservedPng)
    {
        using var source = TextureConverter.DecodeImage(sourcePng);
        using var previous = TextureConverter.DecodeImage(preservedPng);
        if (source.Width != previous.Width || source.Height != previous.Height)
        {
            throw new InvalidDataException("复用树木颜色的贴图尺寸与源资产不一致");
        }
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (Math.Abs(source[x, y].A - previous[x, y].A) > 1)
                {
                    throw new InvalidDataException("复用树木颜色的透明遮罩与源资产不一致，拒绝沿用错误透明度");
                }
            }
        }
    }
}
