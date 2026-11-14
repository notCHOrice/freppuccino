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
        SdfUserText.WriteDefaultColor(attributes);

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

    /// <summary>
    /// Moves the frame and rescales b when the axes stay perpendicular (move, rotate, mirror, scale along the box).
    /// Shear, or scale off those axes, is stored as SDF.Linear.* and b stays in the original frame.
    /// </summary>
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!TryRead(attributes, out var frame, out var b))
        {
            reason = "missing or unreadable SDF.Frame.* / SDF.b.*";
            return false;
        }
        return SdfTransforms.CommitAffine(attributes, frame, xform, scale =>
        {
            if (!TryAbsorbScale(b, scale, out var next)) return false;
            SdfUserText.WriteVec3(attributes, "b", next.X, next.Y, next.Z);
            return true;
        }, out reason);
    }

    /// <summary>Per-axis scale of the half-extents. A box can take any positive scale along its own axes.</summary>
    public static bool TryAbsorbScale(Vector3d b, Vector3d scale, out Vector3d result)
    {
        result = new Vector3d(b.X * scale.X, b.Y * scale.Y, b.Z * scale.Z);
        return result.X > 0 && result.Y > 0 && result.Z > 0;
    }

    /// <summary>Reads the frame (origin = box center) and half-extents b.</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out Vector3d b)
    {
        b = Vector3d.Unset;
        return SdfUserText.TryReadFrame(attributes, out frame) && SdfUserText.TryReadVec3(attributes, "b", out b);
    }

    /// <summary>Pure version of <see cref="TransformSdf"/>; also used for the live gumball preview.</summary>
    public static bool TryTransform(Plane frame, Vector3d b, Transform xform, out Plane newFrame, out Vector3d newB, out string? reason)
    {
        newB = b;
        if (!SdfTransforms.TryTransformFrame(frame, xform, out newFrame, out var scale, out reason))
        {
            if (reason != null) reason += "; sdBox can't represent it";
            return false;
        }
        newB = new Vector3d(b.X * scale.X, b.Y * scale.Y, b.Z * scale.Z);
        return true;
    }
}
