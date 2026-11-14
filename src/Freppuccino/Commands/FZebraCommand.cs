using System.Runtime.InteropServices;
using Freppuccino.Ui;
using Rhino;
using Rhino.Commands;

namespace Freppuccino.Commands;

/// Zebra analysis on unioned objects: the surfaces of FUnion nodes are drawn with stripes of the reflected view
/// direction (like Rhino's Zebra), so blend continuity across smooth unions is visible. Opens the Zebra Options window
/// (stripe direction / size / colors, live); zebra stays on while it is open. Session-only; drawn in the Freppuccino mode.
[Guid("7b3e5f1a-9c24-4d86-b0e7-2a6f8d1c4e95")]
public class FZebraCommand : Command
{
    public override string EnglishName => "FZebra";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        ZebraOptionsForm.ShowOrFocus(doc);
        return Result.Success;
    }
}

/// Turns FZebra off (closes the Zebra Options window), like Rhino's ZebraOff.
[Guid("e4a19c63-5b0d-4f2e-8d71-3c6b9a0f2d58")]
public class FZebraOffCommand : Command
{
    public override string EnglishName => "FZebraOff";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (!ZebraOptionsForm.CloseIfOpen()) RhinoApp.WriteLine("FZebra is not on.");
        return Result.Success;
    }
}
