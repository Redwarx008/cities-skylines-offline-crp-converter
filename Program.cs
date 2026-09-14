namespace OfflineCrpConverter;

internal static class Program
{
    private sealed record Arguments(string CrpPath, string OutputPath, string? AssetName);

    public static int Main(string[] args)
    {
        try
        {
            var arguments = ParseArguments(args);
            var package = CrpPackageReader.Read(arguments.CrpPath);
            var outputPath = Path.GetFullPath(arguments.OutputPath);
            var assetName = arguments.AssetName ?? package.MainAssetName;

            if (File.Exists(outputPath))
            {
                throw new IOException($"输出文件已存在，拒绝覆盖：{outputPath}");
            }

            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new ArgumentException($"输出路径没有父目录：{outputPath}");
            Directory.CreateDirectory(outputDirectory);

            var glb = GltfBuilder.Build(package, assetName);
            GlbValidator.Validate(glb, package, assetName);
            File.WriteAllBytes(outputPath, glb);

            Console.WriteLine($"已生成：{outputPath}");
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
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
            {
                throw new ArgumentException(
                    "用法：OfflineCrpConverter --crp <输入.crp> --output <输出.glb> [--name <资产名>]");
            }

            values[args[index]] = args[++index];
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
        return new Arguments(crpPath, outputPath, string.IsNullOrWhiteSpace(assetName) ? null : assetName.Trim());
    }

    private static string GetRequiredPath(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"缺少参数：{key}");
}
