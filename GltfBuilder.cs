using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Pfim;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace OfflineCrpConverter;

internal enum CutoutTextureChannel
{
    Red,
    Alpha,
}

internal sealed record TreeColorCorrection(float ExposureStops, float Saturation);

internal static class GltfBuilder
{
    public static byte[] Build(CrpPackage package, string assetName, TreeColorCorrection? treeColorCorrection = null,
        bool preserveTreeColors = false, Action<string>? reportTreeColor = null,
        IReadOnlyDictionary<string, TreeBaseColorOverride>? treeColorOverrides = null)
        => new Builder(GeneratedBuildingBaseRemover.Apply(package, reportTreeColor),
            treeColorCorrection, preserveTreeColors, reportTreeColor, treeColorOverrides).Build(assetName);

    private sealed class Builder(CrpPackage package, TreeColorCorrection? treeColorCorrection,
        bool preserveTreeColors, Action<string>? reportTreeColor,
        IReadOnlyDictionary<string, TreeBaseColorOverride>? treeColorOverrides)
    {
        private readonly BinaryBuffer _binary = new();
        private readonly List<Dictionary<string, object?>> _bufferViews = [];
        private readonly List<Dictionary<string, object?>> _accessors = [];
        private readonly List<Dictionary<string, object?>> _images = [];
        private readonly List<Dictionary<string, object?>> _textures = [];
        private readonly List<Dictionary<string, object?>> _materials = [];
        private readonly List<Dictionary<string, object?>> _meshes = [];
        private readonly List<Dictionary<string, object?>> _nodes = [];
        private readonly Dictionary<string, int> _materialIndices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _meshIndices = new(StringComparer.OrdinalIgnoreCase);
        private bool _usesSpecular;

        public byte[] Build(string assetName)
        {
            var rootNode = AddObjectNode(package.MainObjectChecksum);
            var document = new Dictionary<string, object?>
            {
                ["asset"] = new Dictionary<string, object?>
                {
                    ["version"] = "2.0",
                    ["generator"] = "OfflineCrpConverter",
                },
                ["scene"] = 0,
                ["scenes"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = assetName,
                        ["nodes"] = new[] { rootNode },
                    },
                },
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
                ["materials"] = _materials,
                ["textures"] = _textures,
                ["images"] = _images,
                ["samplers"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["magFilter"] = 9729,
                        ["minFilter"] = 9987,
                        ["wrapS"] = 10497,
                        ["wrapT"] = 10497,
                    },
                },
                ["accessors"] = _accessors,
                ["bufferViews"] = _bufferViews,
                ["buffers"] = new[]
                {
                    new Dictionary<string, object?> { ["byteLength"] = _binary.Length },
                },
            };

