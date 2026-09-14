using System.Buffers.Binary;
using System.Text.Json;
using SixLabors.ImageSharp;

namespace OfflineCrpConverter;

internal static class GlbValidator
{
    public static void Validate(byte[] glb, CrpPackage package, string assetName)
    {
        if (glb.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != 0x46546C67 ||
            BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)) != glb.Length)
        {
            throw new InvalidDataException("生成结果不是有效的 GLB 2.0 容器");
        }

        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12)));
        if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)) != 0x4E4F534A ||
            jsonLength <= 0 || 20 + jsonLength + 8 > glb.Length)
        {
            throw new InvalidDataException("GLB JSON chunk 无效");
        }

        using var document = JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var sceneName = root.GetProperty("scenes")[0].GetProperty("name").GetString();
        if (!string.Equals(sceneName, assetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("GLB 场景名称与请求名称不一致");
        }

        var meshCount = root.GetProperty("meshes").GetArrayLength();
        var primitiveCount = root.GetProperty("meshes")
            .EnumerateArray()
            .Sum(static mesh => mesh.GetProperty("primitives").GetArrayLength());
        var materialCount = root.GetProperty("materials").GetArrayLength();
        if (meshCount != package.ReachableMeshCount ||
            primitiveCount != package.ReachablePrimitiveCount ||
            materialCount != package.ReachableMaterialCount)
        {
            throw new InvalidDataException(
                $"GLB 完整性计数不一致：网格 {meshCount}/{package.ReachableMeshCount}，" +
                $"图元 {primitiveCount}/{package.ReachablePrimitiveCount}，" +
                $"材质 {materialCount}/{package.ReachableMaterialCount}");
        }

        foreach (var material in root.GetProperty("materials").EnumerateArray())
        {
            if (!material.GetProperty("doubleSided").GetBoolean())
            {
                throw new InvalidDataException("GLB 存在未设置双面的材质");
            }
        }

        var binaryHeader = 20 + jsonLength;
        var binaryLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binaryHeader)));
        if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binaryHeader + 4)) != 0x004E4942 ||
            binaryHeader + 8 + binaryLength != glb.Length)
        {
            throw new InvalidDataException("GLB BIN chunk 无效");
        }

        var binaryStart = binaryHeader + 8;
        var views = root.GetProperty("bufferViews");
        foreach (var image in root.GetProperty("images").EnumerateArray())
        {
            var view = views[image.GetProperty("bufferView").GetInt32()];
            var offset = view.TryGetProperty("byteOffset", out var offsetValue) ? offsetValue.GetInt32() : 0;
            var length = view.GetProperty("byteLength").GetInt32();
            if (offset < 0 || length <= 0 || offset + length > binaryLength)
            {
                throw new InvalidDataException("GLB 内嵌贴图的 bufferView 越界");
            }

            using var decoded = Image.Load(glb.AsSpan(binaryStart + offset, length));
            if (decoded.Width <= 0 || decoded.Height <= 0)
            {
                throw new InvalidDataException("GLB 内嵌贴图无法解码");
            }
        }
    }
}
