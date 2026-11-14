using System.Drawing;
using System.Text;
using Freppuccino.Csg;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Shaders;

/// Runtime-generated SDF scene from the CSG DAG, raymarched on a fullscreen triangle.
/// Structure (prim kinds, tree shape) is baked into the source; parameters (camera, prims, colors, warp frames, per-node blend k)
/// are uniforms, so moving primitives/axes or editing blends/warp params/colors never recompiles.
/// map(): emitted lazily from the roots. A value = (operand, point context): context 0 is p; each warp node opens a new context
/// (its warped point) for its subtree, so a subtree shared by different warps is emitted once per context, else once.
/// Root = hard min over parentless prims/nodes. Missing leaves (hidden prims or nodes/deleted/over budget) are dropped at
/// codegen: union skips them, intersect becomes empty, difference with missing base becomes empty, warp of a missing child
/// becomes empty. A warp whose line/box is gone passes its child through unwarped.
/// sceneColor(): the same DAG over vec4(rgb, d), evaluated once at the hit point. Union/intersect blend operand colors with
/// the smin/smax weight (a smooth union's fillet fades between them); difference keeps the base's color; warps keep the child's.
public static class FrepShaderGen
{
    /// Uniform header = 8 vec4 rows; the rest of Uniforms.MaxBytes is data: prim rows, prim colors packed 4 per row,
    /// warp rows, then blend k's packed 4 per row.
    const int HeaderRows = 8;
    const int MaxDataRows = Uniforms.MaxBytes / 16 - HeaderRows;

    /// Sphere: 1 row (xyz center, w s). Everything else: 3 rows (xyz origin, w P.x), (xyz unit X, w P.y), (xyz unit Y, w P.z).
    public static int Rows(SdKind kind) => kind == SdKind.Sphere ? 1 : 3;
    /// Twist: (start, rate), (dir, extent). Bend: (start, length), (dir, angle), (bend normal, min radius). Repeat: see WarpUniforms.
    static int WarpRows(FrepOp op) => op switch { FrepOp.Twist => 2, FrepOp.Bend => 3, FrepOp.Repeat => 5, _ => 0 };
    static int KRows(int nodes) => (nodes + 3) / 4;
    /// Rows holding the colors of <paramref name="prims"/> primitives (one packed 0xRRGGBB float each, 4 per row).
    public static int ColorRows(int prims) => (prims + 3) / 4;
    /// Rows left for prims (geometry + color rows) once every node's k / warp rows have a slot.
    public static int PrimRowBudget(FrepTree tree) =>
        MaxDataRows - KRows(tree.Nodes.Count(n => !n.Op.IsWarp())) - tree.Nodes.Sum(n => n.Hidden ? 0 : WarpRows(n.Op));

    /// 0xRRGGBB as a float: exact, since float32 holds every integer below 2^24. Unpacked by unpackColor() in the shader.
    static double PackColor(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    /// Shapes are drawn this many screen pixels inside their true outline, so Rhino's wireframe edges show around them.
    /// A uniform, so changing it never recompiles.
    public const double SilhouetteInsetPx = 1.0;

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
        // Prim colors right after the prim rows.
        int cBase = row;
        row += ColorRows(prims.Count);

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

        // map: float distances. color: the same DAG over vec4(rgb, d) (same variable names), so the shaded color follows the blend.
        var map = new StringBuilder();
        var color = new StringBuilder();
        void Both(string line) { map.Append(line); color.Append(line); }
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
            var term = Term(prims[i].Kind, primRow[i], Point(c));
            map.Append($"    float {v} = {term};\n");
            color.Append($"    vec4 {v} = vec4(unpackColor(u.data[{cBase + i / 4}].{"xyzw"[i % 4]}), {term});\n");
            return v;
        }

