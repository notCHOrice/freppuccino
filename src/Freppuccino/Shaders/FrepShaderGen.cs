using System.Text;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Shaders;

/// Runtime-generated SDF scene: N spheres, smooth-unioned (IQ quadratic smin), raymarched on a fullscreen triangle.
/// Structure (N) is baked into the source; parameters (camera, spheres, blend) are uniforms, so moving spheres never recompiles.
public static class FrepShaderGen
{
    /// Uniform header = 5 vec4 rows; rest of Uniforms.MaxBytes is spheres.
    public const int MaxSpheres = Uniforms.MaxBytes / 16 - 5;

    static readonly Dictionary<int, ShaderSource> cache = [];

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

    /// N >= 1.
    public static ShaderSource Scene(int n)
    {
        if (cache.TryGetValue(n, out var s)) return s;
        var map = new StringBuilder("    float d = sdSphere(p, u.spheres[0]);\n");
        for (int i = 1; i < n; i++) map.Append($"    d = smin(d, sdSphere(p, u.spheres[{i}]), u.params.x);\n");
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
                vec4 spheres[{{n}}]; // xyz center, w radius
            } u;

            const vec3 GREY = vec3(0.78);

            float smin(float a, float b, float k)
            {
                if (k <= 0.0) return min(a, b);
                float h = max(k - abs(a - b), 0.0) / k;
                return min(a, b) - h * h * k * 0.25;
            }
            float sdSphere(vec3 p, vec4 s) { return length(p - s.xyz) - s.w; }
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
        return cache[n] = new ShaderSource(Vertex, frag);
    }

    /// Spheres beyond MaxSpheres are dropped (caller logs).
    public static byte[] SceneUniforms(RhinoViewport vp, IReadOnlyList<Sphere> spheres, double blend)
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
        for (int i = 0; i < Math.Min(spheres.Count, MaxSpheres); i++)
        {
            var s = spheres[i];
            u.Vec4(s.Center.X, s.Center.Y, s.Center.Z, s.Radius);
        }
        return u.ToBytes();
    }
}
