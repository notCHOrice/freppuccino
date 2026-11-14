using System.Text;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Rhino.Display;

namespace Freppuccino.Shaders;

/// Runtime-generated SDF scene from the CSG DAG, raymarched on a fullscreen triangle.
/// Structure (prim kinds, tree shape) is baked into the source; parameters (camera, prims, per-node blend k) are uniforms,
/// so moving primitives or editing blends never recompiles.
/// map(): one local per prim, one per node (creation order = topological, so shared DAG nodes evaluate once),
/// then hard min over parentless prims/nodes. Missing leaves (hidden/deleted/over budget) are dropped at codegen:
/// union skips them, intersect becomes empty, difference with missing base becomes empty.
public static class FrepShaderGen
{
    /// Uniform header = 6 vec4 rows; the rest of Uniforms.MaxBytes is data: prim rows, then blend k's packed 4 per row.
    const int HeaderRows = 6;
    const int MaxDataRows = Uniforms.MaxBytes / 16 - HeaderRows;

    public static int Rows(PrimKind k) => k == PrimKind.Sphere ? 1 : 3;
    static int KRows(int nodes) => (nodes + 3) / 4;
    /// Rows left for prims once every node's k has a slot.
    public static int PrimRowBudget(int nodes) => MaxDataRows - KRows(nodes);

    static readonly Dictionary<string, ShaderSource> cache = [];

    const string Vertex = """
        #version 450
        layout(location = 0) out vec2 vNdc;
        void main()
        {
            vec2 p = vec2(gl_VertexIndex == 1 ? 3.0 : -1.0, gl_VertexIndex == 2 ? 3.0 : -1.0);
            vNdc = p;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <paramref name="ids"/> parallel to <paramref name="prims"/>; prims must fit <see cref="PrimRowBudget"/>.
    /// Returns Program = null if the root is empty. Ks indexed by k slot, for <see cref="SceneUniforms"/>.
    public static (ShaderSource? Program, List<double> Ks) Compile(IReadOnlyList<SdPrim> prims, IReadOnlyList<Guid> ids, FrepTree tree)
    {
        var map = new StringBuilder();
        var index = new Dictionary<Guid, int>();
        int row = 0;
        for (int i = 0; i < prims.Count; i++)
        {
            index[ids[i]] = i;
            var expr = prims[i].Kind == PrimKind.Sphere
                ? $"sdSphere(p, u.data[{row}])"
                : $"sdBox(p, u.data[{row}], u.data[{row + 1}], u.data[{row + 2}])";
            map.Append($"    float p{i} = {expr};\n");
            row += Rows(prims[i].Kind);
        }

        int kBase = row;
        var ks = new List<double>();
        var nodeVar = new Dictionary<int, string?>();
        string? Var(FrepOperand o) => o.IsNode
            ? nodeVar.GetValueOrDefault(o.NodeId)
            : index.TryGetValue(o.ObjectId, out var i) ? $"p{i}" : null;

        foreach (var n in tree.Nodes)
        {
            var terms = n.Operands.Select(Var).ToList();
            List<string>? live = n.Op switch
            {
                FrepOp.Union => [.. terms.OfType<string>()],
                FrepOp.Intersect => terms.Contains(null) ? null : [.. terms.OfType<string>()],
                _ => terms.Count == 0 || terms[0] == null ? null : [.. terms.OfType<string>()],
            };
            string? v = null;
            if (live is { Count: > 0 })
            {
                int slot = ks.Count;
                ks.Add(n.K);
                var k = $"u.data[{kBase + slot / 4}].{"xyzw"[slot % 4]}";
                v = $"n{n.Id}";
                map.Append($"    float {v} = {live[0]};\n");
                foreach (var t in live.Skip(1))
                    map.Append(n.Op switch
                    {
                        FrepOp.Union => $"    {v} = smin({v}, {t}, {k});\n",
                        FrepOp.Intersect => $"    {v} = smax({v}, {t}, {k});\n",
                        _ => $"    {v} = smax({v}, -{t}, {k});\n",
                    });
            }
            nodeVar[n.Id] = v;
        }

        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        var roots = new List<string>();
        for (int i = 0; i < prims.Count; i++)
            if (!referenced.Contains(FrepOperand.Object(ids[i]))) roots.Add($"p{i}");
        foreach (var n in tree.Nodes)
            if (nodeVar[n.Id] is { } v && !referenced.Contains(FrepOperand.Node(n.Id))) roots.Add(v);
        if (roots.Count == 0) return (null, ks);

        map.Append($"    float d = {roots[0]};\n");
        foreach (var r in roots.Skip(1)) map.Append($"    d = min(d, {r});\n");

        int dataRows = kBase + KRows(ks.Count);
        var key = dataRows + "\n" + map;
        if (!cache.TryGetValue(key, out var src))
            cache[key] = src = new ShaderSource(Vertex, Fragment(dataRows, map.ToString()));
        return (src, ks);
    }

    static string Fragment(int dataRows, string map) => $$"""
        #version 450
        layout(location = 0) in vec2 vNdc;
        layout(location = 0) out vec4 outColor;
        layout(std140, set = 0, binding = 0) uniform Frame
        {
            vec4 camPos;     // xyz eye; w = 1 perspective, 0 parallel
            vec4 camX;       // xyz unit right
            vec4 camY;       // xyz unit up
            vec4 camZ;       // xyz unit view direction; w = near
            vec4 frustum;    // left, right, bottom, top (camera coords, at near)
            vec4 params;     // x unused, y = eps per unit t, z = eps const, w = far
            // prims, then node blend k's (4 per row)
            // sphere: 1 row  (xyz center, w radius s)
            // box:    3 rows (xyz center, w b.x), (xyz unit X axis, w b.y), (xyz unit Y axis, w b.z); Z = X × Y
            vec4 data[{{dataRows}}];
        } u;

