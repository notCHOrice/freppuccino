using Rhino.Display;

namespace Freppucino.Display;

/// Rhino caches bound GPU state and skips rebinding if unchanged; after our draw that cache is stale.
/// One clipped Rhino line (just behind the camera) forces Rhino to rebind its own state. Validated on Metal; assumed on D3D11.
public static class RhinoStateReset
{
    /// Test toggle (set via reflection from run_csharp, or env FREPPUCINO_STATE_RESET=0).
    public static bool Enabled = Environment.GetEnvironmentVariable("FREPPUCINO_STATE_RESET") != "0";

    public static void Apply(DisplayPipeline dp)
    {
        if (!Enabled) return;
        var vp = dp.Viewport;
        var c = vp.CameraLocation - vp.CameraDirection;
        var x = vp.CameraX * (vp.CameraLocation.DistanceTo(vp.CameraTarget) * 1e-4);
        dp.DrawLine(c, c + x, System.Drawing.Color.Magenta, 1);
    }
}
