using Rhino;
using Rhino.Display;

namespace Freppuccino.Display;

/// Dev launches (VS Code / `dotnet run`) set FREPPUCCINO_AUTOSTART=1: switch every view to our mode once Rhino has a doc.
public static class DevAutostart
{
    public const string EnvVar = "FREPPUCCINO_AUTOSTART";

    public static void Install()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1") return;
        RhinoApp.Idle += OnIdle;
    }

    static void OnIdle(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var dm = DisplayModeDescription.GetDisplayMode(FreppuccinoDisplayMode.Id);
        if (doc == null || dm == null || doc.Views.ActiveView == null) return;
        RhinoApp.Idle -= OnIdle;
        foreach (var v in doc.Views) v.ActiveViewport.DisplayMode = dm;
        doc.Views.Redraw();
    }
}
