using System.Numerics;

namespace OfflineCrpConverter;

internal static class GroundDecalConverter
{
    public static CrpPackage Apply(CrpPackage package, float localGroundHeight)
    {
        var meshes = new Dictionary<string, CrpMesh>(package.Meshes, StringComparer.OrdinalIgnoreCase);
        var objects = new Dictionary<string, CrpGameObject>(package.GameObjects, StringComparer.OrdinalIgnoreCase);
        var decalMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gameObject in package.GetReachableObjects().Where(static item => item.MeshChecksum is not null))
        {
            var source = package.Meshes[gameObject.MeshChecksum!];
            var hasDecalMaterial = source.SubMeshTriangles.Select((indices, slot) => (indices, slot))
                .Where(static item => item.indices.Length != 0)
                .Any(item => package.Materials[gameObject.MaterialChecksums[item.slot]].ShaderName
                    .StartsWith("Custom/Props/Decal/", StringComparison.Ordinal));
            var projectedIndices = new int[source.SubMeshTriangles.Count][];
            var hasDecal = false;
            for (var slot = 0; slot < source.SubMeshTriangles.Count; slot++)
            {
                var triangles = source.SubMeshTriangles[slot];
                if (triangles.Length == 0)
                {
                    projectedIndices[slot] = [];
                    continue;
                }

                var checksum = gameObject.MaterialChecksums[slot];
                var shader = package.Materials[checksum].ShaderName;
                if (!shader.StartsWith("Custom/Props/Decal/", StringComparison.Ordinal))
                {
                    if (hasDecalMaterial)
                    {
                        throw new InvalidDataException($"网格 {gameObject.Name} 混合贴花与实体材质，需要独立的表面投影处理");
                    }

                    projectedIndices[slot] = triangles;
                    continue;
                }

                hasDecal = true;
                decalMaterials.Add(checksum);
                var top = triangles.Max(index => source.Positions[index].Y);
                var selected = new List<int>();
                for (var index = 0; index < triangles.Length; index += 3)
                {
                    if (Enumerable.Range(index, 3).All(vertex =>
                        MathF.Abs(source.Positions[triangles[vertex]].Y - top) <= 0.00001f &&
                        source.Normals[triangles[vertex]].Y > 0.5f))
                    {
                        selected.AddRange(triangles.AsSpan(index, 3).ToArray());
                    }
                }

                if (selected.Count == 0)
                {
                    throw new InvalidDataException($"贴花 {gameObject.Name} 没有带源 UV 的水平朝上投影面，不能按平地转换");
                }

                projectedIndices[slot] = selected.ToArray();
            }

            if (!hasDecal)
            {
                continue;
            }

            var sourceIndices = projectedIndices.SelectMany(static indices => indices).Distinct().ToArray();
            var indexMap = sourceIndices.Select((sourceIndex, index) => (sourceIndex, index))
                .ToDictionary(static item => item.sourceIndex, static item => item.index);
            var projectedChecksum = gameObject.MeshChecksum + ":ground:" + string.Join(',', gameObject.MaterialChecksums);
            meshes[projectedChecksum] = new CrpMesh(
                sourceIndices.Select(index => new Vector3(source.Positions[index].X, localGroundHeight, source.Positions[index].Z)).ToArray(),
                source.Colors.Length == 0 ? [] : sourceIndices.Select(index => source.Colors[index]).ToArray(),
                sourceIndices.Select(index => source.TextureCoordinates[index]).ToArray(),
                sourceIndices.Select(static _ => Vector3.UnitY).ToArray(),
                source.Tangents.Length == 0 ? [] : sourceIndices.Select(index => source.Tangents[index]).ToArray(),
                projectedIndices.Select(indices => indices.Select(index => indexMap[index]).ToArray()).ToArray());
            objects[gameObject.AssetChecksum] = gameObject with { MeshChecksum = projectedChecksum };
        }

        return new CrpPackage
        {
            SourcePath = package.SourcePath,
            MainAssetName = package.MainAssetName,
            MainObjectName = package.MainObjectName,
            MainObjectChecksum = package.MainObjectChecksum,
            Meshes = meshes,
            Textures = package.Textures,
            Materials = package.Materials,
            GameObjects = objects,
            DependencySourcePaths = package.DependencySourcePaths,
            GroundDecalMaterialChecksums = decalMaterials,
        };
    }
}
