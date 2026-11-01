namespace OfflineCrpConverter;

internal static class WorkshopPrefabReader
{
    public static CrpPackage Read(string path, string? workshopDirectory, IReadOnlySet<string>? excludedPrefabNames = null)
    {
        var package = CrpPackageReader.Read(path);
        var meshes = new Dictionary<string, CrpMesh>(package.Meshes, StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, CrpTexture>(package.Textures, StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, CrpMaterial>(package.Materials, StringComparer.OrdinalIgnoreCase);
        var objects = new Dictionary<string, CrpGameObject>(package.GameObjects, StringComparer.OrdinalIgnoreCase);
        var loadedPackages = new Dictionary<string, CrpPackage>(StringComparer.OrdinalIgnoreCase);
        var activeObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedExclusions = new HashSet<string>(StringComparer.Ordinal);
        AddPlacements(package.MainObjectChecksum);
        if (excludedPrefabNames is not null)
        {
            var unmatched = excludedPrefabNames.Except(matchedExclusions, StringComparer.Ordinal).ToArray();
            if (unmatched.Length != 0)
            {
                throw new InvalidDataException($"排除选项未匹配到任何工坊道具引用：{string.Join(", ", unmatched)}");
            }
        }
        return new CrpPackage
        {
            SourcePath = package.SourcePath,
            MainAssetName = package.MainAssetName,
            MainObjectName = package.MainObjectName,
            MainObjectChecksum = package.MainObjectChecksum,
            Meshes = meshes,
            Textures = textures,
            Materials = materials,
            GameObjects = objects,
            DependencySourcePaths = loadedPackages.Keys.ToArray(),
        };

        void AddPlacements(string checksum)
        {
            if (completedObjects.Contains(checksum))
            {
                return;
            }

            if (!activeObjects.Add(checksum))
            {
                throw new InvalidDataException($"工坊道具依赖形成循环：{objects[checksum].Name}");
            }

            var sourceObject = objects[checksum];
            var children = sourceObject.Children.ToList();
            foreach (var placement in sourceObject.WorkshopPlacements)
            {
                if (excludedPrefabNames?.Contains(placement.PrefabName) == true)
                {
                    matchedExclusions.Add(placement.PrefabName);
                    continue;
                }

                if (workshopDirectory is null)
                {
                    throw new InvalidDataException($"建筑依赖工坊道具 {placement.PrefabName}，请使用 --workshop 指定本地 CRP 目录");
                }

                var separator = placement.PrefabName.IndexOf('.');
                var id = placement.PrefabName[..separator];
                var prefabName = placement.PrefabName[(separator + 1)..];
                var dependencyPath = FindPrefabPackage(Path.Combine(workshopDirectory, id), prefabName);
                if (!loadedPackages.TryGetValue(dependencyPath, out var dependency))
                {
                    dependency = CrpPackageReader.Read(dependencyPath);
                    loadedPackages.Add(dependencyPath, dependency);
                    Merge(meshes, dependency.Meshes);
                    Merge(textures, dependency.Textures);
                    Merge(materials, dependency.Materials);
                    Merge(objects, dependency.GameObjects);
                }

                var prefabs = dependency.GameObjects.Values.Where(item =>
                    item.Name == prefabName && item.PrefabComponent is "PropInfo" or "TreeInfo").ToArray();
                if (prefabs.Length != 1)
                {
                    throw new InvalidDataException($"工坊引用 {placement.PrefabName} 在 {dependencyPath} 中匹配到 {prefabs.Length} 个道具或树木");
                }

                children.Add(new CrpChild(prefabs[0].AssetChecksum, placement.Position, placement.AngleDegrees));
            }

            objects[checksum] = sourceObject with { Children = children, WorkshopPlacements = [] };
            foreach (var child in children)
            {
                AddPlacements(child.ObjectChecksum);
            }

            activeObjects.Remove(checksum);
            completedObjects.Add(checksum);
        }
    }

    private static void Merge<T>(IDictionary<string, T> target, IReadOnlyDictionary<string, T> source)
    {
        foreach (var (checksum, value) in source)
        {
            // CRP checksum is the identity of the serialized content, shared across packages.
            target.TryAdd(checksum, value);
        }
    }

    private static string FindPrefabPackage(string directory, string prefabName)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"缺少工坊依赖目录：{directory}，引用对象：{prefabName}");
        }

        var matches = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.crp", SearchOption.TopDirectoryOnly))
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (new string(reader.ReadChars(4)) != "CRAP")
            {
                throw new InvalidDataException($"依赖不是 CRP 文件：{path}");
            }

            _ = reader.ReadUInt16();
            _ = reader.ReadString();
            _ = reader.ReadString();
            _ = reader.ReadUInt32();
            _ = reader.ReadString();
            var count = reader.ReadInt32();
            _ = reader.ReadInt64();
            var hasPrefab = false;
            for (var index = 0; index < count; index++)
            {
                var name = reader.ReadString();
                _ = reader.ReadString();
                var type = (CrpAssetType)reader.ReadInt32();
                _ = reader.ReadInt64();
                _ = reader.ReadInt64();
                hasPrefab |= type == CrpAssetType.Object && name == prefabName;
            }

            if (hasPrefab)
            {
                matches.Add(path);
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidDataException($"工坊依赖 {directory} 中对象 {prefabName} 匹配到 {matches.Count} 个 CRP 文件");
        }

        return matches[0];
    }
}
