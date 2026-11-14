using System.Runtime.InteropServices;
using Freppuccino.Display;
using Rhino;
using Rhino.Commands;
using Rhino.Input;

namespace Freppuccino.Commands;

[Guid("5b0e2f1a-7c43-4d8e-9a61-2f3c8d4e7b10")]
public class FrepBlendCommand : Command
{
    public override string EnglishName => "FrepBlend";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        double k = FrepSettings.Blend;
        var r = RhinoGet.GetNumber($"Smooth-union blend distance ({doc.ModelUnitSystem}, 0 = hard union)", false, ref k, 0, double.MaxValue);
        if (r != Result.Success) return r;
        FrepSettings.Blend = k;
        doc.Views.Redraw();
        return Result.Success;
    }
}
