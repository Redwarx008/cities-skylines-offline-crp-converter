using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OfflineCrpConverter.Tests;

internal static class SurfaceAndBaseRegression
{
    public static void Run()
    {
        var failures = new List<string>();
        var passed = 0;
        void Test(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine($"PASS SURFACE/BASE: {name}");
                passed++;
            }
            catch (Exception exception)
            {
                failures.Add(name + ": " + exception.Message);
                Console.WriteLine($"FAIL SURFACE/BASE: {name}: {exception.Message}");
            }
        }

        Test("Default builder removes only the generated suffix", () =>
        {
            var package = Fixture();
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            var primitive = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0];
            Require(AccessorCount(document, primitive.GetProperty("indices").GetInt32()) == 6, "Generated triangles were retained");
            Require(AccessorCount(document, primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()) == 4,
                "Generated-only vertices were retained");
            GlbValidator.Validate(glb, package, "fixture");
        });
        Test("Authored negative-height geometry and UV are untouched", () =>
        {
            var package = Fixture();
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            var primitive = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0];
            var attributes = primitive.GetProperty("attributes");
            var positions = FloatAccessor(glb, document, attributes.GetProperty("POSITION").GetInt32(), 3);
            var uv = FloatAccessor(glb, document, attributes.GetProperty("TEXCOORD_0").GetInt32(), 2);
            Require(positions.SequenceEqual(new float[] { 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, -.25f, -1 }), "Authored geometry changed");
            Require(uv.SequenceEqual(new float[] { 0, 0, 1, 0, 0, 1, .25f, .25f }), "Authored UV changed");
        });
        Test("The source package remains unchanged and output is deterministic", () =>
        {
            var package = Fixture();
            var first = GltfBuilder.Build(package, "fixture");
            var second = GltfBuilder.Build(package, "fixture");
            Require(package.Meshes["mesh"].Positions.Length == 8 && package.Meshes["mesh"].SubMeshTriangles[0].Length == 12,
                "Source mesh was modified");
            Require(first.SequenceEqual(second), "Repeated build changed the output");
        });
        Test("Normal texture is reconstructed, not copied from packed blue", () =>
        {
            var glb = GltfBuilder.Build(Fixture(), "fixture");
            using var document = Document(glb);
            var material = document.RootElement.GetProperty("materials")[0];
            using var image = Texture(glb, document, material.GetProperty("normalTexture").GetProperty("index").GetInt32());
            var flat = image[0, 0]; // Packed blue is 0 (maximum specularity), not normal Z.
            Require(flat.R == 128 && flat.G == 128 && flat.B >= 254, "Flat normal was decoded from the specular channel");
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, 0];
                var normal = new Vector3(pixel.R, pixel.G, pixel.B) / 255f * 2 - Vector3.One;
                Require(MathF.Abs(normal.Length() - 1) < .012f, "Decoded normal is not normalized");
            }
        });
        Test("Specular extension uses inverse packed blue in linear alpha", () =>
        {
            var glb = GltfBuilder.Build(Fixture(), "fixture");
            using var document = Document(glb);
            var root = document.RootElement;
            var material = root.GetProperty("materials")[0];
            var specular = material.GetProperty("extensions").GetProperty("KHR_materials_specular");
            var index = specular.GetProperty("specularTexture").GetProperty("index").GetInt32();
            Require(root.GetProperty("extensionsUsed").EnumerateArray().Any(value => value.GetString() == "KHR_materials_specular"),
                "Extension is not declared");
            using var image = Texture(glb, document, index);
            Require(image[0, 0].A == 255 && image[1, 0].A == 0 && image[2, 0].A == 127, "Specular channel has wrong inversion or gamma");
            Require(index == material.GetProperty("normalTexture").GetProperty("index").GetInt32(), "Compatible maps were duplicated");
            Require(material.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")[3].GetSingle() == 1,
                "Reflectivity incorrectly changed opacity");
        });
        Test("Tangent reflection changes XYZ and handedness consistently", () =>
        {
            var glb = GltfBuilder.Build(Fixture(), "fixture");
            using var document = Document(glb);
            var attributes = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
            var tangents = FloatAccessor(glb, document, attributes.GetProperty("TANGENT").GetInt32(), 4);
            Require(tangents.Length == 16 && tangents[0] == 1 && tangents[1] == 0 && tangents[2] == 0 && tangents[3] == 1,
                "Tangent handedness was not reflected with the mesh");
        });
        Test("Surface-map alpha never becomes base-color opacity", () =>
        {
            var glb = GltfBuilder.Build(Fixture(), "fixture");
            using var document = Document(glb);
            var material = document.RootElement.GetProperty("materials")[0];
            var index = material.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("index").GetInt32();
            using var image = Texture(glb, document, index);
            Require(image[0, 0].A == 255 && image[1, 0].A == 255, "Normal/specular alpha contaminated diffuse alpha");
        });
        Test("All-white authored vertices are not interpreted as a generated suffix", () =>
        {
            var package = Fixture();
            var source = package.Meshes["mesh"];
            package = WithMesh(package, source with { Colors = Enumerable.Repeat(Vector4.One, 8).ToArray() });
            Require(TriangleCount(GltfBuilder.Build(package, "fixture")) == 4, "All-white mesh was cut");
        });
        Test("Authored terrain/foundation-like geometry without a marker is retained", () =>
        {
            var package = Fixture();
            var source = package.Meshes["mesh"];
            package = WithMesh(package, source with { Colors = Enumerable.Repeat(new Vector4(1, 1, 1, 0), 8).ToArray() });
            Require(TriangleCount(GltfBuilder.Build(package, "fixture")) == 4, "Y<0 was cut without a generated-base marker");
        });
        Test("Non-tail white colors do not activate removal", () =>
        {
            var package = Fixture();
            var source = package.Meshes["mesh"];
            var colors = source.Colors.ToArray();
            colors[1] = Vector4.One;
            package = WithMesh(package, source with { Colors = colors });
            Require(TriangleCount(GltfBuilder.Build(package, "fixture")) == 4, "Interleaved colors were misidentified");
        });
        Test("Above-ground white suffix is not a basement", () =>
        {
            var package = Fixture();
            var source = package.Meshes["mesh"];
            var positions = source.Positions.ToArray();
            positions[4].Y = 1;
            package = WithMesh(package, source with { Positions = positions });
            Require(TriangleCount(GltfBuilder.Build(package, "fixture")) == 4, "Above-ground geometry was removed");
        });
        Test("The building-specific remover does not affect prop geometry", () =>
        {
            Require(TriangleCount(GltfBuilder.Build(Fixture("Custom/Props/Prop/Default"), "fixture")) == 4, "Prop geometry was cut");
        });
        Test("Building Fence shader has the same independently marked generated skirt", () =>
        {
            Require(TriangleCount(GltfBuilder.Build(Fixture("Custom/Buildings/Building/Fence"), "fixture")) == 2,
                "The generated Building/Fence skirt was retained");
        });
        Test("Prop Fence shader is not treated as a building basement", () =>
        {
            Require(TriangleCount(GltfBuilder.Build(Fixture("Custom/Props/Prop/Fence"), "fixture")) == 4,
                "Authored prop fence geometry was trimmed");
        });
        Test("Mixed original/generated triangle does not activate a guessed removal", () =>
        {
            var package = Fixture();
            var source = package.Meshes["mesh"];
            var indices = source.SubMeshTriangles[0].ToArray();
            indices[6] = 0;
            package = WithMesh(package, source with { SubMeshTriangles = new[] { indices } });
            Require(TriangleCount(GltfBuilder.Build(package, "fixture")) == 4, "Ambiguous mixed geometry was removed");
        });
        Test("A base-only submesh is skipped and completeness validation follows the render package", () =>
        {
            var package = Fixture(separateBaseSlot: true);
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            Require(document.RootElement.GetProperty("meshes")[0].GetProperty("primitives").GetArrayLength() == 1,
                "An empty generated-base primitive was retained");
            GlbValidator.Validate(glb, package, "fixture");
        });
        Test("Source XYS marked as sRGB is explicitly rejected", () =>
        {
            try
            {
                GltfBuilder.Build(Fixture(linearXys: false), "fixture");
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("Ambiguous XYS color space was silently accepted");
        });
        Test("RoadBridge static material preserves APR cutout and XYS surface channels", () =>
        {
            var package = Fixture("Custom/Net/RoadBridge");
            using var apr = new Image<Rgba32>(3, 1);
            apr[0, 0] = new Rgba32(0, 255, 0, 255);
            apr[1, 0] = new Rgba32(255, 255, 0, 255);
            apr[2, 0] = new Rgba32(128, 255, 0, 255);
            ((Dictionary<string, CrpTexture>)package.Textures)["apr"] = new(true, Png(apr));
            var materials = (Dictionary<string, CrpMaterial>)package.Materials;
            materials["material"] = materials["material"] with
            {
                Textures = new Dictionary<string, string>
                {
                    ["_MainTex"] = "diffuse", ["_XYSMap"] = "xys", ["_APRMap"] = "apr",
                },
            };
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            var material = document.RootElement.GetProperty("materials")[0];
            using var image = Texture(glb, document, material.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("index").GetInt32());
            Require(image[0, 0].A == 255 && image[1, 0].A == 0 && image[2, 0].A == 127, "Wrong APR transparency channel");
            Require(material.GetProperty("alphaMode").GetString() == "MASK" && material.TryGetProperty("normalTexture", out _), "RoadBridge surface or alpha missing");
            Require(TriangleCount(glb) == 4, "Network geometry was trimmed as a building");
            GlbValidator.Validate(glb, package, "fixture");
        });
        Test("RoadBridge pavement/road-theme substitution is not silently ignored", () =>
        {
            var package = Fixture("Custom/Net/RoadBridge");
            using var apr = new Image<Rgba32>(3, 1, new Rgba32(0, 0, 0, 255));
            ((Dictionary<string, CrpTexture>)package.Textures)["apr"] = new(true, Png(apr));
            ((Dictionary<string, string>)package.Materials["material"].Textures)["_APRMap"] = "apr";
            try
            {
                GltfBuilder.Build(package, "fixture");
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("Map-theme pavement was silently replaced with an unrelated atlas");
        });
        Test("Opaque basic Road preserves specularity but does not invent normal shading", () =>
        {
            var package = Fixture("Custom/Net/Road");
            using var apr = new Image<Rgba32>(3, 1, new Rgba32(0, 255, 0, 255));
            ((Dictionary<string, CrpTexture>)package.Textures)["apr"] = new(true, Png(apr));
            ((Dictionary<string, string>)package.Materials["material"].Textures)["_APRMap"] = "apr";
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            var material = document.RootElement.GetProperty("materials")[0];
            Require(!material.TryGetProperty("normalTexture", out _), "The basic game Road shader does not use normal maps");
            Require(material.GetProperty("extensions").TryGetProperty("KHR_materials_specular", out _), "Road specularity omitted");
            Require(!document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").TryGetProperty("TANGENT", out _),
                "Added unused road tangents");
            GlbValidator.Validate(glb, package, "fixture");
        });
        Test("Diagnostic output reports removal in the actual default path", () =>
        {
            var messages = new List<string>();
            GltfBuilder.Build(Fixture(), "fixture", null, false, messages.Add);
            Require(messages.Any(message => message.Contains("底座", StringComparison.Ordinal)), "No generated-base diagnostic was produced");
        });
        Test("Shared source mesh remains intact for a non-building instance", () =>
        {
            var package = Fixture();
            var objects = (Dictionary<string, CrpGameObject>)package.GameObjects;
            var materials = (Dictionary<string, CrpMaterial>)package.Materials;
            objects["object"] = objects["object"] with { Children = [new CrpChild("prop", Vector3.Zero, 0)] };
            objects["prop"] = objects["object"] with
            {
                Name = "prop", AssetChecksum = "prop", MaterialChecksums = ["prop-material"], Children = [],
            };
            materials["prop-material"] = materials["material"] with { ShaderName = "Custom/Props/Prop/Default" };
            var glb = GltfBuilder.Build(package, "fixture");
            Require(TriangleCount(glb) == 6, "A shared prop mesh was trimmed with its building instance");
            GlbValidator.Validate(glb, package, "fixture");
        });
        Test("Missing source tangents are reconstructed from UVs in the offline path", () =>
        {
            var package = Fixture();
            package = WithMesh(package, package.Meshes["mesh"] with { Tangents = [] });
            using var document = Document(GltfBuilder.Build(package, "fixture"));
            var primitive = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0];
            Require(primitive.GetProperty("attributes").TryGetProperty("TANGENT", out var tangent), "Missing offline UV tangent reconstruction");
            Require(FloatAccessor(GltfBuilder.Build(package, "fixture"), document, tangent.GetInt32(), 4).All(float.IsFinite), "Nonfinite reconstructed tangent");
            Require(document.RootElement.GetProperty("materials")[0].TryGetProperty("normalTexture", out _), "Dropped normal map without source tangents");
        });
        Test("Invalid source tangents are repaired without changing authored geometry", () =>
        {
            var package = Fixture();
            var tangents = package.Meshes["mesh"].Tangents.ToArray();
            tangents[0] = new Vector4(float.NaN, 0, 0, 1);
            tangents[1] = new Vector4(0, 0, 1, -1); // Parallel to its normal.
            package = WithMesh(package, package.Meshes["mesh"] with { Tangents = tangents });
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            var attributes = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
            var result = FloatAccessor(glb, document, attributes.GetProperty("TANGENT").GetInt32(), 4);
            Require(result.All(float.IsFinite), "Invalid tangent survived reconstruction");
            Require(TriangleCount(glb) == 2, "Tangent reconstruction changed authored triangles");
            Require(result[8] == 1 && result[9] == 0 && result[10] == 0 && result[11] == 1,
                "An existing valid source tangent was changed");
        });
        Test("Near-parallel source tangents remain numerically orthogonal after normalization", () =>
        {
            var package = Fixture();
            var normal = Vector3.Normalize(new Vector3(1, 2, 3));
            var source = package.Meshes["mesh"];
            package = WithMesh(package, source with
            {
                Normals = Enumerable.Repeat(normal, 8).ToArray(),
                Tangents = Enumerable.Repeat(new Vector4(normal + Vector3.UnitX * .00001f, 1), 8).ToArray(),
            });
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            var attributes = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
            var normals = FloatAccessor(glb, document, attributes.GetProperty("NORMAL").GetInt32(), 3);
            var tangents = FloatAccessor(glb, document, attributes.GetProperty("TANGENT").GetInt32(), 4);
            var dot = normals[0]*tangents[0]+normals[1]*tangents[1]+normals[2]*tangents[2];
            Require(MathF.Abs(dot) < .000002f, "Cancellation left a non-orthogonal tangent frame");
        });
        Test("Unused outlier vertices do not inflate static GLB bounds", () =>
        {
            var package = Fixture("Custom/Props/Prop/Default");
            var mesh = package.Meshes["mesh"];
            package = WithMesh(package, mesh with
            {
                Positions = [..mesh.Positions, new Vector3(1000)],
                Colors = [..mesh.Colors, Vector4.Zero], TextureCoordinates = [..mesh.TextureCoordinates, Vector2.Zero],
                Normals = [..mesh.Normals, Vector3.UnitZ], Tangents = [..mesh.Tangents, new Vector4(1, 0, 0, 1)],
            });
            var glb = GltfBuilder.Build(package, "fixture");
            using var document = Document(glb);
            var primitive = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0];
            Require(AccessorCount(document, primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()) == 8,
                "Unused bounding-box outlier was exported");
            Require(TriangleCount(glb) == 4, "A referenced triangle was removed with unused vertices");
            GlbValidator.Validate(glb, package, "fixture");
        });
        Test("Tree XYCA exports normal RG independently of color-mask B and cutout A", () =>
        {
            var package = TreeFixture();
            var glb = GltfBuilder.Build(package, "fixture", preserveTreeColors: true);
            using var document = Document(glb);
            var material = document.RootElement.GetProperty("materials")[0];
            using var normal = Texture(glb, document, material.GetProperty("normalTexture").GetProperty("index").GetInt32());
            Require(normal[0, 0].B == 255 && normal[0, 0].A == 255, "XYCA color/cutout channel was used as normal Z or opacity");
            Require(!material.TryGetProperty("extensions", out _), "Tree color mask was turned into invented specularity");
            using var color = Texture(glb, document, material.GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture").GetProperty("index").GetInt32());
            Require(color[0, 0].A == 255 && color[1, 0].A == 0 && color[2, 0].A == 255, "Tree cutout changed");
            GlbValidator.Validate(glb, package, "fixture");
        });
        Test("Existing calibrated tree PNG and factor survive a surface-only rebuild", () =>
        {
            var package = TreeFixture();
            using var image = new Image<Rgba32>(3, 1);
            image[0, 0] = new Rgba32(10, 90, 30, 255);
            image[1, 0] = new Rgba32(200, 100, 250, 0);
            image[2, 0] = new Rgba32(20, 210, 80, 255);
            var png = Png(image);
            var original = GltfBuilder.Build(package, "fixture", treeColorOverrides:
                new Dictionary<string, TreeBaseColorOverride> { ["material"] = new(png, new Vector4(.8f, .9f, .7f, 1)) });
            var overrides = TreeBaseColorOverrideReader.Read(original);
            Require(overrides["material"].Png.SequenceEqual(png), "Calibrated PNG was re-encoded or recolored");
            var rebuilt = GltfBuilder.Build(package, "fixture", treeColorOverrides: overrides);
            Require(rebuilt.SequenceEqual(original), "Surface-only rebuild changed accepted tree appearance");
        });
        Test("Reusing tree color cannot preserve a conflicting source transparency mask", () =>
        {
            using var wrong = new Image<Rgba32>(3, 1, new Rgba32(100, 90, 80, 255));
            try
            {
                GltfBuilder.Build(TreeFixture(), "fixture", treeColorOverrides:
                    new Dictionary<string, TreeBaseColorOverride> { ["material"] = new(Png(wrong), Vector4.One) });
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("Conflicting old alpha was accepted");
        });
        Test("A material without XYS does not receive invented normal/specular maps", () =>
        {
            var package = Fixture();
            var materials = (Dictionary<string, CrpMaterial>)package.Materials;
            materials["material"] = materials["material"] with
            {
                Textures = new Dictionary<string, string> { ["_MainTex"] = "diffuse" },
            };
            using var document = Document(GltfBuilder.Build(package, "fixture"));
            Require(!document.RootElement.TryGetProperty("extensionsUsed", out _), "Invented specular extension");
            Require(!document.RootElement.GetProperty("materials")[0].TryGetProperty("normalTexture", out _), "Invented normal map");
        });
        Test("An unused XYS material slot does not add unused tangents", () =>
        {
            var package = Fixture(separateBaseSlot: true);
            var materials = (Dictionary<string, CrpMaterial>)package.Materials;
            materials["no-surface"] = materials["material"] with
            {
                Textures = new Dictionary<string, string> { ["_MainTex"] = "diffuse" },
            };
            var objects = (Dictionary<string, CrpGameObject>)package.GameObjects;
            objects["object"] = objects["object"] with { MaterialChecksums = ["no-surface", "material"] };
            using var document = Document(GltfBuilder.Build(package, "fixture"));
            var primitive = document.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0];
            Require(!primitive.GetProperty("attributes").TryGetProperty("TANGENT", out _), "Unused material changed the geometry layout");
            Require(!document.RootElement.TryGetProperty("extensionsUsed", out _), "Unused material exported a surface extension");
        });
        Test("A missing surface binding is rejected by the production validator", () =>
        {
            var package = Fixture();
            var glb = GltfBuilder.Build(package, "fixture");
            var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
            var node = System.Text.Json.Nodes.JsonNode.Parse(glb.AsSpan(20, jsonLength))!;
            node["materials"]![0]!.AsObject().Remove("normalTexture");
            var json = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
            var paddedLength = (json.Length + 3) & ~3;
            var damaged = new byte[20 + paddedLength + glb.Length - 20 - jsonLength];
            glb.AsSpan(0, 20).CopyTo(damaged);
            BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(8), damaged.Length);
            BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(12), paddedLength);
            damaged.AsSpan(20, paddedLength).Fill(0x20);
            json.CopyTo(damaged.AsSpan(20));
            glb.AsSpan(20 + jsonLength).CopyTo(damaged.AsSpan(20 + paddedLength));
            try
            {
                GlbValidator.Validate(damaged, package, "fixture");
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("The production validator accepted a missing normal binding");
        });

        Console.WriteLine($"SURFACE_BASE_REGRESSIONS={passed}; FAILED={failures.Count}");
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static CrpPackage Fixture(string shader = "Custom/Buildings/Building/Default", bool linearXys = true, bool separateBaseSlot = false)
    {
        using var diffuse = new Image<Rgba32>(3, 1, new Rgba32(100, 90, 80, 255));
        using var packed = new Image<Rgba32>(3, 1);
        packed[0, 0] = new Rgba32(128, 128, 0, 255);
        packed[1, 0] = new Rgba32(255, 128, 255, 255);
        packed[2, 0] = new Rgba32(128, 255, 128, 255);
        var body = new[] { 0, 1, 2, 0, 3, 1 };
        var generated = new[] { 4, 6, 5, 5, 6, 7 };
        return new CrpPackage
        {
            SourcePath = "generated fixture", MainAssetName = "fixture", MainObjectName = "fixture", MainObjectChecksum = "object",
            Meshes = new Dictionary<string, CrpMesh>
            {
                ["mesh"] = new(
                    [Vector3.Zero, new(2, 0, 0), new(0, 2, 0), new(0, -.25f, 1), Vector3.Zero, new(2, 0, 0), new(0, -1, 0), new(2, -1, 0)],
                    Enumerable.Repeat(new Vector4(1, 1, 1, 0), 4).Concat(Enumerable.Repeat(Vector4.One, 4)).ToArray(),
                    [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, new(.25f, .25f), Vector2.Zero, new(2, 0), new(0, -1), new(2, -1)],
                    Enumerable.Repeat(Vector3.UnitZ, 8).ToArray(), Enumerable.Repeat(new Vector4(1, 0, 0, -1), 8).ToArray(),
                    separateBaseSlot ? [body, generated] : [body.Concat(generated).ToArray()]),
            },
            Textures = new Dictionary<string, CrpTexture> { ["diffuse"] = new(false, Png(diffuse)), ["xys"] = new(linearXys, Png(packed)) },
            Materials = new Dictionary<string, CrpMaterial>
            {
                ["material"] = new(shader, new Dictionary<string, Vector4>(), new Dictionary<string, Vector4>(),
                    new Dictionary<string, float>(), new Dictionary<string, string> { ["_MainTex"] = "diffuse", ["_XYSMap"] = "xys" }),
            },
            GameObjects = new Dictionary<string, CrpGameObject>
            {
                ["object"] = new("fixture", "object", CrpTransform.Identity, "mesh",
                    separateBaseSlot ? ["material", "material"] : ["material"], [], "BuildingInfo", [], false),
            },
        };
    }

    private static CrpPackage WithMesh(CrpPackage package, CrpMesh mesh) => new()
    {
        SourcePath = package.SourcePath, MainAssetName = package.MainAssetName, MainObjectName = package.MainObjectName,
        MainObjectChecksum = package.MainObjectChecksum, Meshes = new Dictionary<string, CrpMesh> { ["mesh"] = mesh },
        Textures = package.Textures, Materials = package.Materials, GameObjects = package.GameObjects,
    };

    private static CrpPackage TreeFixture()
    {
        var package = Fixture("Custom/Trees/Default");
        using var map = new Image<Rgba32>(3, 1);
        map[0, 0] = new Rgba32(128, 128, 0, 0);
        map[1, 0] = new Rgba32(200, 100, 255, 255);
        map[2, 0] = new Rgba32(90, 200, 64, 0);
        ((Dictionary<string, CrpTexture>)package.Textures)["xyca"] = new(true, Png(map));
        ((Dictionary<string, CrpMaterial>)package.Materials)["material"] = package.Materials["material"] with
        {
            Textures = new Dictionary<string, string> { ["_MainTex"] = "diffuse", ["_XYCAMap"] = "xyca" },
        };
        return package;
    }

    private static byte[] Png(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static JsonDocument Document(byte[] glb) => JsonDocument.Parse(glb.AsMemory(20, BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12))));

    private static int AccessorCount(JsonDocument document, int index) => document.RootElement.GetProperty("accessors")[index].GetProperty("count").GetInt32();

    private static int TriangleCount(byte[] glb)
    {
        using var document = Document(glb);
        return document.RootElement.GetProperty("meshes").EnumerateArray().Sum(mesh => mesh.GetProperty("primitives")
            .EnumerateArray().Sum(primitive => AccessorCount(document, primitive.GetProperty("indices").GetInt32()) / 3));
    }

    private static float[] FloatAccessor(byte[] glb, JsonDocument document, int index, int columns)
    {
        var root = document.RootElement;
        var accessor = root.GetProperty("accessors")[index];
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        var binaryStart = 28 + BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var start = binaryStart + view.GetProperty("byteOffset").GetInt32();
        return Enumerable.Range(0, accessor.GetProperty("count").GetInt32() * columns)
            .Select(component => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(start + component * 4)))).ToArray();
    }

    private static Image<Rgba32> Texture(byte[] glb, JsonDocument document, int texture)
    {
        var root = document.RootElement;
        var image = root.GetProperty("images")[root.GetProperty("textures")[texture].GetProperty("source").GetInt32()];
        var view = root.GetProperty("bufferViews")[image.GetProperty("bufferView").GetInt32()];
        var binaryStart = 28 + BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        return Image.Load<Rgba32>(glb.AsSpan(binaryStart + view.GetProperty("byteOffset").GetInt32(), view.GetProperty("byteLength").GetInt32()));
    }
}
