using Freppuccino.Gpu;
using Freppuccino.Scene;
using Freppuccino.Shaders;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Display;

/// Order (observed, Mac): unselected objects -> PostDrawObjects (us) -> DrawForeground -> selected/highlight pass -> DrawOverlay (gumball etc).
/// We draw depth-off over the unselected wires; later Rhino draws land on top. One RhinoStateReset after our draws (Rhino state cache).
public sealed class ShaderConduit : DisplayConduit
{
    const string Badge = "FREP MODE";
    const int BadgeTextHeight = 11, BadgeMargin = 10, BadgePadX = 9, BadgePadY = 5;
    bool loggedFailure, loggedCap;

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        var dp = e.Display;
        if (!FreppuccinoDisplayMode.IsActive(dp)) return;
        try
        {
            var gpu = GpuBackends.For(dp);
            if (gpu == null) return;
            bool drew = false;

            var scene = FrepScene.Get(e.RhinoDoc);
            if (scene.Count > 0)
            {
                var fit = FrepShaderGen.Fit(scene.Spheres.Count, scene.Boxes.Count);
                if ((fit.Spheres < scene.Spheres.Count || fit.Boxes < scene.Boxes.Count) && !loggedCap)
                {
                    RhinoApp.WriteLine($"Freppuccino: {scene.Spheres.Count} spheres + {scene.Boxes.Count} boxes, drawing {fit.Spheres} + {fit.Boxes}");
                    loggedCap = true;
                }
                drew |= gpu.Draw(dp, FrepShaderGen.Scene(fit.Spheres, fit.Boxes), FrepShaderGen.SceneUniforms(dp.Viewport, scene, fit, FrepSettings.Blend));
            }

            var (r, size) = BadgeRect(dp);
            drew |= gpu.Draw(dp, PillShader.Source, PillShader.Uniforms(r.Left, r.Top, r.Right, r.Bottom, size.Width, size.Height));

            if (drew) RhinoStateReset.Apply(dp);
        }
        catch (Exception ex)
        {
            if (!loggedFailure) RhinoApp.WriteLine("Freppuccino: " + ex);
            loggedFailure = true;
        }
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        var dp = e.Display;
        if (!FreppuccinoDisplayMode.IsActive(dp) || GpuBackends.For(dp) == null) return;
        var (r, _) = BadgeRect(dp);
        dp.Draw2dText(Badge, System.Drawing.Color.FromArgb(30, 30, 30), new Point2d((r.Left + r.Right) * 0.5, (r.Top + r.Bottom) * 0.5), true, BadgeTextHeight, "Arial");
    }

    /// Badge rect in Rhino 2D screen coords (y down), sized from the measured text.
    static (System.Drawing.RectangleF rect, System.Drawing.Size view) BadgeRect(DisplayPipeline dp)
    {
        var size = dp.Viewport.Size;
        var t = dp.Measure2dText(Badge, new Point2d(0, 0), false, 0, BadgeTextHeight, "Arial");
        float w = Math.Abs(t.Width) + 2 * BadgePadX, h = Math.Abs(t.Height) + 2 * BadgePadY; // Height comes back negative (y-up rect)
        return (new System.Drawing.RectangleF(BadgeMargin, BadgeMargin, w, h), size);
    }
}
