using Rhino.Display;

namespace Freppucino.Display;

/// "Freppucino" = copy of Wireframe. Created once per machine if missing; persisted via UpdateDisplayMode.
public static class FreppucinoDisplayMode
{
    public const string Name = "Freppucino";
    public static Guid Id { get; private set; }

    public static void Ensure()
    {
        var m = DisplayModeDescription.FindByName(Name);
        if (m == null)
        {
            var id = DisplayModeDescription.CopyDisplayMode(DisplayModeDescription.WireframeId, Name);
            m = DisplayModeDescription.GetDisplayMode(id);
            if (m != null) DisplayModeDescription.UpdateDisplayMode(m);
        }
        Id = m?.Id ?? Guid.Empty;
    }

    public static bool IsActive(DisplayPipeline p) => Id != Guid.Empty && p.DisplayPipelineAttributes?.Id == Id;
}
