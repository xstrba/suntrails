using System;
using System.Collections.Generic;
using Godot;

namespace World;

/// <summary>
/// Small CPU-side mesh builder mirroring the three.js primitives the web version used
/// (PlaneGeometry, CylinderGeometry, IcosahedronGeometry, mergeGeometries,
/// computeVertexNormals). Triangles are stored counter-clockwise like three.js and
/// flipped to Godot's clockwise front faces in <see cref="ToArrayMesh"/>.
/// UVs use Godot's convention (v = 0 at the top of the image).
/// </summary>
public sealed class MeshBuilder
{
    public readonly List<Vector3> Positions = new();
    public readonly List<Vector3> Normals = new();
    public readonly List<Vector2> UVs = new();
    public readonly List<Vector2> UV2s = new();
    public readonly List<Color> Colors = new();
    public readonly List<Plane> Tangents = new(); // xyz = tangent, D = binormal sign (Godot's w)
    public readonly List<int> Indices = new();

    public int VertexCount => Positions.Count;

    public static MeshBuilder Plane(float width, float height, int segX = 1, int segY = 1)
    {
        var m = new MeshBuilder();
        int gx1 = segX + 1;
        for (int iy = 0; iy <= segY; iy++)
        {
            float y = iy * height / segY - height / 2;
            for (int ix = 0; ix <= segX; ix++)
            {
                float x = ix * width / segX - width / 2;
                m.Positions.Add(new Vector3(x, -y, 0));
                m.Normals.Add(new Vector3(0, 0, 1));
                m.UVs.Add(new Vector2(ix / (float)segX, iy / (float)segY));
            }
        }
        for (int iy = 0; iy < segY; iy++)
            for (int ix = 0; ix < segX; ix++)
            {
                int a = ix + gx1 * iy, b = ix + gx1 * (iy + 1), c = ix + 1 + gx1 * (iy + 1), d = ix + 1 + gx1 * iy;
                m.Indices.AddRange(new[] { a, b, d, b, c, d });
            }
        return m;
    }

    /// <summary>Open-ended cylinder centred on the origin along +Y (caps are never visible on trees).</summary>
    public static MeshBuilder Cylinder(float radiusTop, float radiusBottom, float height, int radial, int heightSegs = 1)
    {
        var m = new MeshBuilder();
        float slope = (radiusBottom - radiusTop) / height;
        for (int y = 0; y <= heightSegs; y++)
        {
            float v = y / (float)heightSegs;
            float r = v * (radiusBottom - radiusTop) + radiusTop;
            for (int x = 0; x <= radial; x++)
            {
                float u = x / (float)radial;
                float theta = u * Mathf.Tau;
                float s = Mathf.Sin(theta), c = Mathf.Cos(theta);
                m.Positions.Add(new Vector3(r * s, -v * height + height / 2, r * c));
                m.Normals.Add(new Vector3(s, slope, c).Normalized());
                m.UVs.Add(new Vector2(u, v));
            }
        }
        int row = radial + 1;
        for (int x = 0; x < radial; x++)
            for (int y = 0; y < heightSegs; y++)
            {
                int a = y * row + x, b = (y + 1) * row + x, c = (y + 1) * row + x + 1, d = y * row + x + 1;
                m.Indices.AddRange(new[] { a, b, d, b, c, d });
            }
        return m;
    }

    /// <summary>Welded icosphere subdivided like three.js IcosahedronGeometry(radius, detail).</summary>
    public static MeshBuilder Icosphere(float radius, int detail)
    {
        float t = (1 + Mathf.Sqrt(5)) / 2;
        var baseV = new Vector3[]
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        int[] faces =
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };
        var m = new MeshBuilder();
        var lookup = new Dictionary<(int, int, int), int>();
        int Weld(Vector3 p)
        {
            p = p.Normalized() * radius;
            var key = ((int)Mathf.Round(p.X * 1e4f), (int)Mathf.Round(p.Y * 1e4f), (int)Mathf.Round(p.Z * 1e4f));
            if (!lookup.TryGetValue(key, out int index))
            {
                index = m.Positions.Count;
                m.Positions.Add(p);
                lookup[key] = index;
            }
            return index;
        }

