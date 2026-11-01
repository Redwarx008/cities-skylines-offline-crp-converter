using System.Numerics;

namespace OfflineCrpConverter;

internal static class TangentFrameConverter
{
    public static Vector4[] Convert(CrpMesh mesh, string name, Action<string>? report)
    {
        if (mesh.Tangents.Length != 0 && mesh.Tangents.Length != mesh.Positions.Length)
        {
            throw new InvalidDataException($"网格 {name} 的切线数量与顶点数量不一致");
        }
        var result = new Vector4[mesh.Positions.Length];
        var missing = new bool[result.Length];
        var repaired = 0;
        for (var index = 0; index < result.Length; index++)
        {
            var n = Normal(mesh.Normals[index], name);
            var source = mesh.Tangents.Length == 0 ? Vector4.Zero : mesh.Tangents[index];
            var t = Orthogonalize(new Vector3(source.X, source.Y, source.Z), n);
            if (Finite(t) && t.LengthSquared() >= 1e-12f && float.IsFinite(source.W) && MathF.Abs(source.W) >= .5f)
            {
                result[index] = Reflect(Vector3.Normalize(t), source.W > 0 ? 1 : -1);
            }
            else
            {
                missing[index] = true;
                repaired++;
            }
        }
        if (repaired == 0)
        {
            return result;
        }
        var tangentSum = new Vector3[result.Length];
        var bitangentSum = new Vector3[result.Length];
        foreach (var indices in mesh.SubMeshTriangles)
        {
            for (var offset = 0; offset < indices.Length; offset += 3)
            {
                var a = indices[offset]; var b = indices[offset + 1]; var c = indices[offset + 2];
                if (!missing[a] && !missing[b] && !missing[c])
                {
                    continue;
                }
                var e1 = mesh.Positions[b] - mesh.Positions[a];
                var e2 = mesh.Positions[c] - mesh.Positions[a];
                var u1 = mesh.TextureCoordinates[b] - mesh.TextureCoordinates[a];
                var u2 = mesh.TextureCoordinates[c] - mesh.TextureCoordinates[a];
                var determinant = u1.X * u2.Y - u1.Y * u2.X;
                if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1e-12f ||
                    Vector3.Cross(e1, e2).LengthSquared() < 1e-20f)
                {
                    continue;
                }
                var tangent = (e1 * u2.Y - e2 * u1.Y) / determinant;
                var bitangent = (e2 * u1.X - e1 * u2.X) / determinant;
                if (!Finite(tangent) || !Finite(bitangent))
                {
                    continue;
                }
                Add(a, b, c); Add(b, c, a); Add(c, a, b);
                void Add(int vertex, int next, int previous)
                {
                    if (!missing[vertex])
                    {
                        return;
                    }
                    var x = Vector3.Normalize(mesh.Positions[next] - mesh.Positions[vertex]);
                    var y = Vector3.Normalize(mesh.Positions[previous] - mesh.Positions[vertex]);
                    var angle = MathF.Acos(Math.Clamp(Vector3.Dot(x, y), -1, 1));
                    tangentSum[vertex] += tangent * angle;
                    bitangentSum[vertex] += bitangent * angle;
                }
            }
        }
        var degenerate = 0;
        for (var index = 0; index < result.Length; index++)
        {
            if (!missing[index])
            {
                continue;
            }
            var n = Normal(mesh.Normals[index], name);
            var t = Orthogonalize(tangentSum[index], n);
            if (!Finite(t) || t.LengthSquared() < 1e-12f)
            {
                // Unused vertices / degenerate UVs have no unique texture-space direction.
                // Use a deterministic finite orthogonal frame, never NaN or a parallel tangent.
                var axis = MathF.Abs(n.X) < .9f ? Vector3.UnitX : Vector3.UnitY;
                t = Orthogonalize(axis, n);
                degenerate++;
            }
            t = Vector3.Normalize(t);
            var w = Vector3.Dot(Vector3.Cross(n, t), bitangentSum[index]) < 0 ? -1 : 1;
            result[index] = Reflect(t, w);
        }
        report?.Invoke($"法线切线重建：{name}，修复/生成 {repaired} 个顶点，其中 {degenerate} 个无唯一 UV 方向的顶点使用稳定正交基；原顶点、UV、法线和三角形不变");
        return result;
    }

    private static Vector3 Normal(Vector3 normal, string name)
    {
        if (!Finite(normal) || normal.LengthSquared() < 1e-12f)
        {
            throw new InvalidDataException($"网格 {name} 包含无效顶点法线，不能生成法线贴图切线");
        }
        return Vector3.Normalize(normal);
    }

    private static Vector4 Reflect(Vector3 tangent, float w) => new(tangent.X, tangent.Y, -tangent.Z, -w);
    private static Vector3 Orthogonalize(Vector3 value, Vector3 normal)
    {
        var projected = value - normal * Vector3.Dot(normal, value);
        if (Finite(projected) && projected.LengthSquared() >= 1e-12f &&
            MathF.Abs(Vector3.Dot(normal, Vector3.Normalize(projected))) <= .000001f)
        {
            return projected;
        }
        // Avoid cancellation when a serialized tangent is almost parallel to N.
        var dot = (double)value.X * normal.X + (double)value.Y * normal.Y + (double)value.Z * normal.Z;
        var length = (double)normal.X * normal.X + (double)normal.Y * normal.Y + (double)normal.Z * normal.Z;
        var scale = dot / length;
        return new Vector3((float)(value.X - normal.X * scale),
            (float)(value.Y - normal.Y * scale), (float)(value.Z - normal.Z * scale));
    }
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
