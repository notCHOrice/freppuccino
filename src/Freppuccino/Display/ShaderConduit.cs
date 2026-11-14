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
    /// Rhino draws the viewport title (view name, e.g. "Perspective" or a restored named view) in the top-left corner;
    /// the badge sits just below it so long names never run into it. Logical px, y down.
    const int TitleClearance = 28;
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
            if (scene.Dropped > 0 && !loggedCap)
            {
                RhinoApp.WriteLine($"Freppuccino: uniform budget full, {scene.Dropped} primitives not drawn");
                loggedCap = true;
            }
            if (scene.Program != null)
                drew |= gpu.Draw(dp, scene.Program, FrepShaderGen.SceneUniforms(dp.Viewport, scene));

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
        if (dp == null || !FreppuccinoDisplayMode.IsActive(dp) || GpuBackends.For(dp) == null) return;
        try
        {
            var (r, _) = BadgeRect(dp);
            dp.Draw2dText(Badge, System.Drawing.Color.FromArgb(30, 30, 30), new Point2d((r.Left + r.Right) * 0.5, (r.Top + r.Bottom) * 0.5), true, BadgeTextHeight, "Arial");
        }
        catch (Exception ex)
        {
            // Offscreen pipelines (view capture, named-view thumbnails) may lack text/viewport state.
            if (!loggedFailure) RhinoApp.WriteLine("Freppuccino: " + ex);
            loggedFailure = true;
        }
    }

    /// Badge rect in Rhino 2D screen coords (y down), sized from the measured text, offset below Rhino's viewport title.
    /// Falls back to an estimated size when measuring isn't available (e.g. offscreen capture pipelines).
    static (System.Drawing.RectangleF rect, System.Drawing.Size view) BadgeRect(DisplayPipeline dp)
    {
        var size = dp.Viewport?.Size ?? System.Drawing.Size.Empty;
        float tw, th;
        try
        {
            var t = dp.Measure2dText(Badge, new Point2d(0, 0), false, 0, BadgeTextHeight, "Arial");
            tw = Math.Abs(t.Width); th = Math.Abs(t.Height); // Height comes back negative (y-up rect)
        }
        catch (NullReferenceException)
        {
            tw = 0; th = 0;
        }
        if (tw <= 0 || th <= 0) { tw = Badge.Length * BadgeTextHeight * 0.65f; th = BadgeTextHeight; }
        float w = tw + 2 * BadgePadX, h = th + 2 * BadgePadY;
        return (new System.Drawing.RectangleF(BadgeMargin, TitleClearance, w, h), size);
    }
}
