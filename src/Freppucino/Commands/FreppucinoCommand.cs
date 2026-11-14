using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

namespace Freppucino.Commands;

[Guid("cd49539f-80ff-4035-9e57-4fbc6c6d3fb7")]
public class FreppucinoCommand : Command
{
    public override string EnglishName => "Freppucino";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var rc = RhinoGet.GetPoint("Center", false, out Point3d center);
        if (rc != Result.Success) return rc;

        doc.Objects.AddSphere(new Sphere(center, 1.0));
        doc.Views.Redraw();
        RhinoApp.WriteLine("Freppucino: added sphere.");
        return Result.Success;
    }
}