        int cols = detail + 1;
        for (int f = 0; f < faces.Length; f += 3)
        {
            Vector3 a = baseV[faces[f]], b = baseV[faces[f + 1]], c = baseV[faces[f + 2]];
            var v = new int[cols + 1][];
            for (int i = 0; i <= cols; i++)
            {
                var aj = a.Lerp(c, i / (float)cols);
                var bj = b.Lerp(c, i / (float)cols);
                int rows = cols - i;
                v[i] = new int[rows + 1];
                for (int j = 0; j <= rows; j++)
                    v[i][j] = Weld(j == 0 && i == cols ? aj : aj.Lerp(bj, j / (float)rows));
            }
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < 2 * (cols - i) - 1; j++)
                {
                    int k = j / 2;
                    if (j % 2 == 0) m.Indices.AddRange(new[] { v[i][k + 1], v[i + 1][k], v[i][k] });
                    else m.Indices.AddRange(new[] { v[i][k + 1], v[i + 1][k + 1], v[i + 1][k] });
                }
        }
        // Make every triangle face outward (counter-clockwise seen from outside).
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            Vector3 p0 = m.Positions[m.Indices[i]], p1 = m.Positions[m.Indices[i + 1]], p2 = m.Positions[m.Indices[i + 2]];
            if ((p1 - p0).Cross(p2 - p0).Dot(p0 + p1 + p2) < 0)
                (m.Indices[i + 1], m.Indices[i + 2]) = (m.Indices[i + 2], m.Indices[i + 1]);
        }
        foreach (var _ in m.Positions) m.UVs.Add(Vector2.Zero);
        m.ComputeNormals();
        return m;
    }

    public MeshBuilder Translate(float x, float y, float z) => Transform(new Transform3D(Basis.Identity, new Vector3(x, y, z)));

    public MeshBuilder Rotate(Basis basis) => Transform(new Transform3D(basis, Vector3.Zero));

    /// <summary>Applies a rigid transform (rotation + translation) to positions and normals.</summary>
    public MeshBuilder Transform(Transform3D xf)
    {
        for (int i = 0; i < Positions.Count; i++)
        {
            Positions[i] = xf * Positions[i];
            if (i < Normals.Count) Normals[i] = (xf.Basis * Normals[i]).Normalized();
        }
        return this;
    }

    /// <summary>Area-weighted vertex normals, like three.js computeVertexNormals on indexed geometry.</summary>
    public MeshBuilder ComputeNormals()
    {
        var acc = new Vector3[Positions.Count];
        for (int i = 0; i < Indices.Count; i += 3)
        {
            int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
            var n = (Positions[c] - Positions[b]).Cross(Positions[a] - Positions[b]);
            acc[a] += n; acc[b] += n; acc[c] += n;
        }
        Normals.Clear();
        foreach (var n in acc) Normals.Add(n.LengthSquared() > 0 ? n.Normalized() : Vector3.Up);
        return this;
    }

    /// <summary>Gives every triangle its own vertices (needed for creased normals).</summary>
    public MeshBuilder Unweld()
    {
        var m = new MeshBuilder();
        foreach (int i in Indices)
        {
            m.Indices.Add(m.Positions.Count);
            m.Positions.Add(Positions[i]);
            if (i < UVs.Count) m.UVs.Add(UVs[i]);
        }
        return m;
    }

    /// <summary>
    /// Smooth normals across edges sharper than <paramref name="creaseDegrees"/> stay hard.
    /// Expects unwelded triangles; corners sharing a position are smoothed together.
    /// </summary>
    public MeshBuilder ComputeNormalsCreased(float creaseDegrees)
    {
        int tris = Indices.Count / 3;
        var faceN = new Vector3[tris];
        var byPos = new Dictionary<(int, int, int), List<int>>();
        static (int, int, int) Key(Vector3 p) => ((int)Mathf.Round(p.X * 1e4f), (int)Mathf.Round(p.Y * 1e4f), (int)Mathf.Round(p.Z * 1e4f));
        for (int t = 0; t < tris; t++)
        {
            Vector3 a = Positions[Indices[t * 3]], b = Positions[Indices[t * 3 + 1]], c = Positions[Indices[t * 3 + 2]];
            faceN[t] = (c - b).Cross(a - b); // area-weighted, three.js winding
            for (int k = 0; k < 3; k++)
            {
                var key = Key(Positions[Indices[t * 3 + k]]);
                if (!byPos.TryGetValue(key, out var list)) byPos[key] = list = new List<int>();
                list.Add(t);
            }
        }
        float cosCrease = Mathf.Cos(Mathf.DegToRad(creaseDegrees));
        var normals = new Vector3[Positions.Count];
        for (int t = 0; t < tris; t++)
        {
            var own = faceN[t].Normalized();
            for (int k = 0; k < 3; k++)
            {
                int v = Indices[t * 3 + k];
                var sum = Vector3.Zero;
                foreach (int o in byPos[Key(Positions[v])])
                    if (faceN[o].Normalized().Dot(own) >= cosCrease) sum += faceN[o];
                normals[v] = sum.LengthSquared() > 0 ? sum.Normalized() : own;
            }
        }
        Normals.Clear();
        Normals.AddRange(normals);
        return this;
    }

    public MeshBuilder Append(MeshBuilder other)
    {
        int offset = Positions.Count;
        Positions.AddRange(other.Positions);
        Normals.AddRange(other.Normals);
        UVs.AddRange(other.UVs);
        UV2s.AddRange(other.UV2s);
        Colors.AddRange(other.Colors);
        Tangents.AddRange(other.Tangents);
        foreach (int i in other.Indices) Indices.Add(i + offset);
        return this;
    }

    public static MeshBuilder Merge(IEnumerable<MeshBuilder> parts)
    {
        var m = new MeshBuilder();
        foreach (var p in parts) m.Append(p);
        return m;
    }

    public ArrayMesh ToArrayMesh()
    {
        var mesh = new ArrayMesh();
        AddSurfaceTo(mesh, null);
        return mesh;
    }

    /// <summary>Adds this geometry as a new surface of <paramref name="mesh"/> (several surfaces = several materials).</summary>
    public void AddSurfaceTo(ArrayMesh mesh, Material material)
    {
        if (Indices.Count == 0) return;
        var indices = new int[Indices.Count];
        for (int i = 0; i < Indices.Count; i += 3)
        {
            indices[i] = Indices[i];
            indices[i + 1] = Indices[i + 2];
            indices[i + 2] = Indices[i + 1];
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Positions.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = Normals.ToArray();
        if (Tangents.Count == Positions.Count)
        {
            var t = new float[Tangents.Count * 4];
            for (int i = 0; i < Tangents.Count; i++)
            {
                t[i * 4] = Tangents[i].Normal.X; t[i * 4 + 1] = Tangents[i].Normal.Y;
                t[i * 4 + 2] = Tangents[i].Normal.Z; t[i * 4 + 3] = Tangents[i].D;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = t;
        }
        if (Colors.Count == Positions.Count) arrays[(int)Mesh.ArrayType.Color] = Colors.ToArray();
        if (UVs.Count == Positions.Count) arrays[(int)Mesh.ArrayType.TexUV] = UVs.ToArray();
        if (UV2s.Count == Positions.Count) arrays[(int)Mesh.ArrayType.TexUV2] = UV2s.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        if (material != null) mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    /// <summary>Rotation equal to a three.js Euler in its default 'XYZ' order.</summary>
    public static Basis EulerXYZ(float x, float y, float z) =>
        new Basis(Vector3.Right, x) * new Basis(Vector3.Up, y) * new Basis(Vector3.Back, z);

    /// <summary>Shortest rotation taking unit vector <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Basis FromTo(Vector3 from, Vector3 to)
    {
        var axis = from.Cross(to);
        float dot = Math.Clamp(from.Dot(to), -1f, 1f);
        if (axis.LengthSquared() < 1e-12f)
            return dot > 0 ? Basis.Identity : new Basis(Vector3.Right, Mathf.Pi);
        return new Basis(axis.Normalized(), Mathf.Acos(dot));
    }
}
