using System.Security.Cryptography;
using System.Text;

namespace Freppucino.Shaders;

/// GLSL 450 (Vulkan dialect) vertex + fragment. Clip space: y up, z in [0,1] (Metal/D3D). No vertex buffers; use gl_VertexIndex.
public sealed record ShaderSource(string Vertex, string Fragment)
{
    public string Hash { get; } = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Vertex + "\0" + Fragment)));
}
