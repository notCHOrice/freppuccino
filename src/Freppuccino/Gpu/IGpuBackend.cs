using Freppuccino.Shaders;
using Rhino.Display;

namespace Freppuccino.Gpu;

/// Draws a program on Rhino's own device, inside Rhino's current pass. Must not end/flush Rhino's pass or leak state Rhino relies on.
public interface IGpuBackend : IDisposable
{
    /// False if nothing was drawn (unsupported pass, compile failure already logged, etc.). Never throws.
    bool Draw(DisplayPipeline pipeline, ShaderSource program);
}

public static class GpuBackends
{
    static IGpuBackend? metal, d3d11;

    public static IGpuBackend? For(DisplayPipeline p) => p.Technology switch
    {
        DisplayTechnology.Metal => metal ??= new Metal.MetalBackend(),
        DisplayTechnology.Direct3d11 => d3d11 ??= new D3D11.D3D11Backend(),
        _ => null,
    };

    public static void DisposeAll() { metal?.Dispose(); d3d11?.Dispose(); metal = d3d11 = null; }
}
