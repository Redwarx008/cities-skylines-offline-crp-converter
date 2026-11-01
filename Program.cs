namespace OfflineCrpConverter;

internal static class Program
{
    private sealed record Arguments(string CrpPath, string OutputPath, string? AssetName, string? WorkshopDirectory,
        float? DecalGroundHeight, IReadOnlySet<string> ExcludedPrefabNames, TreeColorCorrection? TreeColorCorrection,
        bool PreserveTreeColors);

    public static int Main(string[] args)
    {
        try
        {
            var arguments = ParseArguments(args);
            var package = WorkshopPrefabReader.Read(arguments.CrpPath, arguments.WorkshopDirectory, arguments.ExcludedPrefabNames);
            if (arguments.DecalGroundHeight is float groundHeight)
            {
                package = GroundDecalConverter.Apply(package, groundHeight);
            }
            var outputPath = Path.GetFullPath(arguments.OutputPath);
            var assetName = arguments.AssetName ?? package.MainAssetName;

            if (File.Exists(outputPath))
            {
                throw new IOException($"输出文件已存在，拒绝覆盖：{outputPath}");
            }

            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new ArgumentException($"输出路径没有父目录：{outputPath}");
            Directory.CreateDirectory(outputDirectory);

            var glb = GltfBuilder.Build(package, assetName, arguments.TreeColorCorrection,
                arguments.PreserveTreeColors, Console.WriteLine);
            GlbValidator.Validate(glb, package, assetName);
            File.WriteAllBytes(outputPath, glb);

            Console.WriteLine($"已生成：{outputPath}");
            if (arguments.TreeColorCorrection is { } correction)
            {
                Console.WriteLine($"已应用显式树木静态颜色校准：曝光={correction.ExposureStops} EV，饱和度={correction.Saturation}");
            }
            else if (arguments.PreserveTreeColors)
            {
                Console.WriteLine("已显式选择树木原始 RGB：--tree-color-mode raw");
            }
            foreach (var prefabName in arguments.ExcludedPrefabNames)
            {
                Console.WriteLine($"已按显式选项排除工坊道具：{prefabName}");
            }
            Console.WriteLine(
                $"主模型={package.MainObjectName}，网格={package.ReachableMeshCount}，" +
                $"图元={package.ReachablePrimitiveCount}，材质={package.ReachableMaterialCount}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"转换失败：{exception.Message}");
            return 1;
        }
    }

    private static Arguments ParseArguments(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var excludedPrefabNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
            {
                throw new ArgumentException(
                    "用法：OfflineCrpConverter --crp <输入.crp> --output <输出.glb> [--name <资产名>] [--workshop <本地CRP根目录>] [--decal-ground <贴花局部地面高度>] [--exclude-prefab <完整工坊道具引用名>] [--tree-color-mode auto|raw] [--tree-color-exposure <EV> --tree-color-saturation <倍率>]");
            }

            var option = args[index];
            var value = args[++index];
            if (string.Equals(option, "--exclude-prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("--exclude-prefab 需要完整工坊道具引用名");
                }

                excludedPrefabNames.Add(value);
            }
            else
            {
                if (!new[] { "--crp", "--output", "--name", "--workshop", "--decal-ground",
                        "--tree-color-mode", "--tree-color-exposure", "--tree-color-saturation" }
                    .Contains(option, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"未知选项：{option}");
                }

                if (!values.TryAdd(option, value))
                {
                    throw new ArgumentException($"选项重复：{option}");
                }
            }
        }

        var crpPath = GetRequiredPath(values, "--crp");
        var outputPath = GetRequiredPath(values, "--output");
        if (!File.Exists(crpPath))
        {
            throw new FileNotFoundException("CRP 文件不存在", crpPath);
        }

        if (!string.Equals(Path.GetExtension(outputPath), ".glb", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--output 必须是 .glb 文件路径");
        }

        values.TryGetValue("--name", out var assetName);
        values.TryGetValue("--workshop", out var workshopDirectory);
        if (workshopDirectory is not null && !Directory.Exists(workshopDirectory))
        {
            throw new DirectoryNotFoundException($"--workshop 目录不存在：{workshopDirectory}");
        }

        float? decalGroundHeight = null;
        if (values.TryGetValue("--decal-ground", out var groundValue))
        {
            if (!float.TryParse(groundValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedHeight) || !float.IsFinite(parsedHeight))
            {
                throw new ArgumentException("--decal-ground 必须是有限的局部地面高度");
            }

            decalGroundHeight = parsedHeight;
        }

        TreeColorCorrection? treeCorrection = null;
        var preserveTreeColors = false;
        if (values.TryGetValue("--tree-color-mode", out var colorMode))
        {
            preserveTreeColors = colorMode.ToLowerInvariant() switch
            {
                "auto" => false,
                "raw" => true,
                _ => throw new ArgumentException("--tree-color-mode 只接受 auto 或 raw"),
            };
        }
        if (values.ContainsKey("--tree-color-exposure") || values.ContainsKey("--tree-color-saturation"))
        {
            if (values.ContainsKey("--tree-color-mode"))
            {
                throw new ArgumentException("--tree-color-mode 不能与手动颜色校准参数同时使用");
            }
            var exposure = ReadColorParameter(values, "--tree-color-exposure", -16f, 16f);
            var saturation = ReadColorParameter(values, "--tree-color-saturation", 0f, 8f);
            treeCorrection = new TreeColorCorrection(exposure, saturation);
        }

        return new Arguments(crpPath, outputPath, string.IsNullOrWhiteSpace(assetName) ? null : assetName.Trim(),
            workshopDirectory is null ? null : Path.GetFullPath(workshopDirectory), decalGroundHeight, excludedPrefabNames,
            treeCorrection, preserveTreeColors);
    }

    private static float ReadColorParameter(IReadOnlyDictionary<string, string> values, string key, float minimum, float maximum)
    {
        if (!values.TryGetValue(key, out var value) ||
            !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) || !float.IsFinite(parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException($"{key} 必须显式指定 {minimum} 到 {maximum} 之间的有限数值；曝光与饱和度两个参数必须同时提供");
        }

        return parsed;
    }

    private static string GetRequiredPath(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"缺少参数：{key}");
}
