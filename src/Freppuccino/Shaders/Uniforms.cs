using System.Runtime.InteropServices;

namespace Freppuccino.Shaders;

/// std140 writer for the single uniform block (set 0, binding 0). vec4-only layouts, so no padding rules beyond 16-byte rows.
/// Backends bind it at: Metal [[buffer(MetalIndex)]] (vertex + fragment), D3D11 cbuffer b0 (VS + PS).
public sealed class Uniforms
{
    /// Metal setBytes limit; D3D11 constant buffer is allocated at this size.
    public const int MaxBytes = 4096;
    /// High slot so we don't clobber a buffer binding Rhino's state cache thinks is still bound (Rhino's own use is low indices).
    public const int MetalIndex = 30;

    readonly List<float> data = [];

    public Uniforms Vec4(double x, double y = 0, double z = 0, double w = 0)
    {
        data.Add((float)x); data.Add((float)y); data.Add((float)z); data.Add((float)w);
        return this;
    }

    public byte[] ToBytes()
    {
        var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)).ToArray();
        if (bytes.Length > MaxBytes) throw new InvalidOperationException($"uniforms {bytes.Length} B > {MaxBytes} B");
        return bytes;
    }
}
