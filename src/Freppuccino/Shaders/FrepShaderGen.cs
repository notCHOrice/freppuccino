using System.Text;
using Freppuccino.Scene;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Shaders;

/// Runtime-generated SDF scene: spheres then boxes, smooth-unioned (IQ quadratic smin), raymarched on a fullscreen triangle.
/// Structure (sphere/box counts) is baked into the source; parameters (camera, primitives, blend) are uniforms,
/// so moving or resizing primitives never recompiles.
public static class FrepShaderGen
{
    /// Uniform header = 6 vec4 rows; the rest of Uniforms.MaxBytes holds primitives.
    public const int MaxPrimitiveRows = Uniforms.MaxBytes / 16 - 6;
    public const int SphereRows = 1, BoxRows = 3;

    static readonly Dictionary<(int Spheres, int Boxes), ShaderSource> cache = [];

    /// How many spheres and boxes fit in the uniform block (spheres first).
    public static (int Spheres, int Boxes) Fit(int spheres, int boxes)
    {
        int ns = Math.Min(spheres, MaxPrimitiveRows / SphereRows);
        int nb = Math.Min(boxes, (MaxPrimitiveRows - ns * SphereRows) / BoxRows);
        return (ns, nb);
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

    /// spheres + boxes >= 1; counts must come from <see cref="Fit"/>.
    public static ShaderSource Scene(int spheres, int boxes)
    {
        if (cache.TryGetValue((spheres, boxes), out var s)) return s;
        var terms = new List<string>();
        int row = 0;
        for (int i = 0; i < spheres; i++, row += SphereRows) terms.Add($"sdSphere(p, u.prims[{row}])");
        for (int i = 0; i < boxes; i++, row += BoxRows) terms.Add($"sdBox(p, u.prims[{row}], u.prims[{row + 1}], u.prims[{row + 2}])");
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
                // box:    3 rows (xyz center, w b.x), (xyz unit X axis, w b.y), (xyz unit Y axis, w b.z); Z = X × Y
                vec4 prims[{{row}}];
            } u;

            const vec3 GREY = vec3(0.78);

            float smin(float a, float b, float k)
            {
                if (k <= 0.0) return min(a, b);
                float h = max(k - abs(a - b), 0.0) / k;
                return min(a, b) - h * h * k * 0.25;
            }
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
        return cache[(spheres, boxes)] = new ShaderSource(Vertex, frag);
    }

    /// Writes the first <paramref name="fit"/> spheres and boxes (from <see cref="Fit"/>); the rest are dropped (caller logs).
    public static byte[] SceneUniforms(RhinoViewport vp, FrepSceneData scene, (int Spheres, int Boxes) fit, double blend)
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
        for (int i = 0; i < fit.Spheres; i++)
        {
            var (f, radius) = scene.Spheres[i];
            u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, radius);
        }
        for (int i = 0; i < fit.Boxes; i++)
        {
            var (f, half) = scene.Boxes[i];
            u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, half.X)
             .Vec4(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, half.Y)
             .Vec4(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, half.Z);
        }
        return u.ToBytes();
    }
}