        string? EvalNode(int id, int c)
        {
            if (tree.Find(id) is not { Hidden: false } n) return null;
            var v = $"n{id}{Suffix(c)}";
            if (n.Op.IsWarp())
            {
                if (n.Operands.Length == 0) return null;
                if (!warpRow.TryGetValue(id, out var r)) return Eval(n.Operands[0], c); // line/box deleted: unwarped
                int cc = contexts++;
                string p = Point(c), q = Point(cc), l = $"l{cc}";
                Both(n.Op switch
                {
                    FrepOp.Twist => $"    float {l}; vec3 {q} = opTwist({p}, {Row(r)}, {Row(r + 1)}, {l});\n",
                    FrepOp.Bend => $"    float {l}; vec3 {q} = opBend({p}, {Row(r)}, {Row(r + 1)}, {Row(r + 2)}, {l});\n",
                    _ => $"    float {l} = 1.0; vec3 {q} = opRepeat({p}, {Row(r)}, {Row(r + 1)}, {Row(r + 2)}, {Row(r + 3)}, {Row(r + 4)});\n",
                });
                if (Eval(n.Operands[0], cc) is not { } child) return null;
                // Twist/bend stretch space; dividing by the local Lipschitz bound keeps steps conservative. Color = child's.
                map.Append($"    float {v} = {child} / {l};\n");
                color.Append($"    vec4 {v} = vec4({child}.rgb, {child}.w / {l});\n");
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
            color.Append($"    vec4 {v} = {live[0]};\n");
            foreach (var t in live.Skip(1))
            {
                map.Append(n.Op switch
                {
                    FrepOp.Union => $"    {v} = smin({v}, {t}, {k});\n",
                    FrepOp.Intersect => $"    {v} = smax({v}, {t}, {k});\n",
                    _ => $"    {v} = smax({v}, -{t}, {k});\n",
                });
                color.Append(n.Op switch
                {
                    FrepOp.Union => $"    {v} = cmin({v}, {t}, {k});\n",
                    FrepOp.Intersect => $"    {v} = cmax({v}, {t}, {k});\n",
                    _ => $"    {v} = csub({v}, {t}, {k});\n",
                });
            }
            // FZebra: the hit point lies on this union's surface (incl. its blend fillets).
            if (n.Op == FrepOp.Union) color.Append($"    zeb = max(zeb, 1.0 - step(tol, abs({v}.w)));\n");
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
        color.Append($"    vec4 c = {roots[0]};\n");
        foreach (var r in roots.Skip(1))
        {
            map.Append($"    d = min(d, {r});\n");
            color.Append($"    c = cmin(c, {r}, 0.0);\n");
        }

        int dataRows = kBase + KRows(ks.Count);
        var key = dataRows + "\n" + map + "\n" + color;
        if (!cache.TryGetValue(key, out var src))
            cache[key] = src = new ShaderSource(Vertex, Fragment(dataRows, map.ToString(), color.ToString()));
        return (src, ks, warps);
    }

    static string Fragment(int dataRows, string map, string color) => $$"""
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
            vec4 params;     // x = silhouette inset (screen px), y = eps per unit t, z = eps const, w unused
            vec4 bound;      // xyz center, w radius: sphere enclosing the (warped) scene + smooth-union growth; w <= 0 = empty
            vec4 zebraCfg;   // FZebra on unions: x = stripe count (0 = off), y = 1 vertical / 0 horizontal, z/w = colors (0xRRGGBB)
            // prims, then prim colors (packed 0xRRGGBB floats, 4 per row), then warp rows (see opTwist/opBend/opRepeat),
            // then node blend k's (4 per row)
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

        vec3 unpackColor(float packed)
        {
            uint c = uint(packed + 0.5);
            return vec3(float((c >> 16) & 255u), float((c >> 8) & 255u), float(c & 255u)) / 255.0;
        }
        // Same ops over vec4(rgb, d). Colors mix with IQ's smin weight, so they fade across the blend region
        // (|a - b| < k) and are pure away from it; k = 0 picks the nearer operand's color.
        vec4 cmin(vec4 a, vec4 b, float k)
        {
            if (k <= 0.0) return a.w < b.w ? a : b;
            float h = clamp(0.5 + 0.5 * (b.w - a.w) / k, 0.0, 1.0);
            return vec4(mix(b.rgb, a.rgb, h), smin(a.w, b.w, k));
        }
        vec4 cmax(vec4 a, vec4 b, float k)
        {
            if (k <= 0.0) return a.w > b.w ? a : b;
            float h = clamp(0.5 + 0.5 * (a.w - b.w) / k, 0.0, 1.0);
            return vec4(mix(b.rgb, a.rgb, h), smax(a.w, b.w, k));
        }
        vec4 csub(vec4 a, vec4 b, float k) { return vec4(a.rgb, smax(a.w, -b.w, k)); } // cut faces keep the base's color
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
        // Not IQ's sdEllipsoid: its value at the centre depends on the direction of approach, which breaks
        // smooth CSG and meshing. This has the exact surface and changes by at most 1 per unit distance,
        // but it underestimates distance. Keep in sync with Meshing/FrepField.cs.
        float sdEllipsoid(vec3 p, vec3 r) // unit sphere scaled by r, times the smallest semi-axis
        {
            return (length(p / r) - 1.0) * min(r.x, min(r.y, r.z));
        }
        // Unit square base centered at the origin, apex at (0, h, 0).
        // IQ's version measures distance to the four slanted faces only; the base enters just through the sign.
        // Below the base that overestimates (the ray steps through the floor and stops inside), and just above it
        // the interior distance ignores the floor (normals there point at the side faces). Both patched at the end.
        float sdPyramid(vec3 p, float h)
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
            float d = sqrt((d2 + q.z * q.z) / m2) * sign(max(q.z, -p.y));
            // Below the base every point of the solid sits above the base square, so the base is nearest (exact).
            // p.xz here is already abs()'d and shifted by -0.5, so max(p.xz, 0) is the overhang past the square.
            if (p.y < 0.0) return length(vec3(max(p.x, 0.0), p.y, max(p.z, 0.0)));
            return max(d, -p.y); // inside: floor may be closer than the side faces; outside (d > 0): unchanged
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
        // Only evaluated once per hit pixel, so the hot march loop stays float-only.
        // zeb = 1 if p is within tol of a union node's surface (FZebra), else 0.
        vec3 sceneColor(vec3 p, float tol, out float zeb)
        {
            zeb = 0.0;
        {{color}}    return c.rgb;
        }
        // FZebra (Rhino-style): bands of the reflected view direction, measured in the camera frame so the stripes
        // follow the view. Vertical stripes vary with the reflection's left/right component, horizontal with up/down.
        vec3 zebra(vec3 rd, vec3 n)
        {
            vec3 r = reflect(rd, n);
            float s = u.zebraCfg.y > 0.5 ? dot(r, u.camX.xyz) : dot(r, u.camY.xyz);
            float a = acos(clamp(s, -1.0, 1.0)) / 3.14159265 * u.zebraCfg.x;
            return step(0.5, fract(a)) > 0.5 ? unpackColor(u.zebraCfg.w) : unpackColor(u.zebraCfg.z);
        }

        void main()
        {
            vec2 f = vNdc * 0.5 + 0.5;
            vec2 xy = mix(u.frustum.xz, u.frustum.yw, f);
            bool persp = u.camPos.w > 0.5;
            float near = u.camZ.w;
            vec3 ro, rd;
            if (persp)
            {
                ro = u.camPos.xyz;
                rd = normalize(u.camX.xyz * xy.x + u.camY.xyz * xy.y + u.camZ.xyz * near);
            }
            else
            {
                ro = u.camPos.xyz + u.camX.xyz * xy.x + u.camY.xyz * xy.y;
                rd = u.camZ.xyz;
            }
            // March only through the scene's bounding sphere, not Rhino's near/far: Rhino moves its clip planes
            // around while zooming, and starting a ray past the front of a surface lands inside the SDF (instant
            // hit, interior-gradient normals, i.e. the shape looks sliced).
            vec3 oc = ro - u.bound.xyz;
            float bb = dot(oc, rd);
            vec3 perp = oc - bb * rd; // closest approach; avoids |oc|^2 - r^2 cancellation when zoomed far out
            float disc = u.bound.w * u.bound.w - dot(perp, perp);
            if (u.bound.w <= 0.0 || disc < 0.0) discard;
            float h = sqrt(disc);
            float t = -bb - h, tMax = -bb + h;
            if (persp) t = max(t, 0.0); // nothing behind the eye
            if (tMax < t) discard;
            float eps = 0.0;
            bool hit = false;
            for (int i = 0; i < 256; i++)
            {
                eps = u.params.z + u.params.y * t; // half-pixel footprint at t
                // Shrink the surface by params.x pixels so Rhino's wires stay visible around the shading.
                float d = map(ro + rd * t) + u.params.x * 2.0 * eps;
                if (d < eps) { hit = true; break; }
                t += d;
                if (t > tMax) break;
            }
            if (!hit) discard;
            vec3 p = ro + rd * t;
            const vec2 k = vec2(1.0, -1.0);
            vec3 n = normalize(k.xyy * map(p + k.xyy * eps) + k.yyx * map(p + k.yyx * eps) +
                               k.yxy * map(p + k.yxy * eps) + k.xxx * map(p + k.xxx * eps));
            // Object display color, lit as before: world +Z up = full color, -Z down = GREY x color.
            // The hit sits up to ~(1 + 2 x inset) eps inside the true surface; tol covers that with margin.
            float zeb;
            vec3 col = sceneColor(p, eps * (4.0 + 2.0 * u.params.x), zeb);
            // Zebra stripes are unlit (like Rhino's), so their edges read cleanly.
            if (u.zebraCfg.x > 0.0 && zeb > 0.5) { outColor = vec4(zebra(rd, n), 1.0); return; }
            outColor = vec4(col * mix(GREY, vec3(1.0), n.z * 0.5 + 0.5), 1.0);
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
            .Vec4(SilhouetteInsetPx, persp ? slope / near : 0, persp ? 0 : slope, 0);
        var (center, radius) = Bound(scene);
        u.Vec4(center.X, center.Y, center.Z, radius);
        u.Vec4(Display.FrepSettings.Zebra ? Math.Max(1, Display.FrepSettings.ZebraStripes) : 0,
            Display.FrepSettings.ZebraVertical ? 1 : 0,
            PackColor(Display.FrepSettings.ZebraColor1), PackColor(Display.FrepSettings.ZebraColor2));
        // Same order as Compile(): prim rows, colors, warp rows, then k's.
        foreach (var (kind, f, v) in scene.Primitives)
        {
            u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, v.X);
            if (kind == SdKind.Sphere) continue;
            u.Vec4(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, v.Y)
             .Vec4(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, v.Z);
        }
        var cs = scene.Colors;
        double C(int i) => i < cs.Count ? PackColor(cs[i]) : 0;
        for (int i = 0; i < scene.Primitives.Count; i += 4)
            u.Vec4(C(i), C(i + 1), C(i + 2), C(i + 3));
        foreach (var w in scene.Warps) WarpUniforms(u, w);
        var ks = scene.Ks;
        for (int i = 0; i < ks.Count; i += 4)
            u.Vec4(ks[i], i + 1 < ks.Count ? ks[i + 1] : 0, i + 2 < ks.Count ? ks[i + 2] : 0, i + 3 < ks.Count ? ks[i + 3] : 0);
        return u.ToBytes();
    }

    static void WarpUniforms(Uniforms u, SdWarp w)
    {
        foreach (var r in FrepWarpMath.Rows(w)) u.Vec4(r.X, r.Y, r.Z, r.W);
    }

    static double PrimRadius(SdPrimitive p)
    {
        var v = p.P;
        return p.Kind switch
        {
            SdKind.Sphere => Math.Abs(v.X),
            SdKind.Box => new Vector3d(v.X, v.Y, v.Z).Length,
            SdKind.Torus => Math.Abs(v.X) + Math.Abs(v.Y),
            SdKind.Cylinder => Math.Sqrt(v.X * v.X + v.Y * v.Y),
            SdKind.Cone => Math.Sqrt(v.X * v.X + Math.Max(v.Y * v.Y, v.Z * v.Z)),
            SdKind.Ellipsoid => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))),
            SdKind.Pyramid => Math.Sqrt(v.X * v.X + 0.5 * v.Y * v.Y), // origin at base center, apex at +h
            _ => 0,
        };
    }

    /// Conservative world-space bounding sphere of the drawn scene, from the in-memory prims/warps (follows gumball drags).
    /// Walks the tree like Compile(): prims → spheres, nodes union their operands, live warps grow their child's box.
    static (Point3d Center, double Radius) Bound(FrepSceneData scene)
    {
        var tree = scene.Tree;
        var prim = new Dictionary<Guid, int>();
        for (int i = 0; i < scene.Ids.Count; i++) prim[scene.Ids[i]] = i;
        var warp = new Dictionary<int, SdWarp>();
        for (int i = 0; i < scene.WarpIds.Count; i++) warp[scene.WarpIds[i]] = scene.Warps[i];
        var memo = new Dictionary<FrepOperand, BoundingBox>();

        BoundingBox Of(FrepOperand o)
        {
            if (memo.TryGetValue(o, out var b)) return b;
            b = BoundingBox.Empty;
            if (!o.IsNode)
            {
                if (prim.TryGetValue(o.ObjectId, out var i))
                {
                    var p = scene.Primitives[i];
                    var r = new Vector3d(1, 1, 1) * PrimRadius(p);
                    b = new BoundingBox(p.Frame.Origin - r, p.Frame.Origin + r);
                }
            }
            else if (tree.Find(o.NodeId) is { Hidden: false } n)
            {
                if (n.Op == FrepOp.Difference) { if (n.Operands.Length > 0) b = Of(n.Operands[0]); }
                else foreach (var c in n.Operands) b.Union(Of(c));
                if (b.IsValid && warp.TryGetValue(n.Id, out var w)) b = Warped(b, w);
            }
            return memo[o] = b;
        }

        var box = BoundingBox.Empty;
        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        foreach (var id in scene.Ids)
            if (!referenced.Contains(FrepOperand.Object(id))) box.Union(Of(FrepOperand.Object(id)));
        foreach (var n in tree.Nodes)
            if (!referenced.Contains(FrepOperand.Node(n.Id))) box.Union(Of(FrepOperand.Node(n.Id)));
        if (!box.IsValid) return (Point3d.Origin, 0);

        // A smooth union lowers the field by up to k/4 for two operands, and by up to k for more (it
        // compounds), so pad by k per node. The ellipsoid's field underestimates distance by up to the
        // ratio of its semi-axes, so the same drop in value moves its surface that much farther.
        double pad = 0, stretch = 1;
        foreach (var k in scene.Ks) if (k > 0) pad += k;
        foreach (var p in scene.Primitives)
            if (p.Kind == SdKind.Ellipsoid)
            {
                double lo = Math.Min(Math.Abs(p.P.X), Math.Min(Math.Abs(p.P.Y), Math.Abs(p.P.Z)));
                double hi = Math.Max(Math.Abs(p.P.X), Math.Max(Math.Abs(p.P.Y), Math.Abs(p.P.Z)));
                if (lo > 0) stretch = Math.Max(stretch, hi / lo);
            }
        double radius = 0.5 * box.Diagonal.Length + pad * stretch;
        return (box.Center, radius * 1.01 + 1e-6); // margin for float32 rounding
    }

    /// Box of a warp's output given its child's box; same as the mesher's (FrepField.WarpBox).
    internal static BoundingBox Warped(BoundingBox child, SdWarp w)
    {
        var op = w.Op switch { FrepOp.Twist => FieldOp.Twist, FrepOp.Bend => FieldOp.Bend, _ => FieldOp.Repeat };
        var (lo, hi) = FrepField.WarpBox(op, FrepWarpMath.Rows(w), (new(child.Min.X, child.Min.Y, child.Min.Z), new(child.Max.X, child.Max.Y, child.Max.Z)));
        return new BoundingBox(lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z);
    }
}

