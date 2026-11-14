namespace Freppucino.Shaders;

public static class TriangleShader
{
    public static readonly ShaderSource Source = new(
        """
        #version 450
        layout(location = 0) out vec3 vColor;
        const vec2 P[3] = vec2[](vec2(-0.5, -0.5), vec2(0.5, -0.5), vec2(0.0, 0.5));
        const vec3 C[3] = vec3[](vec3(1,0,0), vec3(0,1,0), vec3(0,0,1));
        void main() { vColor = C[gl_VertexIndex]; gl_Position = vec4(P[gl_VertexIndex], 0.0, 1.0); }
        """,
        """
        #version 450
        layout(location = 0) in vec3 vColor;
        layout(location = 0) out vec4 outColor;
        void main() { outColor = vec4(vColor, 1.0); }
        """);
}