            if (_usesSpecular)
            {
                document["extensionsUsed"] = new[] { "KHR_materials_specular" };
            }
            var json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            return WriteGlb(json, _binary.ToArray());
        }

        private int AddObjectNode(string checksum)
        {
            var gameObject = package.GameObjects[checksum];
            var node = new Dictionary<string, object?> { ["name"] = gameObject.Name };
            if (gameObject.ApplyTransform)
            {
                AddTransform(node, gameObject.Transform);
            }
            if (gameObject.MeshChecksum is not null && package.Meshes[gameObject.MeshChecksum].HasGeometry)
            {
                node["mesh"] = GetMeshIndex(gameObject);
            }

            var nodeIndex = _nodes.Count;
            _nodes.Add(node);

            if (gameObject.Children.Count == 0)
            {
                return nodeIndex;
            }

            var childNodeIndices = new int[gameObject.Children.Count];
            for (var index = 0; index < gameObject.Children.Count; index++)
            {
                var child = gameObject.Children[index];
                var childObjectNode = AddObjectNode(child.ObjectChecksum);
                var radians = MathF.PI / 180f * child.AngleDegrees;
                var placementNode = new Dictionary<string, object?>
                {
                    ["name"] = package.GameObjects[child.ObjectChecksum].Name + " 放置",
                    ["translation"] = new[] { child.Position.X, child.Position.Y, -child.Position.Z },
                    // C:S uses Quaternion.AngleAxis(angle, Vector3.down). After Z reflection this is +Y.
                    ["rotation"] = new[] { 0f, MathF.Sin(radians * 0.5f), 0f, MathF.Cos(radians * 0.5f) },
                    ["children"] = new[] { childObjectNode },
                };
                childNodeIndices[index] = _nodes.Count;
                _nodes.Add(placementNode);
            }

            node["children"] = childNodeIndices;
            return nodeIndex;
        }

        private static void AddTransform(Dictionary<string, object?> node, CrpTransform transform)
        {
            if (transform.Position != Vector3.Zero)
            {
                node["translation"] = new[]
                {
                    transform.Position.X,
                    transform.Position.Y,
                    -transform.Position.Z,
                };
            }

            if (transform.Rotation != Quaternion.Identity)
            {
                node["rotation"] = new[]
                {
                    -transform.Rotation.X,
                    -transform.Rotation.Y,
                    transform.Rotation.Z,
                    transform.Rotation.W,
                };
            }

            if (transform.Scale != Vector3.One)
            {
                node["scale"] = new[] { transform.Scale.X, transform.Scale.Y, transform.Scale.Z };
            }
        }

        private int GetMeshIndex(CrpGameObject gameObject)
        {
            var key = gameObject.MeshChecksum + ":" + string.Join(',', gameObject.MaterialChecksums);
            if (_meshIndices.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var mesh = UnusedVertexCompactor.Apply(package.Meshes[gameObject.MeshChecksum!], gameObject.Name, reportTreeColor);
            var positionAccessor = AddVector3Accessor(
                mesh.Positions.Select(static value => new Vector3(value.X, value.Y, -value.Z)).ToArray(),
                includeBounds: true);
            var normalAccessor = AddVector3Accessor(
                mesh.Normals.Select(static value => Vector3.Normalize(new Vector3(value.X, value.Y, -value.Z))).ToArray(),
                includeBounds: false);
            var uvAccessor = AddVector2Accessor(mesh.TextureCoordinates);
            int? tangentAccessor = null;
            if (Enumerable.Range(0, mesh.SubMeshTriangles.Count).Any(slot =>
                    mesh.SubMeshTriangles[slot].Length != 0 && UsesSurfaceMap(gameObject.MaterialChecksums[slot])))
            {
                tangentAccessor = AddVector4Accessor(TangentFrameConverter.Convert(mesh, gameObject.Name, reportTreeColor));
            }

            var primitives = new List<Dictionary<string, object?>>(mesh.SubMeshTriangles.Count);
            for (var subMeshIndex = 0; subMeshIndex < mesh.SubMeshTriangles.Count; subMeshIndex++)
            {
                var sourceIndices = mesh.SubMeshTriangles[subMeshIndex];
                if (sourceIndices.Length == 0)
                {
                    continue;
                }

                var convertedIndices = new uint[sourceIndices.Length];
                for (var triangle = 0; triangle < sourceIndices.Length; triangle += 3)
                {
                    convertedIndices[triangle] = (uint)sourceIndices[triangle];
                    convertedIndices[triangle + 1] = (uint)sourceIndices[triangle + 2];
                    convertedIndices[triangle + 2] = (uint)sourceIndices[triangle + 1];
                }

                var attributes = new Dictionary<string, object?>
                {
                    ["POSITION"] = positionAccessor,
                    ["NORMAL"] = normalAccessor,
                    ["TEXCOORD_0"] = uvAccessor,
                };
                if (tangentAccessor is not null && UsesSurfaceMap(gameObject.MaterialChecksums[subMeshIndex]))
                {
                    attributes["TANGENT"] = tangentAccessor.Value;
                }
                primitives.Add(new Dictionary<string, object?>
                {
                    ["attributes"] = attributes,
                    ["indices"] = AddIndexAccessor(convertedIndices),
                    ["material"] = GetMaterialIndex(gameObject.MaterialChecksums[subMeshIndex]),
                    ["mode"] = 4,
                });
            }

            var meshIndex = _meshes.Count;
            _meshes.Add(new Dictionary<string, object?>
            {
                ["name"] = gameObject.Name,
                ["primitives"] = primitives,
            });
            _meshIndices.Add(key, meshIndex);
            return meshIndex;
        }

        private int GetMaterialIndex(string checksum)
        {
            if (_materialIndices.TryGetValue(checksum, out var existing))
            {
                return existing;
            }

            var source = package.Materials[checksum];
            var isGroundDecal = package.GroundDecalMaterialChecksums.Contains(checksum);
            if (source.ShaderName.StartsWith("Custom/Props/Decal/", StringComparison.Ordinal) && !isGroundDecal)
            {
                throw new InvalidDataException(
                    $"材质 {checksum} 使用地形投影贴花 Shader {source.ShaderName}；原始包围盒不能作为静态模型导出，需要单独的贴花投影转换");
            }
            var color = source.Colors.TryGetValue("_Color", out var tint) ? tint : Vector4.One;
            var hasMainTexture = source.Textures.TryGetValue("_MainTex", out var mainChecksum) &&
                !string.IsNullOrWhiteSpace(mainChecksum);
            var baseColor = hasMainTexture ? Vector4.One : color;
            var pbr = new Dictionary<string, object?>
            {
                ["baseColorFactor"] = new[] { baseColor.X, baseColor.Y, baseColor.Z, baseColor.W },
                ["metallicFactor"] = 0f,
                ["roughnessFactor"] = 0.85f,
            };

            var hasAciTexture = source.Textures.TryGetValue("_ACIMap", out var aciChecksum) &&
                !string.IsNullOrWhiteSpace(aciChecksum);
            var hasXycaTexture = source.Textures.TryGetValue("_XYCAMap", out var xycaChecksum) &&
                !string.IsNullOrWhiteSpace(xycaChecksum);
            var hasAprTexture = source.Textures.TryGetValue("_APRMap", out var aprChecksum) &&
                !string.IsNullOrWhiteSpace(aprChecksum);
            if (hasAprTexture)
            {
                if (source.ShaderName is not ("Custom/Net/RoadBridge" or "Custom/Net/Road"))
                {
                    throw new InvalidDataException($"材质 {checksum} 的 Shader 尚未校准 APR 地形/透明语义：{source.ShaderName}");
                }
                SurfaceTextureConverter.ValidateStaticBridgeMask(package.Textures[aprChecksum!], source.ShaderName == "Custom/Net/Road");
            }
            if (hasAciTexture && !SupportsInverseAciAlpha(source.ShaderName))
            {
                throw new InvalidDataException(
                    $"材质 {checksum} 的 Shader 尚未校准 ACI 透明语义：{source.ShaderName}");
            }

            if (hasXycaTexture && !string.Equals(source.ShaderName, "Custom/Trees/Default", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"材质 {checksum} 的 Shader 尚未校准 XYCA 透明语义：{source.ShaderName}");
            }

            if ((hasAciTexture ? 1 : 0) + (hasXycaTexture ? 1 : 0) + (hasAprTexture ? 1 : 0) > 1)
            {
                throw new InvalidDataException($"材质 {checksum} 同时包含多个 ACI/XYCA/APR 遮罩，透明来源不明确");
            }

            if ((hasAciTexture || hasXycaTexture || hasAprTexture) && !hasMainTexture)
            {
                throw new InvalidDataException($"材质 {checksum} 有透明遮罩，但没有 MainTex");
            }

            if (hasMainTexture)
            {
                var png = TextureConverter.CreateBaseColorPng(
                    package.Textures[mainChecksum!],
                    hasAciTexture ? package.Textures[aciChecksum!] :
                        hasXycaTexture ? package.Textures[xycaChecksum!] :
                        hasAprTexture ? package.Textures[aprChecksum!] : null,
                    hasXycaTexture ? CutoutTextureChannel.Alpha : CutoutTextureChannel.Red,
                    source.ShaderName == "Custom/Trees/Default" && treeColorOverrides is null ? treeColorCorrection : null,
                    autoCorrectTreeColor: source.ShaderName == "Custom/Trees/Default" &&
                        !preserveTreeColors && treeColorCorrection is null && treeColorOverrides is null,
                    out var automaticColor);
                if (source.ShaderName == "Custom/Trees/Default" && treeColorOverrides is not null)
                {
                    if (!treeColorOverrides.TryGetValue(checksum, out var preserved))
                    {
                        throw new InvalidDataException($"复用树木颜色的 GLB 缺少源材质 {checksum}");
                    }
                    TreeBaseColorOverrideReader.ValidateAlpha(png, preserved.Png);
                    png = preserved.Png;
                    pbr["baseColorFactor"] = new[] { preserved.Factor.X, preserved.Factor.Y, preserved.Factor.Z, preserved.Factor.W };
                    reportTreeColor?.Invoke($"保留既有树木颜色：{checksum}，RGB/alpha PNG 字节不变；另行补齐源法线");
                }
                if (automaticColor is not null)
                {
                    reportTreeColor?.Invoke(FormattableString.Invariant(
                        $"树木自动颜色适配：材质={checksum}，阴影指数={automaticColor.ShadowGamma:R}，饱和度={automaticColor.Saturation:R}，源通道峰值中位数={automaticColor.SourceMedianIntensity:R}，可见像素={automaticColor.VisibleTexels}"));
                }
                var imageIndex = AddImage(png, checksum + "_BaseColor");
                var textureIndex = _textures.Count;
                _textures.Add(new Dictionary<string, object?>
                {
                    ["name"] = checksum + "_BaseColor",
                    ["sampler"] = 0,
                    ["source"] = imageIndex,
                });
                pbr["baseColorTexture"] = new Dictionary<string, object?> { ["index"] = textureIndex };
            }

            var material = new Dictionary<string, object?>
            {
                ["name"] = checksum,
                ["doubleSided"] = true,
                ["pbrMetallicRoughness"] = pbr,
            };
            if (source.ShaderName == "Custom/Trees/Default" && hasXycaTexture)
            {
                var normalPng = SurfaceTextureConverter.CreateNormalPng(package.Textures[xycaChecksum!]);
                var normalImage = AddImage(normalPng, checksum + "_TreeNormal");
                var normalTexture = _textures.Count;
                _textures.Add(new Dictionary<string, object?>
                {
                    ["name"] = checksum + "_TreeNormal", ["sampler"] = 0, ["source"] = normalImage,
                });
                material["normalTexture"] = new Dictionary<string, object?> { ["index"] = normalTexture };
            }
            if (source.Textures.TryGetValue("_XYSMap", out var xysChecksum) && !string.IsNullOrWhiteSpace(xysChecksum))
            {
                if (!SurfaceTextureConverter.SupportsShader(source.ShaderName))
                {
                    throw new InvalidDataException($"材质 {checksum} 的 Shader 尚未校准 XYS 法线/反光语义：{source.ShaderName}");
                }
                var png = SurfaceTextureConverter.CreateNormalSpecularPng(package.Textures[xysChecksum]);
                var imageIndex = AddImage(png, checksum + "_NormalSpecular");
                var textureIndex = _textures.Count;
                _textures.Add(new Dictionary<string, object?>
                {
                    ["name"] = checksum + "_NormalSpecular", ["sampler"] = 0, ["source"] = imageIndex,
                });
                if (SurfaceTextureConverter.UsesNormalMap(source.ShaderName))
                {
                    material["normalTexture"] = new Dictionary<string, object?> { ["index"] = textureIndex };
                }
                material["extensions"] = new Dictionary<string, object?>
                {
                    ["KHR_materials_specular"] = new Dictionary<string, object?>
                    {
                        ["specularFactor"] = 1f,
                        ["specularTexture"] = new Dictionary<string, object?> { ["index"] = textureIndex },
                    },
                };
                _usesSpecular = true;
            }
            if (isGroundDecal)
            {
                material["alphaMode"] = "BLEND";
            }
            if (hasAciTexture || hasXycaTexture || hasAprTexture)
            {
                material["alphaMode"] = "MASK";
                material["alphaCutoff"] = 0.5f;
            }

            var materialIndex = _materials.Count;
            _materials.Add(material);
            _materialIndices.Add(checksum, materialIndex);
            return materialIndex;
        }

        private static bool SupportsInverseAciAlpha(string shaderName)
            => shaderName.StartsWith("Custom/Buildings/Building/", StringComparison.Ordinal) ||
                shaderName.StartsWith("Custom/Props/Prop/", StringComparison.Ordinal);

        private bool UsesSurfaceMap(string checksum)
        {
            var material = package.Materials[checksum];
            var role = material.ShaderName == "Custom/Trees/Default" ? "_XYCAMap" : "_XYSMap";
            return SurfaceTextureConverter.UsesNormalMap(material.ShaderName) &&
                material.Textures.TryGetValue(role, out var value) && !string.IsNullOrWhiteSpace(value);
        }

        private int AddImage(byte[] png, string name)
        {
            var bufferView = AddBufferView(png, target: null);
            var index = _images.Count;
            _images.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["bufferView"] = bufferView,
                ["mimeType"] = "image/png",
            });
            return index;
        }

        private int AddVector3Accessor(Vector3[] values, bool includeBounds)
        {
            var bytes = new byte[values.Length * 12];
            for (var index = 0; index < values.Length; index++)
            {
                WriteSingle(bytes.AsSpan(index * 12), values[index].X);
                WriteSingle(bytes.AsSpan(index * 12 + 4), values[index].Y);
                WriteSingle(bytes.AsSpan(index * 12 + 8), values[index].Z);
            }

            var accessor = new Dictionary<string, object?>
            {
                ["bufferView"] = AddBufferView(bytes, 34962),
                ["componentType"] = 5126,
                ["count"] = values.Length,
                ["type"] = "VEC3",
            };
            if (includeBounds)
            {
                accessor["min"] = new[]
                {
                    values.Min(static value => value.X),
                    values.Min(static value => value.Y),
                    values.Min(static value => value.Z),
                };
                accessor["max"] = new[]
                {
                    values.Max(static value => value.X),
                    values.Max(static value => value.Y),
                    values.Max(static value => value.Z),
                };
            }

            var accessorIndex = _accessors.Count;
            _accessors.Add(accessor);
            return accessorIndex;
        }

        private int AddVector4Accessor(Vector4[] values)
        {
            var bytes = new byte[values.Length * 16];
            for (var index = 0; index < values.Length; index++)
            {
                WriteSingle(bytes.AsSpan(index * 16), values[index].X);
                WriteSingle(bytes.AsSpan(index * 16 + 4), values[index].Y);
                WriteSingle(bytes.AsSpan(index * 16 + 8), values[index].Z);
                WriteSingle(bytes.AsSpan(index * 16 + 12), values[index].W);
            }
            var accessorIndex = _accessors.Count;
            _accessors.Add(new Dictionary<string, object?>
            {
                ["bufferView"] = AddBufferView(bytes, 34962), ["componentType"] = 5126,
                ["count"] = values.Length, ["type"] = "VEC4",
            });
            return accessorIndex;
        }

        private int AddVector2Accessor(Vector2[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (var index = 0; index < values.Length; index++)
            {
                WriteSingle(bytes.AsSpan(index * 8), values[index].X);
                WriteSingle(bytes.AsSpan(index * 8 + 4), values[index].Y);
            }

            var accessorIndex = _accessors.Count;
            _accessors.Add(new Dictionary<string, object?>
            {
                ["bufferView"] = AddBufferView(bytes, 34962),
                ["componentType"] = 5126,
                ["count"] = values.Length,
                ["type"] = "VEC2",
            });
            return accessorIndex;
        }

        private int AddIndexAccessor(uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (var index = 0; index < values.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), values[index]);
            }

            var accessorIndex = _accessors.Count;
            _accessors.Add(new Dictionary<string, object?>
            {
                ["bufferView"] = AddBufferView(bytes, 34963),
                ["componentType"] = 5125,
                ["count"] = values.Length,
                ["type"] = "SCALAR",
                ["min"] = new[] { values.Min() },
                ["max"] = new[] { values.Max() },
            });
            return accessorIndex;
        }

        private int AddBufferView(byte[] bytes, int? target)
        {
            var (offset, length) = _binary.Add(bytes);
            var view = new Dictionary<string, object?>
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = length,
            };
            if (target.HasValue)
            {
                view["target"] = target.Value;
            }

            var index = _bufferViews.Count;
            _bufferViews.Add(view);
            return index;
        }

        private static void WriteSingle(Span<byte> target, float value)
            => BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(value));
    }

    private static byte[] WriteGlb(byte[] json, byte[] binary)
    {
        var paddedJsonLength = Align4(json.Length);
        var paddedBinaryLength = Align4(binary.Length);
        var totalLength = 12 + 8 + paddedJsonLength + 8 + paddedBinaryLength;
        var glb = new byte[totalLength];
        var span = glb.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 0x4E4F534A);
        json.CopyTo(span[20..]);
        span.Slice(20 + json.Length, paddedJsonLength - json.Length).Fill((byte)' ');
        var binaryHeader = 20 + paddedJsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(span[binaryHeader..], (uint)paddedBinaryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(binaryHeader + 4)..], 0x004E4942);
        binary.CopyTo(span[(binaryHeader + 8)..]);
        return glb;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private sealed class BinaryBuffer
    {
        private readonly MemoryStream _stream = new();

        public int Length => checked((int)_stream.Length);

        public (int Offset, int Length) Add(ReadOnlySpan<byte> bytes)
        {
            while ((_stream.Position & 3) != 0)
            {
                _stream.WriteByte(0);
            }

            var offset = checked((int)_stream.Position);
            _stream.Write(bytes);
            return (offset, bytes.Length);
        }

        public byte[] ToArray()
        {
            while ((_stream.Position & 3) != 0)
            {
                _stream.WriteByte(0);
            }

            return _stream.ToArray();
        }
    }
}

