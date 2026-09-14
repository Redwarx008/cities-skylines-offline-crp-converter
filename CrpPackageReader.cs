using System.Numerics;

namespace OfflineCrpConverter;

internal enum CrpAssetType
{
    Unknown = 0,
    Object = 1,
    Material = 2,
    Texture = 3,
    StaticMesh = 4,
    Text = 50,
    Assembly = 51,
    Data = 52,
    Package = 53,
    Locale = 80,
    User = 100,
    UserSaveGame = 101,
    UserCustomMap = 102,
    UserMetaData = 103,
    UserLut = 104,
    UserDistrictStyle = 105,
    UserMapTheme = 106,
    UserMapThemeMap = 107,
}

internal sealed record CrpAssetHeader(
    string Name,
    string Checksum,
    CrpAssetType Type,
    long RelativeOffset,
    long Size);

internal sealed record CrpTexture(bool ForceLinear, byte[] ImageBytes);

internal sealed record CrpMaterial(
    string ShaderName,
    IReadOnlyDictionary<string, Vector4> Colors,
    IReadOnlyDictionary<string, Vector4> Vectors,
    IReadOnlyDictionary<string, float> Floats,
    IReadOnlyDictionary<string, string> Textures);

internal sealed record CrpMesh(
    Vector3[] Positions,
    Vector4[] Colors,
    Vector2[] TextureCoordinates,
    Vector3[] Normals,
    Vector4[] Tangents,
    IReadOnlyList<int[]> SubMeshTriangles)
{
    public bool HasGeometry => Positions.Length != 0 &&
        SubMeshTriangles.Any(static triangles => triangles.Length != 0);
}

internal sealed record CrpTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static CrpTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

internal sealed record CrpChild(string ObjectChecksum, Vector3 Position, float AngleDegrees);

internal sealed record CrpGameObject(
    string Name,
    string AssetChecksum,
    CrpTransform Transform,
    string? MeshChecksum,
    IReadOnlyList<string> MaterialChecksums,
    IReadOnlyList<CrpChild> Children,
    string? PrefabComponent,
    IReadOnlyList<string> NetworkVariantNames,
    bool ApplyTransform);

internal sealed class CrpPackage
{
    public required string SourcePath { get; init; }
    public required string MainAssetName { get; init; }
    public required string MainObjectName { get; init; }
    public required string MainObjectChecksum { get; init; }
    public required IReadOnlyDictionary<string, CrpMesh> Meshes { get; init; }
    public required IReadOnlyDictionary<string, CrpTexture> Textures { get; init; }
    public required IReadOnlyDictionary<string, CrpMaterial> Materials { get; init; }
    public required IReadOnlyDictionary<string, CrpGameObject> GameObjects { get; init; }

    public int ReachableMeshCount => GetReachableRenderMeshes().Count;
    public int ReachablePrimitiveCount => GetReachableRenderMeshes()
        .Sum(item => Meshes[item.MeshChecksum!].SubMeshTriangles.Count(static triangles => triangles.Length != 0));
    public int ReachableMaterialCount => GetReachableObjects()
        .Where(static item => item.MeshChecksum is not null)
        .SelectMany(gameObject => Meshes[gameObject.MeshChecksum!].SubMeshTriangles
            .Select((triangles, index) => (triangles, index))
            .Where(static slot => slot.triangles.Length != 0)
            .Select(slot => gameObject.MaterialChecksums[slot.index]))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    private IReadOnlyList<CrpGameObject> GetReachableRenderMeshes()
        => GetReachableObjects()
            .Where(item => item.MeshChecksum is not null && Meshes[item.MeshChecksum].HasGeometry)
            .DistinctBy(
                static item => item.MeshChecksum + "\0" + string.Join('\0', item.MaterialChecksums),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<CrpGameObject> GetReachableObjects()
    {
        var result = new List<CrpGameObject>();
        var activePath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(MainObjectChecksum);
        return result;

        void Visit(string checksum)
        {
            if (!GameObjects.TryGetValue(checksum, out var gameObject))
            {
                throw new InvalidDataException($"复合建筑引用了不存在的 GameObject：{checksum}");
            }

            if (!activePath.Add(checksum))
            {
                throw new InvalidDataException($"复合建筑引用形成循环：{gameObject.Name} ({checksum})");
            }

            result.Add(gameObject);
            foreach (var child in gameObject.Children)
            {
                Visit(child.ObjectChecksum);
            }

            activePath.Remove(checksum);
        }
    }
}

internal static class CrpPackageReader
{
    private const string Magic = "CRAP";

    public static CrpPackage Read(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        var magic = new string(reader.ReadChars(4));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"不是 Cities: Skylines CRP 文件：{path}");
        }

        var formatVersion = reader.ReadUInt16();
        if (formatVersion is < 4 or > 10)
        {
            throw new InvalidDataException($"尚未验证的 CRP 格式版本：{formatVersion}");
        }

        _ = reader.ReadString(); // package name
        _ = reader.ReadString(); // encrypted author
        _ = reader.ReadUInt32(); // package version
        var mainAssetName = reader.ReadString();
        var assetCount = reader.ReadInt32();
        var contentStart = reader.ReadInt64();
        if (assetCount <= 0 || contentStart <= 0 || contentStart >= stream.Length)
        {
            throw new InvalidDataException("CRP 头部中的资产数量或内容偏移无效");
        }

