using System.Text;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Shaders;

/// Runtime-generated SDF scene from the CSG DAG, raymarched on a fullscreen triangle.
/// Structure (prim kinds, tree shape) is baked into the source; parameters (camera, prims, warp frames, per-node blend k) are uniforms,
/// so moving primitives/axes or editing blends/warp params never recompiles.
/// map(): emitted lazily from the roots. A value = (operand, point context): context 0 is p; each warp node opens a new context
/// (its warped point) for its subtree, so a subtree shared by different warps is emitted once per context, else once.
/// Root = hard min over parentless prims/nodes. Missing leaves (hidden prims or nodes/deleted/over budget/warp without its
/// line or box) are dropped at codegen: union skips them, intersect becomes empty, difference with missing base becomes empty,
/// warp of a missing child becomes empty.
public static class FrepShaderGen
{
    /// Uniform header = 6 vec4 rows; the rest of Uniforms.MaxBytes is data: prim rows, warp rows, then blend k's packed 4 per row.
    const int HeaderRows = 6;
    const int MaxDataRows = Uniforms.MaxBytes / 16 - HeaderRows;

    /// Sphere: 1 row (xyz center, w s). Everything else: 3 rows (xyz origin, w P.x), (xyz unit X, w P.y), (xyz unit Y, w P.z).
    public static int Rows(SdKind kind) => kind == SdKind.Sphere ? 1 : 3;
    /// Twist: (start, rate), (dir, extent). Bend: (start, length), (dir, angle), (bend normal, min radius). Repeat: see WarpUniforms.
    static int WarpRows(FrepOp op) => op switch { FrepOp.Twist => 2, FrepOp.Bend => 3, FrepOp.Repeat => 5, _ => 0 };
    static int KRows(int nodes) => (nodes + 3) / 4;
    /// Rows left for prims once every node's k / warp rows have a slot.
    public static int PrimRowBudget(FrepTree tree) =>
        MaxDataRows - KRows(tree.Nodes.Count(n => !n.Op.IsWarp())) - tree.Nodes.Sum(n => n.Hidden ? 0 : WarpRows(n.Op));

    static readonly Dictionary<string, ShaderSource> cache = [];

