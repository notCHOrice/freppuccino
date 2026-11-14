using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Display;

namespace Freppuccino.Commands;

[Guid("cd49539f-80ff-4035-9e57-4fbc6c6d3fb7")]
public class FreppuccinoCommand : Command
{
    public override string EnglishName => "Freppuccino";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var view = doc.Views.ActiveView;
        var dm = DisplayModeDescription.GetDisplayMode(Display.FreppuccinoDisplayMode.Id);
        if (view == null || dm == null) return Result.Failure;
        view.ActiveViewport.DisplayMode = dm;
        view.Redraw();
        return Result.Success;
    }
}