internal static class TextureConverter
{
    public static byte[] CreateBaseColorPng(
        CrpTexture mainTexture,
        CrpTexture? cutoutTexture,
        CutoutTextureChannel cutoutChannel,
        TreeColorCorrection? colorCorrection,
        bool autoCorrectTreeColor,
        out TreeMaterialColorConverter.Analysis? automaticColor)
    {
        using var diffuse = DecodeImage(mainTexture.ImageBytes);
        using var cutout = cutoutTexture is null ? null : DecodeImage(cutoutTexture.ImageBytes);
        if (cutout is not null && (cutout.Width != diffuse.Width || cutout.Height != diffuse.Height))
        {
            throw new InvalidDataException(
                $"MainTex 与透明遮罩尺寸不一致：{diffuse.Width}x{diffuse.Height} / {cutout.Width}x{cutout.Height}");
        }

        if (cutout is not null)
        {
            diffuse.ProcessPixelRows(cutout, (diffuseRows, cutoutRows) =>
            {
                for (var y = 0; y < diffuseRows.Height; y++)
                {
                    var diffuseRow = diffuseRows.GetRowSpan(y);
                    var cutoutRow = cutoutRows.GetRowSpan(y);
                    for (var x = 0; x < diffuseRow.Length; x++)
                    {
                        ref var pixel = ref diffuseRow[x];
                        // The game's importer inverts building ACI.R and tree XYCA.A.
                        var mask = cutoutChannel == CutoutTextureChannel.Alpha
                            ? cutoutRow[x].A
                            : cutoutRow[x].R;
                        pixel.A = Math.Min(pixel.A, (byte)(255 - mask));
                    }
                }
            });
        }

        automaticColor = autoCorrectTreeColor ? TreeMaterialColorConverter.Analyze(diffuse, mainTexture.ForceLinear) : null;
        if (automaticColor is not null)
        {
            TreeMaterialColorConverter.ApplyAutomatic(diffuse, automaticColor, mainTexture.ForceLinear);
        }
        else if (colorCorrection is not null)
        {
            TreeMaterialColorConverter.Apply(diffuse, colorCorrection, mainTexture.ForceLinear);
        }

        using var output = new MemoryStream();
        diffuse.Save(output, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha,
            CompressionLevel = PngCompressionLevel.BestCompression,
        });
        return output.ToArray();
    }

    internal static Image<Rgba32> DecodeImage(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith("DDS "u8))
        {
            return Image.Load<Rgba32>(bytes);
        }

        using var stream = new MemoryStream(bytes, writable: false);
        using var source = Pfimage.FromStream(stream);
        var output = new Image<Rgba32>(source.Width, source.Height);
        output.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < source.Height; y++)
            {
                var inputOffset = y * source.Stride;
                var row = rows.GetRowSpan(y);
                switch (source.Format)
                {
                    case ImageFormat.Rgba32:
                        for (var x = 0; x < source.Width; x++)
                        {
                            var offset = inputOffset + x * 4;
                            row[x] = new Rgba32(
                                source.Data[offset + 2],
                                source.Data[offset + 1],
                                source.Data[offset],
                                source.Data[offset + 3]);
                        }
                        break;
                    case ImageFormat.Rgb24:
                        for (var x = 0; x < source.Width; x++)
                        {
                            var offset = inputOffset + x * 3;
                            row[x] = new Rgba32(
                                source.Data[offset + 2],
                                source.Data[offset + 1],
                                source.Data[offset],
                                255);
                        }
                        break;
                    default:
                        throw new InvalidDataException($"尚未支持的 DDS 像素格式：{source.Format}");
                }
            }
        });
        return output;
    }
}
