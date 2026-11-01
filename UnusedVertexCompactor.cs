using System.Numerics;

namespace OfflineCrpConverter;

internal static class UnusedVertexCompactor
{
    public static CrpMesh Apply(CrpMesh mesh, string name, Action<string>? report)
    {
        var used = mesh.SubMeshTriangles.SelectMany(static slot => slot).Distinct().Order().ToArray();
        if (used.Length == 0 || used.Length == mesh.Positions.Length)
        {
            return mesh;
        }
        var low = new Vector3(float.PositiveInfinity);
        var high = new Vector3(float.NegativeInfinity);
        foreach (var index in used)
        {
            low = Vector3.Min(low, mesh.Positions[index]);
            high = Vector3.Max(high, mesh.Positions[index]);
        }
        var size = high - low;
        var epsilon = MathF.Max(1, MathF.Max(size.X, MathF.Max(size.Y, size.Z))) * .00001f;
        if (!mesh.Positions.Any(p => p.X < low.X - epsilon || p.X > high.X + epsilon ||
            p.Y < low.Y - epsilon || p.Y > high.Y + epsilon || p.Z < low.Z - epsilon || p.Z > high.Z + epsilon))
        {
            return mesh;
        }
        var map = new int[mesh.Positions.Length];
        for (var index = 0; index < used.Length; index++)
        {
            map[used[index]] = index;
        }
        report?.Invoke($"清理无引用越界顶点：{name}，移除 {mesh.Positions.Length - used.Length} 个非渲染顶点，保留全部三角形，避免静态预览包围盒被撑大");
        return new CrpMesh(used.Select(index => mesh.Positions[index]).ToArray(),
            mesh.Colors.Length == 0 ? [] : used.Select(index => mesh.Colors[index]).ToArray(),
            used.Select(index => mesh.TextureCoordinates[index]).ToArray(),
            used.Select(index => mesh.Normals[index]).ToArray(),
            mesh.Tangents.Length == 0 ? [] : used.Select(index => mesh.Tangents[index]).ToArray(),
            mesh.SubMeshTriangles.Select(indices => indices.Select(index => map[index]).ToArray()).ToArray());
    }
}
