using Rhino.Display;

namespace Freppuccino.Display;

/// "Freppuccino" = copy of Ghosted at <see cref="Transparency"/>, as the underlay for the shader layer.
/// Created once per machine; recreated if missing or out of date (older Wireframe / shaded copies, other transparency).
/// Persisted via UpdateDisplayMode.
public static class FreppuccinoDisplayMode
{
    public const string Name = "Freppuccino";
    /// Ghosted surface transparency, 0 opaque .. 1 transparent (Rhino's 80%).
    public const double Transparency = 0.8;
    public static Guid Id { get; private set; }

    public static void Ensure()
    {
        var m = DisplayModeDescription.FindByName(Name);
        if (m != null && !IsCurrent(m))
        {
            DisplayModeDescription.DeleteDisplayMode(m.Id);
            m = null;
        }
        if (m == null)
        {
            var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.GhostedId, Name);
            m = DisplayModeDescription.GetDisplayMode(id);
            if (m != null)
            {
                m.DisplayAttributes.FrontMaterialTransparency = Transparency;
                m.DisplayAttributes.BackMaterialTransparency = Transparency;
                DisplayModeDescription.UpdateDisplayMode(m);
            }
        }
        Id = m?.Id ?? Guid.Empty;
    }

    static bool IsCurrent(DisplayModeDescription m) =>
        m.DisplayAttributes.ShadingEnabled && Math.Abs(m.DisplayAttributes.FrontMaterialTransparency - Transparency) < 1e-3;

    public static bool IsActive(DisplayPipeline p) => Id != Guid.Empty && p.DisplayPipelineAttributes?.Id == Id;
}
