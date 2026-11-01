using System.Numerics;

namespace OfflineCrpConverter;

// C:S appends white (alpha=1) vertices for its theme-textured underground skirt.
// Authored vertices have alpha=0. Never infer a basement from negative Y alone.
internal static class GeneratedBuildingBaseRemover
{
    public static CrpPackage Apply(CrpPackage package, Action<string>? report = null)
    {
        Dictionary<string, CrpMesh>? meshes = null;
        Dictionary<string, CrpGameObject>? objects = null;
        foreach (var gameObject in package.GetReachableObjects().DistinctBy(static item => item.AssetChecksum))
        {
            if (gameObject.MeshChecksum is null)
            {
                continue;
            }

            var source = package.Meshes[gameObject.MeshChecksum];
            var prefix = FindAuthoredPrefix(source);
            if (prefix == 0)
            {
                continue;
            }

            var filtered = new int[source.SubMeshTriangles.Count][];
            var removed = 0;
            var ambiguous = false;
            for (var slot = 0; slot < source.SubMeshTriangles.Count && !ambiguous; slot++)
            {
                var indices = source.SubMeshTriangles[slot];
                var kept = new List<int>(indices.Length);
                for (var triangle = 0; triangle < indices.Length; triangle += 3)
                {
                    var generated = 0;
                    for (var corner = 0; corner < 3; corner++)
                    {
                        generated += indices[triangle + corner] >= prefix ? 1 : 0;
                    }
                    if (generated == 0)
                    {
                        kept.AddRange(indices.AsSpan(triangle, 3).ToArray());
                        continue;
                    }
                    if (generated != 3 || slot >= gameObject.MaterialChecksums.Count ||
                        !IsBuildingShader(package.Materials[gameObject.MaterialChecksums[slot]].ShaderName))
                    {
                        ambiguous = true;
                        break;
                    }
                    removed++;
                }
                filtered[slot] = kept.ToArray();
            }
            if (ambiguous || removed == 0 || filtered.All(static indices => indices.Length == 0))
            {
                continue;
            }

            meshes ??= new Dictionary<string, CrpMesh>(package.Meshes, StringComparer.OrdinalIgnoreCase);
            objects ??= new Dictionary<string, CrpGameObject>(package.GameObjects, StringComparer.OrdinalIgnoreCase);
            var checksum = gameObject.MeshChecksum + ":without-generated-base:" + string.Join(',', gameObject.MaterialChecksums);
            meshes[checksum] = new CrpMesh(source.Positions[..prefix], source.Colors[..prefix],
                source.TextureCoordinates[..prefix], source.Normals[..prefix],
                source.Tangents.Length == 0 ? [] : source.Tangents[..prefix], filtered);
            objects[gameObject.AssetChecksum] = gameObject with { MeshChecksum = checksum };
            report?.Invoke($"移除游戏生成底座：{gameObject.Name}，{removed} 个三角形；保留原始建筑 {prefix} 个顶点（含原台阶和负高度细节）");
        }
        if (meshes is null || objects is null)
        {
            return package;
        }
        return new CrpPackage
        {
            SourcePath = package.SourcePath,
            MainAssetName = package.MainAssetName,
            MainObjectName = package.MainObjectName,
            MainObjectChecksum = package.MainObjectChecksum,
            Meshes = meshes, Textures = package.Textures, Materials = package.Materials, GameObjects = objects,
            DependencySourcePaths = package.DependencySourcePaths,
            GroundDecalMaterialChecksums = package.GroundDecalMaterialChecksums,
        };
    }

    private static bool IsBuildingShader(string shader)
        => shader is "Custom/Buildings/Building/Default" or "Custom/Buildings/Building/NoBase" or
            "Custom/Buildings/Building/Fence";

    private static int FindAuthoredPrefix(CrpMesh mesh)
    {
        if (mesh.Colors.Length != mesh.Positions.Length || mesh.Normals.Length != mesh.Positions.Length ||
            mesh.TextureCoordinates.Length != mesh.Positions.Length ||
            (mesh.Tangents.Length != 0 && mesh.Tangents.Length != mesh.Positions.Length))
        {
            return 0;
        }
        var prefix = Array.FindIndex(mesh.Colors, static color => color == Vector4.One);
        if (prefix <= 0 || mesh.Colors[..prefix].Any(static color => color.W != 0))
        {
            return 0;
        }
        var hasTop = false;
        var hasBottom = false;
        for (var index = prefix; index < mesh.Positions.Length; index++)
        {
            var y = mesh.Positions[index].Y;
            var top = MathF.Abs(y) <= 0.00001f;
            var bottom = MathF.Abs(y + 1) <= 0.00001f;
            if (mesh.Colors[index] != Vector4.One || (!top && !bottom) ||
                MathF.Abs(mesh.Normals[index].Y) > 0.00001f)
            {
                return 0;
            }
            hasTop |= top;
            hasBottom |= bottom;
        }
        return hasTop && hasBottom ? prefix : 0;
    }
}
