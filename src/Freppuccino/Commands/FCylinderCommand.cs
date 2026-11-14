using System;
using System.Runtime.InteropServices;
using Freppuccino.Sdf;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Freppuccino.Commands;

/// <summary>
/// Same interaction as Rhino's Cylinder command (always a closed solid), plus sdCappedCylinder metadata
/// in Attribute User Text: SDF.Type = sdCappedCylinder, SDF.h = half height, SDF.r = radius,
/// frame origin at the cylinder's center, local Y = cylinder axis. See <see cref="SdfUserText"/>.
/// </summary>
[Guid("83e26d8a-8ac8-4517-b7ef-13af08fb180f")]
public class FCylinderCommand : Command
{
    public const string SdfType = "sdCappedCylinder";

    public override string EnglishName => "FCylinder";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Cylinder cylinder;
        using (var gc = new GetCylinder())
        {
            var rc = gc.Get(out cylinder);
            if (rc != Result.Success)
                return rc;
        }

        var bottom = cylinder.BasePlane.PointAt(0, 0, cylinder.Height1);
        var top = cylinder.BasePlane.PointAt(0, 0, cylinder.Height2);
        double radius = Math.Abs(cylinder.Radius);
        double h = bottom.DistanceTo(top) * 0.5;
        if (!cylinder.IsValid || radius <= RhinoMath.ZeroTolerance || h <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("FCylinder: cylinder is not valid.");
            return Result.Failure;
        }

        var frame = FrepInput.AxisFrame((bottom + top) * 0.5, top - bottom, cylinder.BasePlane.XAxis);
        return FrepInput.AddPrimitive(doc, "FCylinder", "cylinder", cylinder.ToBrep(true, true),
            a => WriteSdf(a, frame, h, radius, doc.ModelUnitSystem));
    }

    /// <summary>sdCappedCylinder(p, h, r): h = half height along local Y, r = radius; centered on the local origin.</summary>
    public static void WriteSdf(ObjectAttributes attributes, Plane frame, double h, double r, UnitSystem units)
    {
        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteFloat(attributes, "h", h);
        SdfUserText.WriteFloat(attributes, "r", r);
    }

    /// <summary>
    /// Moves the frame, scales h along the axis and r across it when the cross-section stays round.
    /// An elliptical or sheared cylinder is stored as SDF.Linear.*; h and r stay in the original frame.
    /// </summary>
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!TryRead(attributes, out var frame, out var h, out var r))
        {
            reason = "missing or unreadable SDF.Frame.* / SDF.h / SDF.r";
            return false;
        }
        return SdfTransforms.CommitAffine(attributes, frame, xform, scale =>
        {
            if (!TryAbsorbScale(h, r, scale, out var nh, out var nr)) return false;
            SdfUserText.WriteFloat(attributes, "h", nh);
            SdfUserText.WriteFloat(attributes, "r", nr);
            return true;
        }, out reason);
    }

    /// <summary>Axis scale goes into h, the shared radial scale into r. Unequal X/Z scales stay in the linear map.</summary>
    public static bool TryAbsorbScale(double h, double r, Vector3d scale, out double newH, out double newR)
    {
        newH = h * scale.Y;
        newR = r * (scale.X + scale.Z) * 0.5;
        return SdfTransforms.NearlyEqual(scale.X, scale.Z) && newH > 0 && newR > 0;
    }

    /// <summary>Reads the frame (origin = center, Y = axis), half height h and radius r.</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out double h, out double r)
    {
        h = r = 0.0;
        return SdfUserText.TryReadFrame(attributes, out frame) &&
               SdfUserText.TryReadFloat(attributes, "h", out h) &&
               SdfUserText.TryReadFloat(attributes, "r", out r);
    }

    /// <summary>Pure version of <see cref="TransformSdf"/>; also usable for a live gumball preview.</summary>
    public static bool TryTransform(Plane frame, double h, double r, Transform xform, out Plane newFrame, out double newH, out double newR, out string? reason)
    {
        newH = h;
        newR = r;
        if (!SdfTransforms.TryTransformFrame(frame, xform, out newFrame, out var scale, out reason))
        {
            if (reason != null) reason += "; sdCappedCylinder can't represent it";
            return false;
        }

        if (!SdfTransforms.NearlyEqual(scale.X, scale.Z))
        {
            reason = "non-uniform scale across the axis makes the cylinder elliptical; sdCappedCylinder can't represent it";
            return false;
        }

        newH = h * scale.Y;
        newR = r * (scale.X + scale.Z) * 0.5;
        return true;
    }
}
