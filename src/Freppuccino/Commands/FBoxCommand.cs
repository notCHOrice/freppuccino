using System;
using System.Runtime.InteropServices;
using Freppuccino.Sdf;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;

namespace Freppuccino.Commands;

/// <summary>
/// Same interaction as Rhino's Box command (Diagonal / 3Point / Vertical / Center),
/// but also writes sdBox metadata to the object's Attribute User Text:
///   SDF.Type = sdBox, SDF.b.x / SDF.b.y / SDF.b.z = half-extents along the local frame,
///   with the frame origin at the box center. See <see cref="SdfUserText"/>.
/// </summary>
[Guid("e5f44abf-954b-4888-9be5-5284e17e1828")]
public class FBoxCommand : Command
{
    public const string SdfType = "sdBox";

    public override string EnglishName => "FBox";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        // GetBoxMode.All gives the same prompts and options as the built-in Box command.
        var rc = RhinoGet.GetBox(out Box box, GetBoxMode.All, Point3d.Unset, null, null, null);
        if (rc != Result.Success)
            return rc;

        if (!box.IsValid)
        {
            RhinoApp.WriteLine("FBox: box is not valid.");
            return Result.Failure;
        }

        var brep = box.ToBrep();
        if (brep == null)
        {
            RhinoApp.WriteLine("FBox: unable to create box geometry.");
            return Result.Failure;
        }

        var attributes = doc.CreateDefaultAttributes();
        WriteSdf(attributes, box, doc.ModelUnitSystem);

        if (doc.Objects.AddBrep(brep, attributes) == Guid.Empty)
        {
            RhinoApp.WriteLine("FBox: unable to add box to the document.");
            return Result.Failure;
        }

        doc.Views.Redraw();
        return Result.Success;
    }

    /// <summary>sdBox(p, b): b is the half-size vector; the box is centered on the local origin.</summary>
    public static void WriteSdf(ObjectAttributes attributes, Box box, UnitSystem units)
    {
        var frame = box.Plane;
        frame.Origin = box.Center;

        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteVec3(attributes, "b",
            Math.Abs(box.X.Length) * 0.5,
            Math.Abs(box.Y.Length) * 0.5,
            Math.Abs(box.Z.Length) * 0.5);
    }
}
