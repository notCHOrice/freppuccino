using Freppuccino.Gpu;
using Freppuccino.Shaders;
using Rhino;
using Rhino.Display;

namespace Freppuccino.Display;

/// Order (observed, Mac): unselected objects -> PostDrawObjects (us) -> DrawForeground -> selected/highlight pass -> DrawOverlay (gumball etc).
public sealed class ShaderConduit : DisplayConduit
{
    public ShaderSource Program { get; set; } = TriangleShader.Source;
    bool loggedFailure;

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        var dp = e.Display;
        if (!FreppuccinoDisplayMode.IsActive(dp)) return;
        try
        {
            if (GpuBackends.For(dp)?.Draw(dp, Program) == true) RhinoStateReset.Apply(dp);
        }
        catch (Exception ex)
        {
            if (!loggedFailure) RhinoApp.WriteLine("Freppuccino: " + ex);
            loggedFailure = true;
        }
    }
}
