using System.Text;
using Freppuccino.Scene;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Shaders;

/// Runtime-generated SDF scene: all primitives (grouped by <see cref="SdKind"/>), smooth-unioned (IQ quadratic smin),
/// raymarched on a fullscreen triangle. Structure (per-kind counts) is baked into the source; parameters
/// (camera, primitives, blend) are uniforms, so moving or resizing primitives never recompiles.
public static class FrepShaderGen
{
    /// Uniform header = 6 vec4 rows; the rest of Uniforms.MaxBytes holds primitives.
    public const int MaxPrimitiveRows = Uniforms.MaxBytes / 16 - 6;
    public static readonly int KindCount = Enum.GetValues<SdKind>().Length;

    /// Sphere: 1 row (xyz center, w s). Everything else: 3 rows (xyz origin, w P.x), (xyz unit X, w P.y), (xyz unit Y, w P.z).
    public static int Rows(SdKind kind) => kind == SdKind.Sphere ? 1 : 3;

    static readonly Dictionary<string, ShaderSource> cache = [];

    /// How many of the scene's primitives (a prefix, since they're grouped by kind) fit in the uniform block.
    public static int Fit(FrepSceneData scene)
    {
        int rows = 0, n = 0;
        foreach (var p in scene.Primitives)
        {
            if (rows + Rows(p.Kind) > MaxPrimitiveRows) break;
            rows += Rows(p.Kind);
            n++;
        }
        return n;
    }

    /// GLSL call evaluating one primitive whose rows start at <paramref name="row"/>; q is the point in its local frame.
    static string Term(SdKind kind, int row)
    {
        if (kind == SdKind.Sphere) return $"sdSphere(p, u.prims[{row}])";
        string o = $"u.prims[{row}]", x = $"u.prims[{row + 1}]", y = $"u.prims[{row + 2}]";
        string q = $"toLocal(p, {o}, {x}, {y})";
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

    /// Shader for the first <paramref name="count"/> primitives (count >= 1, from <see cref="Fit"/>).
    public static ShaderSource Scene(FrepSceneData scene, int count)
    {
        var counts = new int[KindCount];
        for (int i = 0; i < count; i++) counts[(int)scene.Primitives[i].Kind]++;
        var key = string.Join(",", counts);
        if (cache.TryGetValue(key, out var s)) return s;

        var terms = new List<string>();
        int row = 0;
        for (int k = 0; k < KindCount; k++)
            for (int i = 0; i < counts[k]; i++, row += Rows((SdKind)k))
                terms.Add(Term((SdKind)k, row));
        var map = new StringBuilder($"    float d = {terms[0]};\n");
        for (int i = 1; i < terms.Count; i++) map.Append($"    d = smin(d, {terms[i]}, u.params.x);\n");
        var frag = $$"""
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
                vec4 params;     // x = blend k, y = eps per unit t, z = eps const, w = far
                // sphere: 1 row  (xyz center, w radius s)
                // others: 3 rows (xyz origin, w P.x), (xyz unit X axis, w P.y), (xyz unit Y axis, w P.z); Z = X × Y
                //   box b = P; torus t = P.xy; cylinder h, r; cone h, r1, r2; ellipsoid r = P; pyramid h, s
                vec4 prims[{{row}}];
            } u;

            const vec3 GREY = vec3(0.78);

            float smin(float a, float b, float k)
            {
                if (k <= 0.0) return min(a, b);
                float h = max(k - abs(a - b), 0.0) / k;
                return min(a, b) - h * h * k * 0.25;
            }
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
        return cache[key] = new ShaderSource(Vertex, frag);
    }

    /// Writes the first <paramref name="count"/> primitives (from <see cref="Fit"/>); the rest are dropped (caller logs).
    public static byte[] SceneUniforms(RhinoViewport vp, FrepSceneData scene, int count, double blend)
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
            .Vec4(blend, persp ? slope / near : 0, persp ? 0 : slope, far);
        // Same order as Scene(): primitives are already grouped by kind.
        for (int i = 0; i < count; i++)
        {
            var (kind, f, v) = scene.Primitives[i];
            u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, v.X);
            if (kind == SdKind.Sphere) continue;
            u.Vec4(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, v.Y)
             .Vec4(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, v.Z);
        }
        return u.ToBytes();
    }
}