        var headers = new CrpAssetHeader[assetCount];
        for (var index = 0; index < assetCount; index++)
        {
            var name = reader.ReadString();
            var checksum = reader.ReadString();
            var type = (CrpAssetType)reader.ReadInt32();
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            if (offset < 0 || size <= 0 || contentStart + offset + size > stream.Length)
            {
                throw new InvalidDataException($"资产范围越界：{name} ({checksum})");
            }

            headers[index] = new CrpAssetHeader(name, checksum, type, offset, size);
        }

        if (reader.BaseStream.Position != contentStart)
        {
            throw new InvalidDataException(
                $"CRP 头部长度与内容起点不一致：读取到 {reader.BaseStream.Position}，声明为 {contentStart}");
        }

        var meshes = new Dictionary<string, CrpMesh>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, CrpTexture>(StringComparer.OrdinalIgnoreCase);
        var materials = new Dictionary<string, CrpMaterial>(StringComparer.OrdinalIgnoreCase);
        var gameObjects = new Dictionary<string, CrpGameObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            stream.Position = contentStart + header.RelativeOffset;
            var assetEnd = stream.Position + header.Size;
            var isNull = reader.ReadBoolean();
            if (isNull)
            {
                continue;
            }

            var assemblyQualifiedName = reader.ReadString();
            var runtimeType = assemblyQualifiedName.Split(',')[0];
            var objectName = reader.ReadString();

            switch (runtimeType)
            {
                case "UnityEngine.Mesh":
                    AddUnique(meshes, header.Checksum, ReadMesh(reader, assetEnd), "网格");
                    break;
                case "UnityEngine.Texture2D":
                case "ColossalFramework.Importers.Image":
                    AddUnique(textures, header.Checksum, ReadTexture(reader), "贴图");
                    break;
                case "UnityEngine.Material":
                    AddUnique(materials, header.Checksum, ReadMaterial(reader), "材质");
                    break;
                case "UnityEngine.GameObject":
                    var generatedObjects = new List<CrpGameObject>();
                    AddUnique(
                        gameObjects,
                        header.Checksum,
                        ReadGameObject(
                            reader,
                            assetEnd,
                            objectName,
                            header.Checksum,
                            formatVersion,
                            generatedObjects),
                        "GameObject");
                    foreach (var generatedObject in generatedObjects)
                    {
                        AddUnique(
                            gameObjects,
                            generatedObject.AssetChecksum,
                            generatedObject,
                            "NetInfo 渲染对象");
                    }

                    break;
            }

