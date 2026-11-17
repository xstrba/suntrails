using System.Collections.Generic;
using Godot;

namespace World;

/// <summary>
/// Loads the MegaKit tree and plant models once (main thread, before any chunk is generated) and gives
/// them the game's own bark and leaf materials. Afterwards the per-model measurements are
/// read-only, so chunk workers can use them for placement.
///
/// Per-instance data for tree MultiMeshes (INSTANCE_CUSTOM): rgb = leaf tint, a = birch bark.
/// One shared material per bark texture and per leaf texture serves every species.
/// </summary>
public static class TreeAssets
{
    public sealed class Model
    {
        public Mesh Mesh;
        public float Height;        // model height in its own units (before species scale)
        public float FrameSize;     // square that frames the whole model, base at y = 0 (impostors)
        public float TrunkRadius;   // for collisions
    }

    static readonly Dictionary<string, Model> Models = new();
    public static bool Loaded { get; private set; }

    public static Model Get(string name) => Models[name];

    public static void Load()
    {
        if (Loaded) return;
        var barkShader = GD.Load<Shader>("res://shaders/tree_bark.gdshader");
        var leafShader = GD.Load<Shader>("res://shaders/tree_leaves.gdshader");
        var materials = new Dictionary<string, ShaderMaterial>();

        ShaderMaterial Bark(string name)
        {
            if (materials.TryGetValue(name, out var m)) return m;
            m = new ShaderMaterial { Shader = barkShader };
            m.SetShaderParameter("albedo_tex", GD.Load<Texture2D>($"res://assets/megakit/{name}.png"));
            m.SetShaderParameter("normal_tex", GD.Load<Texture2D>($"res://assets/megakit/{name}_Normal.png"));
            return materials[name] = m;
        }

        ShaderMaterial Leaves(string name)
        {
            // Use the pack's white leaf textures so every species can tint them.
            string white = name switch
            {
                "Leaves_NormalTree" => "Leaves_NormalTree",
                "Leaves_TwistedTree" => "Leaves_TwistedTree",
                "Leaves_Pine" or "Leaf_Pine" => "Leaf_Pine",
                _ => name,
            };
            if (materials.TryGetValue(white, out var m)) return m;
            m = new ShaderMaterial { Shader = leafShader };
            m.SetShaderParameter("leaf_tex", GD.Load<Texture2D>($"res://assets/megakit/{white}.png"));
            return materials[white] = m;
        }

        var names = new HashSet<string>();
        foreach (var k in TreeKind.All) names.UnionWith(k.Models);
        foreach (var p in PlantKind.All) names.Add(p.Model);
        foreach (var name in names)
        {
            var scene = GD.Load<PackedScene>($"res://assets/megakit/{name}.gltf").Instantiate();
            var mi = FindMesh(scene);
            var mesh = (Mesh)mi.Mesh.Duplicate();
            scene.Free();

            var barkSurfaces = new List<int>();
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                string mat = mesh.SurfaceGetMaterial(s)?.ResourceName ?? "";
                bool bark = mat.StartsWith("Bark");
                if (bark) barkSurfaces.Add(s);
                mesh.SurfaceSetMaterial(s, bark ? Bark(mat) : Leaves(mat));
            }

            // Measurements: overall bounds, and the trunk's width near the ground.
            var aabb = mesh.GetAabb();
            float trunk = 0.15f;
            foreach (int s in barkSurfaces)
                foreach (var v in mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                    if (v.Y < 0.6f) trunk = Mathf.Max(trunk, new Vector2(v.X, v.Z).Length());
            float half = Mathf.Max(Mathf.Max(-aabb.Position.X, aabb.End.X), Mathf.Max(-aabb.Position.Z, aabb.End.Z));
            Models[name] = new Model
            {
                Mesh = mesh,
                Height = aabb.End.Y,
                FrameSize = Mathf.Max(aabb.End.Y, half * 2) * 1.03f,
                // Roots flare out at the very bottom; collide with ~70% of that.
                TrunkRadius = Mathf.Min(trunk * 0.7f, 1.2f),
            };
        }
        Loaded = true;
    }

    static MeshInstance3D FindMesh(Node n)
    {
        if (n is MeshInstance3D mi) return mi;
        foreach (var c in n.GetChildren())
            if (FindMesh(c) is { } found) return found;
        return null;
    }
}