        const vec3 GREY = vec3(0.78);

        // IQ quadratic smooth min/max; k = blend distance, 0 = hard.
        float smin(float a, float b, float k)
        {
            if (k <= 0.0) return min(a, b);
            float h = max(k - abs(a - b), 0.0) / k;
            return min(a, b) - h * h * k * 0.25;
        }
        float smax(float a, float b, float k) { return -smin(-a, -b, k); }
        float sdSphere(vec3 p, vec4 s) { return length(p - s.xyz) - s.w; }
        float sdBox(vec3 p, vec4 o, vec4 x, vec4 y)
        {
            vec3 d = p - o.xyz;
            vec3 q = abs(vec3(dot(d, x.xyz), dot(d, y.xyz), dot(d, cross(x.xyz, y.xyz)))) - vec3(o.w, x.w, y.w);
            return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
        }
        float map(vec3 p)
        {
        {{map}}    return d;
        }

        void main()
        {
            vec2 f = vNdc * 0.5 + 0.5;
            vec2 xy = mix(u.frustum.xz, u.frustum.yw, f);
            bool persp = u.camPos.w > 0.5;
            float near = u.camZ.w, far = u.params.w;
            vec3 ro, rd; float t, tMax;
            if (persp)
            {
                ro = u.camPos.xyz;
                rd = normalize(u.camX.xyz * xy.x + u.camY.xyz * xy.y + u.camZ.xyz * near);
                float c = dot(rd, u.camZ.xyz); // depth per unit t
                t = near / c; tMax = far / c;
            }
            else
            {
                ro = u.camPos.xyz + u.camX.xyz * xy.x + u.camY.xyz * xy.y;
                rd = u.camZ.xyz;
                t = near; tMax = far;
            }
            float eps = 0.0;
            bool hit = false;
            for (int i = 0; i < 256; i++)
            {
                eps = u.params.z + u.params.y * t;
                float d = map(ro + rd * t);
                if (d < eps) { hit = true; break; }
                t += d;
                if (t > tMax) break;
            }
            if (!hit) discard;
            vec3 p = ro + rd * t;
            const vec2 k = vec2(1.0, -1.0);
            vec3 n = normalize(k.xyy * map(p + k.xyy * eps) + k.yyx * map(p + k.yyx * eps) +
                               k.yxy * map(p + k.yxy * eps) + k.xxx * map(p + k.xxx * eps));
            outColor = vec4(mix(GREY, vec3(1.0), n.z * 0.5 + 0.5), 1.0); // world +Z up: white, -Z down: light grey
        }
        """;

    public static byte[] SceneUniforms(RhinoViewport vp, FrepSceneData scene)
    {
        vp.GetFrustum(out var l, out var r, out var b, out var t, out var near, out var far);
        var persp = vp.IsPerspectiveProjection;
        var px = Math.Max(1, vp.Size.Width);
        var slope = (r - l) / px * 0.5; // half-pixel footprint
        var dir = -vp.CameraZ;
        var u = new Uniforms()
            .Vec4(vp.CameraLocation.X, vp.CameraLocation.Y, vp.CameraLocation.Z, persp ? 1 : 0)
            .Vec4(vp.CameraX.X, vp.CameraX.Y, vp.CameraX.Z)
            .Vec4(vp.CameraY.X, vp.CameraY.Y, vp.CameraY.Z)
            .Vec4(dir.X, dir.Y, dir.Z, near)
            .Vec4(l, r, b, t)
            .Vec4(0, persp ? slope / near : 0, persp ? 0 : slope, far);
        foreach (var (kind, f, p) in scene.Prims)
        {
            if (kind == PrimKind.Sphere)
                u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, p.X);
            else
                u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, p.X)
                 .Vec4(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, p.Y)
                 .Vec4(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, p.Z);
        }
        var ks = scene.Ks;
        for (int i = 0; i < ks.Count; i += 4)
            u.Vec4(ks[i], i + 1 < ks.Count ? ks[i + 1] : 0, i + 2 < ks.Count ? ks[i + 2] : 0, i + 3 < ks.Count ? ks[i + 3] : 0);
        return u.ToBytes();
    }
}