            if (stream.Position > assetEnd)
            {
                throw new InvalidDataException($"资产解析越界：{header.Name} ({header.Checksum})");
            }
        }

        var mainObjects = gameObjects.Values
            .Where(static item => item.PrefabComponent is "BuildingInfo" or "PropInfo" or "VehicleInfo" or "NetInfo")
            .ToArray();
        var namedMainObjects = mainObjects
            .Where(static item => item.Name.EndsWith("_Data", StringComparison.Ordinal))
            .ToArray();
        var mainObject = mainObjects.Length == 1
            ? mainObjects[0]
            : namedMainObjects.Length == 1
                ? namedMainObjects[0]
                : null;
        if (mainObject is null)
        {
            throw new InvalidDataException(
                $"找不到唯一主 Prefab GameObject；候选：" +
                string.Join("、", mainObjects.Select(static item => item.Name)));
        }

        if (mainObject.PrefabComponent == "NetInfo" &&
            mainObject.MeshChecksum is null &&
            mainObject.Children.Count == 0)
        {
            mainObject = SelectNetworkRepresentative(mainObject, mainObjects, gameObjects, meshes);
        }

        var package = new CrpPackage
        {
            SourcePath = Path.GetFullPath(path),
            MainAssetName = mainAssetName,
            MainObjectName = mainObject.Name,
            MainObjectChecksum = mainObject.AssetChecksum,
            Meshes = meshes,
            Textures = textures,
            Materials = materials,
            GameObjects = gameObjects,
        };
        ValidateReferences(package);
        if (package.ReachableMeshCount == 0)
        {
            throw new InvalidDataException($"主模型 {package.MainObjectName} 没有可导出的几何");
        }

        return package;
    }

    private static CrpGameObject SelectNetworkRepresentative(
        CrpGameObject mainObject,
        IReadOnlyList<CrpGameObject> prefabObjects,
        IDictionary<string, CrpGameObject> gameObjects,
        IReadOnlyDictionary<string, CrpMesh> meshes)
    {
        foreach (var variantName in mainObject.NetworkVariantNames)
        {
            var variants = prefabObjects
                .Where(item =>
                    string.Equals(item.Name, variantName, StringComparison.Ordinal) ||
                    variantName.EndsWith('.' + item.Name, StringComparison.Ordinal))
                .ToArray();
            if (variants.Length != 1)
            {
                continue;
            }

            foreach (var child in variants[0].Children)
            {
                var renderObject = gameObjects[child.ObjectChecksum];
                if (renderObject.Name.Contains("路段", StringComparison.Ordinal) &&
                    renderObject.MeshChecksum is not null)
                {
                    var representative = renderObject with
                    {
                        Transform = GetNetworkSegmentTransform(meshes[renderObject.MeshChecksum]),
                        ApplyTransform = true,
                    };
                    gameObjects[child.ObjectChecksum] = representative;
                    return representative;
                }
            }
        }

        var renderableVariants = prefabObjects
            .Where(item => item.PrefabComponent == "NetInfo" && !ReferenceEquals(item, mainObject))
            .Where(item => item.Children.Any(child =>
                gameObjects[child.ObjectChecksum].Name.Contains("路段", StringComparison.Ordinal) &&
                gameObjects[child.ObjectChecksum].MeshChecksum is not null))
            .ToArray();
        var isBridge = mainObject.Name.Contains("Bridge", StringComparison.OrdinalIgnoreCase) ||
            mainObject.Name.Contains('桥', StringComparison.Ordinal) ||
            mainObject.Name.Contains('橋', StringComparison.Ordinal);
        var preferredVariantIndex = isBridge && renderableVariants.Length > 1 ? 1 : 0;
        if (renderableVariants.Length > preferredVariantIndex)
        {
            var variant = renderableVariants[preferredVariantIndex];
            foreach (var child in variant.Children)
            {
                var renderObject = gameObjects[child.ObjectChecksum];
                if (renderObject.Name.Contains("路段", StringComparison.Ordinal) &&
                    renderObject.MeshChecksum is not null)
                {
                    var representative = renderObject with
                    {
                        Transform = GetNetworkSegmentTransform(meshes[renderObject.MeshChecksum]),
                        ApplyTransform = true,
                    };
                    gameObjects[child.ObjectChecksum] = representative;
                    return representative;
                }
            }
        }

        throw new InvalidDataException(
            $"主网络 {mainObject.Name} 没有直接几何，也无法从其网络变体中确定代表性路段；" +
            $"AI 引用：{string.Join("、", mainObject.NetworkVariantNames)}");
    }

    private static CrpTransform GetNetworkSegmentTransform(CrpMesh mesh)
    {
        var yExtent = mesh.Positions.Max(static value => value.Y) - mesh.Positions.Min(static value => value.Y);
        var zExtent = mesh.Positions.Max(static value => value.Z) - mesh.Positions.Min(static value => value.Z);
        if (yExtent <= zExtent)
        {
            return CrpTransform.Identity;
        }

        var sourceAxisRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI * 0.5f);
        return CrpTransform.Identity with { Rotation = sourceAxisRotation };
    }

    private static void ValidateReferences(CrpPackage package)
    {
        foreach (var gameObject in package.GetReachableObjects())
        {
            if (gameObject.MeshChecksum is null)
            {
                if (gameObject.MaterialChecksums.Count != 0)
                {
                    throw new InvalidDataException($"{gameObject.Name} 有材质但没有 MeshFilter");
                }

                continue;
            }

            if (!package.Meshes.TryGetValue(gameObject.MeshChecksum, out var mesh))
            {
                throw new InvalidDataException(
                    $"{gameObject.Name} 引用了不存在的网格：{gameObject.MeshChecksum}");
            }

            if (mesh.SubMeshTriangles.Count > gameObject.MaterialChecksums.Count)
            {
                throw new InvalidDataException(
                    $"{gameObject.Name} 的子网格数 {mesh.SubMeshTriangles.Count} 与材质槽数 " +
                    $"{gameObject.MaterialChecksums.Count} 不匹配：存在没有材质的子网格");
            }

            // BuildingInfo initializes from Renderer.sharedMaterial. Extra renderer slots beyond
            // the mesh submesh count are not independent geometry and must not be overlaid.
            foreach (var materialChecksum in mesh.SubMeshTriangles
                .Select((triangles, index) => (triangles, index))
                .Where(static item => item.triangles.Length != 0)
                .Select(item => gameObject.MaterialChecksums[item.index]))
            {
                if (!package.Materials.TryGetValue(materialChecksum, out var material))
                {
                    throw new InvalidDataException(
                        $"{gameObject.Name} 引用了不存在的材质：{materialChecksum}");
                }

                foreach (var (slot, textureChecksum) in material.Textures)
                {
                    if (string.IsNullOrEmpty(textureChecksum))
                    {
                        continue;
                    }

                    if (!package.Textures.ContainsKey(textureChecksum))
                    {
                        throw new InvalidDataException(
                            $"材质 {materialChecksum} 的 {slot} 引用了不存在的贴图：{textureChecksum}");
                    }
                }
            }
        }
    }

    private static CrpMesh ReadMesh(BinaryReader reader, long assetEnd)
    {
        var positions = ReadArray(reader, static input => ReadVector3(input));
        var colors = ReadArray(reader, static input => ReadVector4(input));
        var textureCoordinates = ReadArray(reader, static input => ReadVector2(input));
        var normals = ReadArray(reader, static input => ReadVector3(input));
        var tangents = ReadArray(reader, static input => ReadVector4(input));
        _ = ReadArray(reader, static input =>
        {
            for (var index = 0; index < 4; index++)
            {
                _ = input.ReadInt32();
                _ = input.ReadSingle();
            }

            return 0;
        });
        _ = ReadArray(reader, static input =>
        {
            for (var index = 0; index < 16; index++)
            {
                _ = input.ReadSingle();
            }

            return 0;
        });

        var subMeshCount = reader.ReadInt32();
        if (subMeshCount <= 0 || subMeshCount > 1024)
        {
            throw new InvalidDataException($"无效的子网格数量：{subMeshCount}");
        }

        var triangles = new int[subMeshCount][];
        for (var index = 0; index < subMeshCount; index++)
        {
            triangles[index] = ReadArray(reader, static input => input.ReadInt32());
            if (triangles[index].Length % 3 != 0)
            {
                throw new InvalidDataException($"子网格 {index} 的索引数量不是三角形列表");
            }
        }

        var hasAnyIndices = triangles.Any(static item => item.Length != 0);
        if (positions.Length == 0)
        {
            if (normals.Length != 0 || textureCoordinates.Length != 0 || hasAnyIndices)
            {
                throw new InvalidDataException(
                    $"空网格含不一致数据：位置={positions.Length}，法线={normals.Length}，UV={textureCoordinates.Length}");
            }
        }
        else if (normals.Length != positions.Length || textureCoordinates.Length != positions.Length)
        {
            throw new InvalidDataException(
                $"网格顶点属性数量不一致：位置={positions.Length}，法线={normals.Length}，UV={textureCoordinates.Length}");
        }

        foreach (var index in triangles.SelectMany(static item => item))
        {
            if ((uint)index >= (uint)positions.Length)
            {
                throw new InvalidDataException($"网格索引 {index} 超出顶点范围 {positions.Length}");
            }
        }

        // Unity 的 Mesh 序列化尾部在不同游戏补丁间可能增加非几何字段；资产边界由 CRP 头部明确给出。
        reader.BaseStream.Position = assetEnd;
        return new CrpMesh(positions, colors, textureCoordinates, normals, tangents, triangles);
    }

    private static CrpTexture ReadTexture(BinaryReader reader)
    {
        var forceLinear = reader.ReadBoolean();
        var imageLength = reader.ReadUInt32();
        if (imageLength <= 8)
        {
            imageLength = reader.ReadUInt32();
        }

        if (imageLength < 8 || imageLength > int.MaxValue)
        {
            throw new InvalidDataException($"无效的 DDS 数据长度：{imageLength}");
        }

        var bytes = reader.ReadBytes((int)imageLength);
        if (bytes.Length != imageLength || !HasSupportedImageSignature(bytes))
        {
            throw new InvalidDataException("CRP Texture2D 不包含受支持的 DDS、PNG 或 JPEG 数据");
        }

        return new CrpTexture(forceLinear, bytes);
    }

    private static bool HasSupportedImageSignature(ReadOnlySpan<byte> bytes)
        => bytes.StartsWith("DDS "u8) ||
            bytes.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }) ||
            bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF });

    private static CrpMaterial ReadMaterial(BinaryReader reader)
    {
        var shaderName = reader.ReadString();
        var propertyCount = reader.ReadInt32();
        if (propertyCount < 0 || propertyCount > 4096)
        {
            throw new InvalidDataException($"无效的材质属性数量：{propertyCount}");
        }

        var colors = new Dictionary<string, Vector4>(StringComparer.Ordinal);
        var vectors = new Dictionary<string, Vector4>(StringComparer.Ordinal);
        var floats = new Dictionary<string, float>(StringComparer.Ordinal);
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < propertyCount; index++)
        {
            var propertyType = reader.ReadInt32();
            var propertyName = reader.ReadString();
            switch (propertyType)
            {
                case 0:
                    colors.Add(propertyName, ReadVector4(reader));
                    break;
                case 1:
                    vectors.Add(propertyName, ReadVector4(reader));
                    break;
                case 2:
                    floats.Add(propertyName, reader.ReadSingle());
                    break;
                case 3:
                    textures.Add(propertyName, reader.ReadBoolean() ? string.Empty : reader.ReadString());
                    break;
                default:
                    throw new InvalidDataException($"材质 {shaderName} 含未知属性类型：{propertyType}");
            }
        }

        return new CrpMaterial(shaderName, colors, vectors, floats, textures);
    }

    private static CrpGameObject ReadGameObject(
        BinaryReader reader,
        long assetEnd,
        string objectName,
        string assetChecksum,
        ushort formatVersion,
        ICollection<CrpGameObject> generatedObjects)
    {
        _ = reader.ReadString(); // tag
        _ = reader.ReadInt32(); // layer
        _ = reader.ReadBoolean(); // enabled
        var propertyCount = reader.ReadInt32();
        var transform = CrpTransform.Identity;
        string? meshChecksum = null;
        IReadOnlyList<string> materialChecksums = [];
        var children = new List<CrpChild>();
        var networkVariantNames = new List<string>();
        string? prefabComponent = null;

        for (var index = 0; index < propertyCount; index++)
        {
            if (reader.ReadBoolean())
            {
                continue;
            }

            var runtimeType = reader.ReadString().Split(',')[0];
            switch (runtimeType)
            {
                case "UnityEngine.Transform":
                    transform = new CrpTransform(
                        ReadVector3(reader),
                        new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        ReadVector3(reader));
                    break;
                case "UnityEngine.MeshFilter":
                    meshChecksum = reader.ReadString();
                    break;
                case "UnityEngine.MeshRenderer":
                    materialChecksums = ReadArray(reader, static input => input.ReadString());
                    break;
                case "BuildingInfo":
                case "BuildingInfoSub":
                    prefabComponent = runtimeType;
                    var info = ReadPropertyBag(reader, formatVersion);
                    children.AddRange(ReadChildren(info, "m_subMeshes", "m_subInfo", includeFixedHeight: false));
                    children.AddRange(ReadChildren(info, "m_subBuildings", "m_buildingInfo", includeFixedHeight: true));
                    break;
                case "PropInfo":
                case "VehicleInfo":
                    prefabComponent = runtimeType;
                    _ = ReadPropertyBag(reader, formatVersion);
                    break;
                case "NetInfo":
                    prefabComponent = runtimeType;
                    var netInfo = ReadPropertyBag(reader, formatVersion, "m_nodes");
                    children.AddRange(CreateNetworkRenderObjects(
                        netInfo,
                        "m_segments",
                        "路段",
                        objectName,
                        assetChecksum,
                        generatedObjects));
                    children.AddRange(CreateNetworkRenderObjects(
                        netInfo,
                        "m_nodes",
                        "节点",
                        objectName,
                        assetChecksum,
                        generatedObjects));
                    // NetInfo 的几何引用到 m_nodes 即结束；其后是 PrefabInfo 的界面与缩略图字段。
                    // 它们不参与离线几何导出，且具体尾部随游戏补丁变化，以 CRP 资产边界跳过。
                    index = propertyCount;
                    break;
                case var aiType when aiType.EndsWith("AI", StringComparison.Ordinal):
                    var aiProperties = ReadPropertyBag(reader, formatVersion);
                    foreach (var variantName in aiProperties.Values.OfType<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(variantName))
                        {
                            networkVariantNames.Add(variantName);
                        }
                    }

                    break;
                default:
                    _ = ReadValue(reader, runtimeType, formatVersion);
                    break;
            }
        }

        if (reader.BaseStream.Position > assetEnd)
        {
            throw new InvalidDataException(
                $"GameObject {objectName} 解析越界：位置={reader.BaseStream.Position}，结尾={assetEnd}");
        }

        // BuildingInfoSub serializes inherited BuildingInfoBase presentation fields after the
        // geometry-bearing component fields. They contain no mesh/material/child references;
        // the CRP asset boundary is the authoritative end of that opaque base-class tail.
        reader.BaseStream.Position = assetEnd;

        return new CrpGameObject(
            objectName,
            assetChecksum,
            transform,
            string.IsNullOrEmpty(meshChecksum) ? null : meshChecksum,
            materialChecksums,
            children,
            prefabComponent,
            networkVariantNames,
            false);
    }

    private static IEnumerable<CrpChild> CreateNetworkRenderObjects(
        IReadOnlyDictionary<string, object?> properties,
        string propertyName,
        string displayName,
        string parentName,
        string parentChecksum,
        ICollection<CrpGameObject> generatedObjects)
    {
        if (!properties.TryGetValue(propertyName, out var value) || value is not object?[] entries)
        {
            yield break;
        }

        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index] is not IReadOnlyDictionary<string, object?> fields ||
                fields["m_mesh"] is not string meshChecksum ||
                fields["m_material"] is not string materialChecksum)
            {
                throw new InvalidDataException($"{propertyName} 条目结构不完整");
            }

            if (string.IsNullOrEmpty(meshChecksum) || string.IsNullOrEmpty(materialChecksum))
            {
                continue;
            }

            var checksum = $"{parentChecksum}:net:{propertyName}:{index}";
            generatedObjects.Add(new CrpGameObject(
                $"{parentName} {displayName} {index + 1}",
                checksum,
                CrpTransform.Identity,
                meshChecksum,
                [materialChecksum],
                [],
                null,
                [],
                false));
            yield return new CrpChild(checksum, Vector3.Zero, 0f);
        }
    }

    private static IEnumerable<CrpChild> ReadChildren(
        IReadOnlyDictionary<string, object?> properties,
        string propertyName,
        string checksumName,
        bool includeFixedHeight)
    {
        if (!properties.TryGetValue(propertyName, out var value) || value is not object?[] entries)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry is not IReadOnlyDictionary<string, object?> fields ||
                fields[checksumName] is not string checksum ||
                fields["m_position"] is not Vector3 position ||
                fields["m_angle"] is not float angle)
            {
                throw new InvalidDataException($"{propertyName} 条目结构不完整");
            }

            if (includeFixedHeight && fields["m_fixedHeight"] is not bool)
            {
                throw new InvalidDataException($"{propertyName} 条目缺少 m_fixedHeight");
            }

            yield return new CrpChild(checksum, position, angle);
        }
    }

    private static Dictionary<string, object?> ReadPropertyBag(
        BinaryReader reader,
        ushort formatVersion,
        string? lastPropertyName = null)
    {
        var propertyCount = reader.ReadInt32();
        if (propertyCount < 0 || propertyCount > 16384)
        {
            throw new InvalidDataException($"无效的对象属性数量：{propertyCount}");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < propertyCount; index++)
        {
            if (reader.ReadBoolean())
            {
                continue;
            }

            var runtimeType = reader.ReadString().Split(',')[0];
            var propertyName = reader.ReadString();
            try
            {
                values.Add(propertyName, ReadValue(reader, runtimeType, formatVersion));
                if (string.Equals(propertyName, lastPropertyName, StringComparison.Ordinal))
                {
                    return values;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                throw new InvalidDataException(
                    $"属性 {propertyName} ({runtimeType}) 解析失败，流位置={reader.BaseStream.Position}: " +
                    exception.Message,
                    exception);
            }
        }

        if (lastPropertyName is not null)
        {
            throw new InvalidDataException($"对象缺少必要属性：{lastPropertyName}");
        }

        return values;
    }

    private static object? ReadValue(BinaryReader reader, string runtimeType, ushort formatVersion)
    {
        if (runtimeType.EndsWith("[]", StringComparison.Ordinal))
        {
            var elementType = runtimeType[..^2];
            var count = reader.ReadInt32();
            if (count < 0 || count > 10_000_000)
            {
                throw new InvalidDataException($"无效的数组长度：{runtimeType}={count}");
            }

            if (count == 0)
            {
                return Array.Empty<object?>();
            }

            var values = new object?[count];
            for (var index = 0; index < count; index++)
            {
                values[index] = ReadValue(reader, elementType, formatVersion);
            }

            return values;
        }

        return runtimeType switch
        {
            "System.String" => reader.ReadString(),
            "System.Single" => reader.ReadSingle(),
            "System.Int32" => reader.ReadInt32(),
            "System.Boolean" => reader.ReadBoolean(),
            "System.DateTime" => reader.ReadString(),
            "UnityEngine.Vector2" => ReadVector2(reader),
            "UnityEngine.Vector3" => ReadVector3(reader),
            "UnityEngine.Vector4" or "UnityEngine.Color" => ReadVector4(reader),
            "UnityEngine.Matrix4x4" => ReadMatrix(reader),
            "UnityEngine.BoneWeight" => ReadBoneWeight(reader),
            "UnityEngine.GameObject" => ReadGameObjectReference(reader),
            "ColossalFramework.Packaging.Package+Asset" => reader.ReadString(),
            "CombinedMilestone" => reader.ReadString(),
            "BuildingInfoGen" or "PropInfoGen" or "TreeInfoGen" or "VehicleInfoGen" => reader.ReadString(),
            "ItemClass" => new Dictionary<string, object?> { ["m_itemClass"] = reader.ReadString() },
            "BuildingInfo+MeshInfo" => ReadBuildingMeshInfo(reader, formatVersion),
            "BuildingInfo+SubInfo" => ReadBuildingSubInfo(reader),
            "BuildingInfo+Prop" => ReadBuildingProp(reader),
            "BuildingInfo+PathInfo" => ReadBuildingPath(reader, formatVersion),
            "NetInfo+Lane" => ReadNetLane(reader, formatVersion),
            "NetLaneProps" => ReadNetLaneProps(reader, formatVersion),
            "NetLaneProps+Prop" => ReadNetLaneProp(reader, formatVersion),
            "NetInfo+Segment" => ReadNetSegment(reader, formatVersion),
            "NetInfo+Node" => ReadNetNode(reader, formatVersion),
            "PropInfo+Variation" => ReadPropVariation(reader),
            "PropInfo+ParkingSpace" => ReadPropParking(reader),
            "PropInfo+Effect" => ReadPropEffect(reader),
            "DepotAI+SpawnPoint" => ReadDepotSpawnPoint(reader),
            "PropInfo+SpecialPlace" => ReadPropSpecialPlace(reader),
            "TransportInfo" => new Dictionary<string, object?> { ["m_transportInfo"] = reader.ReadString() },
            "NetInfo" => reader.ReadString(),
            "MessageInfo" => ReadMessageInfo(reader),
            "VehicleInfo+MeshInfo" => ReadVehicleMeshInfo(reader),
            "VehicleInfo+Effect" => ReadVehicleEffect(reader),
            "VehicleInfo+VehicleDoor" => ReadVehicleDoor(reader),
            "VehicleInfo+VehicleTrailer" => ReadVehicleTrailer(reader),
            "BuildingInfo" or "BuildingInfoSub" or "VehicleInfo" or "PropInfo" =>
                ReadPropertyBag(reader, formatVersion),
            _ when IsSerializedInt(runtimeType) => reader.ReadInt32(),
            _ when runtimeType.EndsWith("AI", StringComparison.Ordinal) => ReadPropertyBag(reader, formatVersion),
            _ => throw new InvalidDataException($"尚未支持的 CRP 序列化类型：{runtimeType}"),
        };
    }

    private static bool IsSerializedInt(string runtimeType)
        => runtimeType is
            "CustomAssetMetaData" or "CustomAssetMetaData+Type" or
            "ItemClass+Placement" or "ItemClass+Availability" or "ItemClass+Level" or
            "ItemClass+Service" or "ItemClass+SubService" or
            "NetInfo+ConnectGroup" or
            "BuildingInfo+PlacementMode" or "BuildingInfo+ZoningMode" or
            "TerrainModify+Surface" or "PropInfo+DoorType" or
            "VehicleParked+Flags" or "Vehicle+Flags" or "Building+Flags" or "CitizenInstance+Flags" or
            "VehicleInfo+VehicleType" or "SteamHelper+DLC_BitMask" or
            "SteamHelper+ExpansionBitMask" or "SteamHelper+ModderPackBitMask" or
            "SteamHelper+RadioBitMask";

    private static Dictionary<string, object?> ReadBuildingMeshInfo(BinaryReader reader, ushort formatVersion)
    {
        var result = new Dictionary<string, object?>
        {
            ["m_subInfo"] = reader.ReadString(),
            ["m_flagsForbidden"] = reader.ReadInt32(),
            ["m_flagsRequired"] = reader.ReadInt32(),
        };
        if (formatVersion >= 9)
        {
            result["m_flagsCompatibility"] = reader.ReadInt32();
        }

        result["m_position"] = ReadVector3(reader);
        result["m_angle"] = reader.ReadSingle();
        return result;
    }

    private static Dictionary<string, object?> ReadNetSegment(BinaryReader reader, ushort formatVersion)
    {
        var result = new Dictionary<string, object?>
        {
            ["m_mesh"] = reader.ReadString(),
            ["m_material"] = reader.ReadString(),
            ["m_lodMesh"] = reader.ReadString(),
            ["m_lodMaterial"] = reader.ReadString(),
            ["m_forwardRequired"] = reader.ReadInt32(),
            ["m_forwardForbidden"] = reader.ReadInt32(),
            ["m_backwardRequired"] = reader.ReadInt32(),
            ["m_backwardForbidden"] = reader.ReadInt32(),
        };

        if (formatVersion >= 11)
        {
            result["m_forwardRequired2"] = reader.ReadInt32();
            result["m_forwardForbidden2"] = reader.ReadInt32();
            result["m_backwardRequired2"] = reader.ReadInt32();
            result["m_backwardForbidden2"] = reader.ReadInt32();
        }

        result["m_emptyTransparent"] = reader.ReadBoolean();
        result["m_disableBendNodes"] = reader.ReadBoolean();
        return result;
    }

    private static Dictionary<string, object?> ReadNetNode(BinaryReader reader, ushort formatVersion)
    {
        var result = new Dictionary<string, object?>
        {
            ["m_mesh"] = reader.ReadString(),
            ["m_material"] = reader.ReadString(),
            ["m_lodMesh"] = reader.ReadString(),
            ["m_lodMaterial"] = reader.ReadString(),
            ["m_flagsRequired"] = reader.ReadInt32(),
            ["m_flagsForbidden"] = reader.ReadInt32(),
            ["m_connectGroup"] = reader.ReadInt32(),
            ["m_directConnect"] = reader.ReadBoolean(),
            ["m_emptyTransparent"] = reader.ReadBoolean(),
        };

        if (formatVersion >= 9)
        {
            result["m_flagsRequired2"] = reader.ReadInt32();
            result["m_flagsForbidden2"] = reader.ReadInt32();
            result["m_tagsRequired"] = ReadArray(reader, static input => input.ReadString());
            result["m_tagsForbidden"] = ReadArray(reader, static input => input.ReadString());
            result["m_forbidAnyTags"] = reader.ReadBoolean();
            result["m_minSameTags"] = reader.ReadByte();
            result["m_maxSameTags"] = reader.ReadByte();
            result["m_minOtherTags"] = reader.ReadByte();
            result["m_maxOtherTags"] = reader.ReadByte();
        }

        return result;
    }

    private static Dictionary<string, object?> ReadNetLane(BinaryReader reader, ushort formatVersion)
    {
        var result = new Dictionary<string, object?>
        {
            ["m_position"] = reader.ReadSingle(),
            ["m_width"] = reader.ReadSingle(),
            ["m_verticalOffset"] = reader.ReadSingle(),
            ["m_stopOffset"] = reader.ReadSingle(),
            ["m_speedLimit"] = reader.ReadSingle(),
            ["m_direction"] = reader.ReadInt32(),
            ["m_laneType"] = reader.ReadInt32(),
            ["m_vehicleType"] = reader.ReadInt32(),
            ["m_stopType"] = reader.ReadInt32(),
            ["m_laneProps"] = ReadNetLaneProps(reader, formatVersion),
            ["m_allowConnect"] = reader.ReadBoolean(),
            ["m_useTerrainHeight"] = reader.ReadBoolean(),
            ["m_centerPlatform"] = reader.ReadBoolean(),
            ["m_elevated"] = reader.ReadBoolean(),
        };

        if (formatVersion >= 9)
        {
            result["m_vehicleCategoryPart1"] = reader.ReadInt32();
            result["m_vehicleCategoryPart2"] = reader.ReadInt32();
        }

        return result;
    }

    private static Dictionary<string, object?> ReadNetLaneProps(BinaryReader reader, ushort formatVersion)
        => new()
        {
            ["m_props"] = ReadArray(reader, input => ReadNetLaneProp(input, formatVersion)),
        };

    private static Dictionary<string, object?> ReadNetLaneProp(BinaryReader reader, ushort formatVersion)
    {
        var result = new Dictionary<string, object?>
        {
            ["m_flagsRequired"] = reader.ReadInt32(),
            ["m_flagsForbidden"] = reader.ReadInt32(),
            ["m_startFlagsRequired"] = reader.ReadInt32(),
            ["m_startFlagsForbidden"] = reader.ReadInt32(),
            ["m_endFlagsRequired"] = reader.ReadInt32(),
            ["m_endFlagsForbidden"] = reader.ReadInt32(),
            ["m_colorMode"] = reader.ReadInt32(),
            ["m_prop"] = reader.ReadString(),
            ["m_tree"] = reader.ReadString(),
            ["m_position"] = ReadVector3(reader),
            ["m_angle"] = reader.ReadSingle(),
            ["m_segmentOffset"] = reader.ReadSingle(),
            ["m_repeatDistance"] = reader.ReadSingle(),
            ["m_minLength"] = reader.ReadSingle(),
            ["m_cornerAngle"] = reader.ReadSingle(),
            ["m_probability"] = reader.ReadInt32(),
        };

        if (formatVersion >= 8)
        {
            result["m_upgradable"] = reader.ReadBoolean();
        }

        if (formatVersion >= 9)
        {
            result["m_startFlagsRequired2"] = reader.ReadInt32();
            result["m_startFlagsForbidden2"] = reader.ReadInt32();
            result["m_endFlagsRequired2"] = reader.ReadInt32();
            result["m_endFlagsForbidden2"] = reader.ReadInt32();
        }

        return result;
    }

    private static Dictionary<string, object?> ReadBuildingSubInfo(BinaryReader reader)
        => new()
        {
            ["m_buildingInfo"] = reader.ReadString(),
            ["m_position"] = ReadVector3(reader),
            ["m_angle"] = reader.ReadSingle(),
            ["m_fixedHeight"] = reader.ReadBoolean(),
        };

    private static Dictionary<string, object?> ReadBuildingProp(BinaryReader reader)
        => new()
        {
            ["m_prop"] = reader.ReadString(),
            ["m_tree"] = reader.ReadString(),
            ["m_position"] = ReadVector3(reader),
            ["m_angle"] = reader.ReadSingle(),
            ["m_probability"] = reader.ReadInt32(),
            ["m_fixedHeight"] = reader.ReadBoolean(),
        };

    private static Dictionary<string, object?> ReadBuildingPath(BinaryReader reader, ushort formatVersion)
    {
        var result = new Dictionary<string, object?>
        {
            ["m_netInfo"] = reader.ReadString(),
            ["m_nodes"] = ReadValue(reader, "UnityEngine.Vector3[]", formatVersion),
            ["m_curveTargets"] = ReadValue(reader, "UnityEngine.Vector3[]", formatVersion),
            ["m_invertSegments"] = reader.ReadBoolean(),
            ["m_maxSnapDistance"] = reader.ReadSingle(),
        };
        if (formatVersion >= 5)
        {
            result["m_forbidLaneConnection"] = ReadArray(reader, static input => input.ReadBoolean());
            result["m_trafficLights"] = ReadArray(reader, static input => input.ReadInt32());
            result["m_yieldSigns"] = ReadArray(reader, static input => input.ReadBoolean());
        }

        if (formatVersion >= 11)
        {
            result["m_isInvertible"] = reader.ReadBoolean();
            result["m_isUpgradeable"] = reader.ReadBoolean();
        }

        return result;
    }

    private static Dictionary<string, object?> ReadPropVariation(BinaryReader reader)
        => new() { ["m_prop"] = reader.ReadString(), ["m_probability"] = reader.ReadInt32() };

    private static Dictionary<string, object?> ReadPropParking(BinaryReader reader)
        => new()
        {
            ["m_position"] = ReadVector3(reader),
            ["m_angle"] = ReadVector3(reader),
            ["m_size"] = ReadVector3(reader),
        };

    private static Dictionary<string, object?> ReadPropEffect(BinaryReader reader)
        => new()
        {
            ["m_effect"] = reader.ReadString(),
            ["m_position"] = ReadVector3(reader),
            ["m_direction"] = ReadVector3(reader),
        };

    private static Dictionary<string, object?> ReadDepotSpawnPoint(BinaryReader reader)
        => new() { ["m_position"] = ReadVector3(reader), ["m_target"] = ReadVector3(reader) };

    private static Dictionary<string, object?> ReadPropSpecialPlace(BinaryReader reader)
        => new()
        {
            ["m_specialFlags"] = reader.ReadInt32(),
            ["m_position"] = ReadVector3(reader),
            ["m_direction"] = ReadVector3(reader),
        };

    private static Dictionary<string, object?> ReadMessageInfo(BinaryReader reader)
        => new()
        {
            ["firstId1"] = reader.ReadString(),
            ["firstId2"] = reader.ReadString(),
            ["repeatId1"] = reader.ReadString(),
            ["repeatId2"] = reader.ReadString(),
        };

    private static Dictionary<string, object?> ReadVehicleMeshInfo(BinaryReader reader)
        => new()
        {
            ["checksum"] = reader.ReadString(),
            ["m_parkedFlagsForbidden"] = reader.ReadInt32(),
            ["m_parkedFlagsRequired"] = reader.ReadInt32(),
            ["m_vehicleFlagsForbidden"] = reader.ReadInt32(),
            ["m_vehicleFlagsRequired"] = reader.ReadInt32(),
        };

    private static Dictionary<string, object?> ReadVehicleEffect(BinaryReader reader)
        => new()
        {
            ["m_effect"] = reader.ReadString(),
            ["m_parkedFlagsForbidden"] = reader.ReadInt32(),
            ["m_parkedFlagsRequired"] = reader.ReadInt32(),
            ["m_vehicleFlagsForbidden"] = reader.ReadInt32(),
            ["m_vehicleFlagsRequired"] = reader.ReadInt32(),
        };

    private static Dictionary<string, object?> ReadVehicleDoor(BinaryReader reader)
        => new() { ["m_name"] = reader.ReadString(), ["m_position"] = ReadVector3(reader) };

    private static Dictionary<string, object?> ReadVehicleTrailer(BinaryReader reader)
        => new()
        {
            ["m_type"] = reader.ReadInt32(),
            ["m_probability"] = reader.ReadInt32(),
            ["m_invertprobability"] = reader.ReadInt32(),
        };

    private static object ReadGameObjectReference(BinaryReader reader)
        => new Dictionary<string, object?>
        {
            ["tag"] = reader.ReadString(),
            ["enabled"] = reader.ReadBoolean(),
        };

    private static object ReadMatrix(BinaryReader reader)
    {
        var values = new float[16];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = reader.ReadSingle();
        }

        return values;
    }

    private static object ReadBoneWeight(BinaryReader reader)
    {
        var values = new object[8];
        for (var index = 0; index < 4; index++)
        {
            values[index * 2] = reader.ReadInt32();
            values[index * 2 + 1] = reader.ReadSingle();
        }

        return values;
    }

    private static T[] ReadArray<T>(BinaryReader reader, Func<BinaryReader, T> readElement)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000)
        {
            throw new InvalidDataException($"无效的数组长度：{count}");
        }

        var values = new T[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = readElement(reader);
        }

        return values;
    }

    private static Vector2 ReadVector2(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());
    private static Vector3 ReadVector3(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static Vector4 ReadVector4(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void AddUnique<T>(IDictionary<string, T> dictionary, string checksum, T value, string label)
    {
        if (!dictionary.TryAdd(checksum, value))
        {
            throw new InvalidDataException($"{label} checksum 重复：{checksum}");
        }
    }
}