    /// GLSL call evaluating one primitive whose rows start at <paramref name="row"/> at point <paramref name="p"/>.
    static string Term(SdKind kind, int row, string p)
    {
        if (kind == SdKind.Sphere) return $"sdSphere({p}, u.data[{row}])";
        string o = $"u.data[{row}]", x = $"u.data[{row + 1}]", y = $"u.data[{row + 2}]";
        string q = $"toLocal({p}, {o}, {x}, {y})";
        return kind switch
        {
            SdKind.Box => $"sdBox({q}, vec3({o}.w, {x}.w, {y}.w))",
            SdKind.Torus => $"sdTorus({q}, vec2({o}.w, {x}.w))",
            SdKind.Cylinder => $"sdCappedCylinder({q}, {o}.w, {x}.w)",
            SdKind.Cone => $"sdCappedCone({q}, {o}.w, {x}.w, {y}.w)",
            SdKind.Ellipsoid => $"sdEllipsoid({q}, vec3({o}.w, {x}.w, {y}.w))",
            SdKind.Pyramid => $"sdPyramidScaled({q}, {o}.w, {x}.w)",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

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
    /// <paramref name="liveWarps"/>: warp node ids whose reference geometry resolved. Returns Program = null if the root is empty;
    /// Ks (k slot order) and Warps (warp row order) for <see cref="SceneUniforms"/>.
    public static (ShaderSource? Program, List<double> Ks, List<int> Warps) Compile(
        IReadOnlyList<SdPrimitive> prims, IReadOnlyList<Guid> ids, FrepTree tree, IReadOnlySet<int> liveWarps)
    {
        var index = new Dictionary<Guid, int>();
        var primRow = new int[prims.Count];
        int row = 0;
        for (int i = 0; i < prims.Count; i++)
        {
            index[ids[i]] = i;
            primRow[i] = row;
            row += Rows(prims[i].Kind);
        }

        // Slots are assigned up front (in tree order), so rows are fixed before lazy emission.
        var warpRow = new Dictionary<int, int>();
        var warps = new List<int>();
        foreach (var n in tree.Nodes)
            if (n.Op.IsWarp() && !n.Hidden && liveWarps.Contains(n.Id))
            {
                warpRow[n.Id] = row;
                row += WarpRows(n.Op);
                warps.Add(n.Id);
            }
        int kBase = row;
        var kSlot = new Dictionary<int, int>();
        var ks = new List<double>();
        foreach (var n in tree.Nodes)
            if (!n.Op.IsWarp())
            {
                kSlot[n.Id] = ks.Count;
                ks.Add(n.K);
            }

        var map = new StringBuilder();
        var memo = new Dictionary<(FrepOperand, int), string?>();
        int contexts = 1;
        static string Suffix(int c) => c == 0 ? "" : $"_{c}";
        static string Point(int c) => c == 0 ? "p" : $"q{c}";
        static string Row(int r) => $"u.data[{r}]";

        string? Eval(FrepOperand o, int c)
        {
            if (memo.TryGetValue((o, c), out var v)) return v;
            v = o.IsNode ? EvalNode(o.NodeId, c) : EvalPrim(o.ObjectId, c);
            return memo[(o, c)] = v;
        }

        string? EvalPrim(Guid id, int c)
        {
            if (!index.TryGetValue(id, out var i)) return null;
            var v = $"p{i}{Suffix(c)}";
            map.Append($"    float {v} = {Term(prims[i].Kind, primRow[i], Point(c))};\n");
            return v;
        }

        string? EvalNode(int id, int c)
        {
            if (tree.Find(id) is not { Hidden: false } n) return null;
            var v = $"n{id}{Suffix(c)}";
            if (n.Op.IsWarp())
            {
                if (!warpRow.TryGetValue(id, out var r) || n.Operands.Length == 0) return null;
                int cc = contexts++;
                string p = Point(c), q = Point(cc), l = $"l{cc}";
                map.Append(n.Op switch
                {
                    FrepOp.Twist => $"    float {l}; vec3 {q} = opTwist({p}, {Row(r)}, {Row(r + 1)}, {l});\n",
                    FrepOp.Bend => $"    float {l}; vec3 {q} = opBend({p}, {Row(r)}, {Row(r + 1)}, {Row(r + 2)}, {l});\n",
                    _ => $"    float {l} = 1.0; vec3 {q} = opRepeat({p}, {Row(r)}, {Row(r + 1)}, {Row(r + 2)}, {Row(r + 3)}, {Row(r + 4)});\n",
                });
                if (Eval(n.Operands[0], cc) is not { } child) return null;
                // Twist/bend stretch space; dividing by the local Lipschitz bound keeps steps conservative.
                map.Append($"    float {v} = {child} / {l};\n");
                return v;
            }

            var terms = n.Operands.Select(o => Eval(o, c)).ToList();
            List<string>? live = n.Op switch
            {
                FrepOp.Union => [.. terms.OfType<string>()],
                FrepOp.Intersect => terms.Contains(null) ? null : [.. terms.OfType<string>()],
                _ => terms.Count == 0 || terms[0] == null ? null : [.. terms.OfType<string>()],
            };
            if (live is not { Count: > 0 }) return null;
            int slot = kSlot[id];
            var k = $"u.data[{kBase + slot / 4}].{"xyzw"[slot % 4]}";
            map.Append($"    float {v} = {live[0]};\n");
            foreach (var t in live.Skip(1))
                map.Append(n.Op switch
                {
                    FrepOp.Union => $"    {v} = smin({v}, {t}, {k});\n",
                    FrepOp.Intersect => $"    {v} = smax({v}, {t}, {k});\n",
                    _ => $"    {v} = smax({v}, -{t}, {k});\n",
                });
            return v;
        }

        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        var roots = new List<string>();
        for (int i = 0; i < prims.Count; i++)
            if (!referenced.Contains(FrepOperand.Object(ids[i])) && Eval(FrepOperand.Object(ids[i]), 0) is { } v) roots.Add(v);
        foreach (var n in tree.Nodes)
            if (!referenced.Contains(FrepOperand.Node(n.Id)) && Eval(FrepOperand.Node(n.Id), 0) is { } v) roots.Add(v);
        if (roots.Count == 0) return (null, ks, warps);

        map.Append($"    float d = {roots[0]};\n");
        foreach (var r in roots.Skip(1)) map.Append($"    d = min(d, {r});\n");

        int dataRows = kBase + KRows(ks.Count);
        var key = dataRows + "\n" + map;
        if (!cache.TryGetValue(key, out var src))
            cache[key] = src = new ShaderSource(Vertex, Fragment(dataRows, map.ToString()));
        return (src, ks, warps);
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
            // prims, then warp rows (see opTwist/opBend/opRepeat), then node blend k's (4 per row)
            // sphere: 1 row  (xyz center, w radius s)
            // others: 3 rows (xyz origin, w P.x), (xyz unit X axis, w P.y), (xyz unit Y axis, w P.z); Z = X × Y
            //   box b = P; torus t = P.xy; cylinder h, r; cone h, r1, r2; ellipsoid r = P; pyramid h, s
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
        float dot2(vec2 v) { return dot(v, v); }
        vec3 toLocal(vec3 p, vec4 o, vec4 x, vec4 y)
        {
            vec3 d = p - o.xyz;
            return vec3(dot(d, x.xyz), dot(d, y.xyz), dot(d, cross(x.xyz, y.xyz)));
        }

        // IQ, https://iquilezles.org/articles/distfunctions/ (all in local space, Y-up)
        float sdSphere(vec3 p, vec4 s) { return length(p - s.xyz) - s.w; }
        float sdBox(vec3 p, vec3 b)
        {
            vec3 q = abs(p) - b;
            return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
        }
        float sdTorus(vec3 p, vec2 t)
        {
            vec2 q = vec2(length(p.xz) - t.x, p.y);
            return length(q) - t.y;
        }
        float sdCappedCylinder(vec3 p, float h, float r)
        {
            vec2 d = abs(vec2(length(p.xz), p.y)) - vec2(r, h);
            return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
        }
        float sdCappedCone(vec3 p, float h, float r1, float r2)
        {
            vec2 q = vec2(length(p.xz), p.y);
            vec2 k1 = vec2(r2, h);
            vec2 k2 = vec2(r2 - r1, 2.0 * h);
            vec2 ca = vec2(q.x - min(q.x, (q.y < 0.0) ? r1 : r2), abs(q.y) - h);
            vec2 cb = q - k1 + k2 * clamp(dot(k1 - q, k2) / dot2(k2), 0.0, 1.0);
            float s = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;
            return s * sqrt(min(dot2(ca), dot2(cb)));
        }
        float sdEllipsoid(vec3 p, vec3 r) // bound, not exact
        {
            float k0 = length(p / r);
            float k1 = length(p / (r * r));
            return k1 > 0.0 ? k0 * (k0 - 1.0) / k1 : -min(r.x, min(r.y, r.z));
        }
        float sdPyramid(vec3 p, float h) // unit square base centered at the origin, apex at (0, h, 0)
        {
            float m2 = h * h + 0.25;
            p.xz = abs(p.xz);
            p.xz = (p.z > p.x) ? p.zx : p.xz;
            p.xz -= 0.5;
            vec3 q = vec3(p.z, h * p.y - 0.5 * p.x, h * p.x + 0.5 * p.y);
            float s = max(-q.x, 0.0);
            float t = clamp((q.y - 0.5 * p.z) / (m2 + 0.25), 0.0, 1.0);
            float a = m2 * (q.x + s) * (q.x + s) + q.y * q.y;
            float b = m2 * (q.x + 0.5 * t) * (q.x + 0.5 * t) + (q.y - m2 * t) * (q.y - m2 * t);
            float d2 = min(q.y, -q.x * m2 - q.y * 0.5) > 0.0 ? 0.0 : min(a, b);
            return sqrt((d2 + q.z * q.z) / m2) * sign(max(q.z, -p.y));
        }
        float sdPyramidScaled(vec3 p, float h, float s) { return sdPyramid(p / s, h / s) * s; } // base side s

        // Domain warps: return the point to evaluate the child at; lip = local Lipschitz bound (child distance gets divided by it).
        // Twist: a = (line start, rate rad/unit), d = (unit dir, extent = child radius around the axis, clamps the bound).
        vec3 opTwist(vec3 p, vec4 a, vec4 d, out float lip)
        {
            vec3 v = p - a.xyz;
            float h = dot(v, d.xyz);
            vec3 r = v - d.xyz * h;
            float ang = -a.w * h, c = cos(ang), s = sin(ang);
            float sh = abs(a.w) * min(length(r), d.w);
            lip = 0.5 * (sh + sqrt(sh * sh + 4.0)); // largest singular value of a shear by sh
            return a.xyz + d.xyz * h + r * c + cross(d.xyz, r) * s;
        }
        // Bend: a = (line start, length L), d = (unit dir, total angle >= 0), n = (unit bend direction, min radius for the bound).
        // [0, L] along d bends into an arc toward n; straight (rigid) before the start and past the end.
        vec3 opBend(vec3 p, vec4 a, vec4 d, vec4 n, out float lip)
        {
            lip = 1.0;
            float beta = d.w, L = a.w;
            vec3 v = p - a.xyz;
            float h = dot(v, d.xyz);
            if (beta < 1e-5 || h <= 0.0) return p;
            float y = dot(v, n.xyz);
            vec3 b = v - d.xyz * h - n.xyz * y;
            float R = L / beta;
            vec2 c = vec2(h, R - y); // from the arc center
            float alpha = atan(c.x, c.y), h0, y0;
            if (alpha <= beta)
            {
                float rho = length(c);
                h0 = R * alpha;
                y0 = R - rho;
                lip = max(1.0, R / max(rho, n.w));
            }
            else
            {
                vec2 q = vec2(h, y) - R * vec2(sin(beta), 1.0 - cos(beta)); // from the arc end
                h0 = L + dot(q, vec2(cos(beta), sin(beta)));
                y0 = dot(q, vec2(-sin(beta), cos(beta)));
            }
            return a.xyz + d.xyz * h0 + n.xyz * y0 + b;
        }
        // Limited repetition: o = (anchor = child center, spacing x), x = (unit box X, spacing y), y = (unit box Y, spacing z),
        // lo/hi = copy index range per axis (box axes). Cells are centered on the anchor; the child must fit within one spacing.
        vec3 opRepeat(vec3 p, vec4 o, vec4 x, vec4 y, vec4 lo, vec4 hi)
        {
            vec3 s = vec3(o.w, x.w, y.w);
            vec3 i = clamp(round(toLocal(p, o, x, y) / max(s, vec3(1e-9))), lo.xyz, hi.xyz) * s;
            return p - x.xyz * i.x - y.xyz * i.y - cross(x.xyz, y.xyz) * i.z;
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
        // Same order as Compile(): prim rows, warp rows, then k's.
        foreach (var (kind, f, v) in scene.Primitives)
        {
            u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, v.X);
            if (kind == SdKind.Sphere) continue;
            u.Vec4(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, v.Y)
             .Vec4(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, v.Z);
        }
        foreach (var w in scene.Warps) WarpUniforms(u, w);
        var ks = scene.Ks;
        for (int i = 0; i < ks.Count; i += 4)
            u.Vec4(ks[i], i + 1 < ks.Count ? ks[i + 1] : 0, i + 2 < ks.Count ? ks[i + 2] : 0, i + 3 < ks.Count ? ks[i + 3] : 0);
        return u.ToBytes();
    }

    static void WarpUniforms(Uniforms u, SdWarp w)
    {
        var a = w.Axis.From;
        var d = w.Axis.Direction;
        double len = d.Length;
        d.Unitize();
        switch (w.Op)
        {
            case FrepOp.Twist:
                u.Vec4(a.X, a.Y, a.Z, Rhino.RhinoMath.ToRadians(w.P.X) / len)
                 .Vec4(d.X, d.Y, d.Z, w.Extent);
                break;
            case FrepOp.Bend:
            {
                double beta = Rhino.RhinoMath.ToRadians(w.P.X), phi = Rhino.RhinoMath.ToRadians(w.P.Y);
                var n0 = BendReference(d);
                var n = n0 * Math.Cos(phi) + Vector3d.CrossProduct(d, n0) * Math.Sin(phi);
                if (beta < 0) (n, beta) = (-n, -beta);
                double radius = beta > 1e-9 ? len / beta : double.MaxValue / 4;
                u.Vec4(a.X, a.Y, a.Z, len)
                 .Vec4(d.X, d.Y, d.Z, beta)
                 .Vec4(n.X, n.Y, n.Z, Math.Max(radius - w.Extent, 0.05 * radius));
                break;
            }
            case FrepOp.Repeat:
            {
                var f = w.Region.Plane;
                var c = w.Anchor;
                f.RemapToPlaneSpace(c, out var cl);
                var s = w.P;
                (double lo, double hi) Range(double sp, Interval iv, double at) => sp <= 0 ? (0, 0)
                    : (Math.Min(0, Math.Ceiling((iv.Min - at) / sp - 1e-9)), Math.Max(0, Math.Floor((iv.Max - at) / sp + 1e-9)));
                var (x0, x1) = Range(s.X, w.Region.X, cl.X);
                var (y0, y1) = Range(s.Y, w.Region.Y, cl.Y);
                var (z0, z1) = Range(s.Z, w.Region.Z, cl.Z);
                u.Vec4(c.X, c.Y, c.Z, s.X)
                 .Vec4(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, s.Y)
                 .Vec4(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, s.Z)
                 .Vec4(x0, y0, z0)
                 .Vec4(x1, y1, z1);
                break;
            }
        }
    }

    /// Bend direction 0°: world +Z projected perpendicular to the axis (world +X for a vertical axis).
    static Vector3d BendReference(Vector3d d)
    {
        var r = Math.Abs(d.Z) > 0.99 ? Vector3d.XAxis : Vector3d.ZAxis;
        r -= d * (r * d);
        r.Unitize();
        return r;
    }
}

