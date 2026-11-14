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
/// Same interaction as Rhino's Cone command (always a closed solid), plus sdCappedCone metadata
/// in Attribute User Text: SDF.Type = sdCappedCone, SDF.h = half height, SDF.r1 = base radius (local y = -h),
/// SDF.r2 = top radius (local y = +h; 0 for a pointed cone), frame origin halfway between base and apex,
/// local Y = axis from base to apex. See <see cref="SdfUserText"/>.
/// </summary>
[Guid("c9a82d75-2242-47d6-a3fc-aa5390879fbf")]
public class FConeCommand : Command
{
    public const string SdfType = "sdCappedCone";

    public override string EnglishName => "FCone";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Cone cone;
        using (var gc = new GetCone())
        {
            var rc = gc.Get(out cone);
            if (rc != Result.Success)
                return rc;
        }

        var basePoint = cone.BasePoint;
        var apex = cone.ApexPoint;
        double radius = Math.Abs(cone.Radius);
        double h = basePoint.DistanceTo(apex) * 0.5;
        if (!cone.IsValid || radius <= RhinoMath.ZeroTolerance || h <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("FCone: cone is not valid.");
            return Result.Failure;
        }

        var frame = FrepInput.AxisFrame((basePoint + apex) * 0.5, apex - basePoint, cone.Plane.XAxis);
        return FrepInput.AddPrimitive(doc, "FCone", "cone", cone.ToBrep(true),
            a => WriteSdf(a, frame, h, radius, 0.0, doc.ModelUnitSystem));
    }

    /// <summary>sdCappedCone(p, h, r1, r2): h = half height along local Y, r1 at y = -h, r2 at y = +h; centered on the local origin.</summary>
    public static void WriteSdf(ObjectAttributes attributes, Plane frame, double h, double r1, double r2, UnitSystem units)
    {
        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteFloat(attributes, "h", h);
        SdfUserText.WriteFloat(attributes, "r1", r1);
        SdfUserText.WriteFloat(attributes, "r2", r2);
    }

    /// <summary>
    /// Moves the frame, scales h along the axis and r1/r2 across it. Works for move, rotate, mirror, uniform scale
    /// and scale along the axis. Scaling the cross-section non-uniformly makes it elliptical, so it is rejected.
    /// </summary>
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!TryRead(attributes, out var frame, out var h, out var r1, out var r2))
        {
            reason = "missing or unreadable SDF.Frame.* / SDF.h / SDF.r1 / SDF.r2";
            return false;
        }

        if (!TryTransform(frame, h, r1, r2, xform, out frame, out h, out r1, out r2, out reason))
            return false;

        SdfUserText.WriteFrame(attributes, frame);
        SdfUserText.WriteFloat(attributes, "h", h);
        SdfUserText.WriteFloat(attributes, "r1", r1);
        SdfUserText.WriteFloat(attributes, "r2", r2);
        return true;
    }

    /// <summary>Reads the frame (origin = mid-axis, Y = base→top), half height h and radii r1 (base), r2 (top).</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out double h, out double r1, out double r2)
    {
        h = r1 = r2 = 0.0;
        return SdfUserText.TryReadFrame(attributes, out frame) &&
               SdfUserText.TryReadFloat(attributes, "h", out h) &&
               SdfUserText.TryReadFloat(attributes, "r1", out r1) &&
               SdfUserText.TryReadFloat(attributes, "r2", out r2);
    }

    /// <summary>Pure version of <see cref="TransformSdf"/>; also usable for a live gumball preview.</summary>
    public static bool TryTransform(Plane frame, double h, double r1, double r2, Transform xform,
        out Plane newFrame, out double newH, out double newR1, out double newR2, out string? reason)
    {
        newH = h;
        newR1 = r1;
        newR2 = r2;
        if (!SdfTransforms.TryTransformFrame(frame, xform, out newFrame, out var scale, out reason))
        {
            if (reason != null) reason += "; sdCappedCone can't represent it";
            return false;
        }

        if (!SdfTransforms.NearlyEqual(scale.X, scale.Z))
        {
            reason = "non-uniform scale across the axis makes the cone elliptical; sdCappedCone can't represent it";
            return false;
        }

        double radial = (scale.X + scale.Z) * 0.5;
        newH = h * scale.Y;
        newR1 = r1 * radial;
        newR2 = r2 * radial;
        return true;
    }
}
