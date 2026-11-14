namespace Freppuccino.Shaders;

/// "FREP MODE" badge background: the old debug triangle's R/G/B as a horizontal gradient in a rounded rect (SDF).
/// Rect in Rhino 2D screen coords (logical px, y down); text drawn on top by Rhino (ShaderConduit.DrawForeground).
public static class PillShader
{
    public static readonly ShaderSource Source = new(
        """
        #version 450
        layout(std140, set = 0, binding = 0) uniform Pill { vec4 rect; vec4 viewport; } u; // rect x0,y0,x1,y1; viewport w,h,radius
        layout(location = 0) out vec2 vPx;
        const vec2 Q[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));
        void main()
        {
            vPx = mix(u.rect.xy, u.rect.zw, Q[gl_VertexIndex]);
            gl_Position = vec4(vPx.x / u.viewport.x * 2.0 - 1.0, 1.0 - vPx.y / u.viewport.y * 2.0, 0.0, 1.0);
        }
        """,
        """
        #version 450
        layout(std140, set = 0, binding = 0) uniform Pill { vec4 rect; vec4 viewport; } u;
        layout(location = 0) in vec2 vPx;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec2 c = (u.rect.xy + u.rect.zw) * 0.5, h = (u.rect.zw - u.rect.xy) * 0.5;
            float r = u.viewport.z;
            vec2 q = abs(vPx - c) - h + r;
            if (length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r > 0.0) discard;
            float t = clamp((vPx.x - u.rect.x) / (u.rect.z - u.rect.x), 0.0, 1.0);
            vec3 rgb = t < 0.5 ? mix(vec3(1, 0, 0), vec3(0, 1, 0), t * 2.0) : mix(vec3(0, 1, 0), vec3(0, 0, 1), t * 2.0 - 1.0);
            outColor = vec4(mix(rgb, vec3(1.0), 0.45), 1.0); // pastel so dark text reads
        }
        """,
        VertexCount: 6);

    public static byte[] Uniforms(float x0, float y0, float x1, float y1, float viewW, float viewH) =>
        new Uniforms().Vec4(x0, y0, x1, y1).Vec4(viewW, viewH, (y1 - y0) * 0.5).ToBytes();
}
