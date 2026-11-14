using System.Security.Cryptography;
using System.Text;

namespace Freppuccino.Shaders;

/// GLSL 450 (Vulkan dialect) vertex + fragment. Clip space: y up, z in [0,1] (Metal/D3D). No vertex buffers; use gl_VertexIndex.
/// Optional per-draw data: one `layout(std140, set = 0, binding = 0) uniform` block, shared by both stages (see Uniforms).
public sealed record ShaderSource(string Vertex, string Fragment, uint VertexCount = 3)
{
    public string Hash { get; } = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Vertex + "\0" + Fragment)));
}
