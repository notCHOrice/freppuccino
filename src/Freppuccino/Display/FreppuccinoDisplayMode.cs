using Rhino.Display;

namespace Freppuccino.Display;

/// "Freppuccino" = copy of Shaded (shaded underlay + click-in-surface picking), selection highlight = wires only.
/// Created once per machine if missing (or if an older wireframe-based copy exists); persisted via UpdateDisplayMode.
public static class FreppuccinoDisplayMode
{
    public const string Name = "Freppuccino";
    public static Guid Id { get; private set; }

    public static void Ensure()
    {
        var m = DisplayModeDescription.FindByName(Name);
        if (m != null && !m.DisplayAttributes.ShadingEnabled)
        {
            DisplayModeDescription.DeleteDisplayMode(m.Id); // pre-shaded version
            m = null;
        }
        if (m == null)
        {
            var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.ShadedId, Name);
            m = DisplayModeDescription.GetDisplayMode(id);
            if (m != null)
            {
                m.DisplayAttributes.HighlightSurfaces = false;
                DisplayModeDescription.UpdateDisplayMode(m);
            }
        }
        Id = m?.Id ?? Guid.Empty;
    }

    public static bool IsActive(DisplayPipeline p) => Id != Guid.Empty && p.DisplayPipelineAttributes?.Id == Id;
}
