using Rhino.Display;

namespace Freppuccino.Display;

/// "Freppuccino" = copy of Ghosted at <see cref="Transparency"/>, as the underlay for the shader layer.
/// Created once per machine; recreated if missing or out of date (older Wireframe / shaded copies, other transparency).
/// Persisted via UpdateDisplayMode.
///
/// "Freppuccino Capture" (hidden from menus) is the same with the proxy Breps invisible (fully transparent, no edges) and curves hidden (warp axes), for the
/// AI's screenshots (Ai/AiCapture.cs): only the shader's CSG result, grid and axes, so subtracted tools don't show as ghosts.
public static class FreppuccinoDisplayMode
{
    public const string Name = "Freppuccino";
    public const string CaptureName = "Freppuccino Capture";
    /// Ghosted surface transparency, 0 opaque .. 1 transparent (Rhino's 80%).
    public const double Transparency = 0.8;
    public static Guid Id { get; private set; }
    public static Guid CaptureId { get; private set; }

    public static void Ensure()
    {
        Id = EnsureMode(Name, IsCurrent, a =>
        {
            a.FrontMaterialTransparency = Transparency;
            a.BackMaterialTransparency = Transparency;
        });
        CaptureId = EnsureMode(CaptureName, IsCurrentCapture, a =>
        {
            a.FrontMaterialTransparency = 1;
            a.BackMaterialTransparency = 1;
            a.ShowSurfaceEdges = false;
            a.ShowIsoCurves = false;
            a.ShowSilhouttes = false;
            a.ShowTangentEdges = false;
            a.ShowTangentSeams = false;
            a.ShowCurves = false; // warp axis lines
        }, inMenu: false);
    }

    static Guid EnsureMode(string name, Func<DisplayModeDescription, bool> isCurrent, Action<DisplayPipelineAttributes> setup, bool inMenu = true)
    {
        var m = DisplayModeDescription.FindByName(name);
        if (m != null && !isCurrent(m))
        {
            DisplayModeDescription.DeleteDisplayMode(m.Id);
            m = null;
        }
        if (m == null)
        {
            var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.GhostedId, name);
            m = DisplayModeDescription.GetDisplayMode(id);
            if (m != null)
            {
                setup(m.DisplayAttributes);
                m.InMenu = inMenu;
                DisplayModeDescription.UpdateDisplayMode(m);
            }
        }
        return m?.Id ?? Guid.Empty;
    }

    static bool IsCurrent(DisplayModeDescription m) =>
        m.DisplayAttributes.ShadingEnabled && Math.Abs(m.DisplayAttributes.FrontMaterialTransparency - Transparency) < 1e-3;

    static bool IsCurrentCapture(DisplayModeDescription m) =>
        m.DisplayAttributes.ShadingEnabled && m.DisplayAttributes.FrontMaterialTransparency > 0.999 && !m.DisplayAttributes.ShowSurfaceEdges && !m.DisplayAttributes.ShowCurves && !m.InMenu;

    public static bool IsActive(DisplayPipeline p) =>
        p.DisplayPipelineAttributes?.Id is { } id && id != Guid.Empty && (id == Id || id == CaptureId);
}
