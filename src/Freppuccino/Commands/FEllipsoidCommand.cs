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
/// Center + three axis ends (like Rhino's Ellipsoid command, default mode), plus sdEllipsoid metadata in
/// Attribute User Text: SDF.Type = sdEllipsoid, SDF.r.x / SDF.r.y / SDF.r.z = semi-axis lengths along the
/// local frame, frame origin at the center. See <see cref="SdfUserText"/>.
/// Note: IQ's sdEllipsoid is a bound (not an exact distance), so blends look slightly different from other primitives.
/// </summary>
[Guid("6bff18ca-61ba-46b9-b28c-220ac70352bf")]
public class FEllipsoidCommand : Command
{
    public const string SdfType = "sdEllipsoid";

    static double lastX = 2.0, lastY = 1.5, lastZ = 1.0;

    public override string EnglishName => "FEllipsoid";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var rc = FrepInput.GetPoint("Ellipsoid center", out var center, out var cplane);
        if (rc != Result.Success)
            return rc;

        // First axis: any direction in the construction plane.
        var plane = cplane;
        rc = FrepInput.GetLength("FEllipsoid", "End of first axis", center, lastX,
            p => center.DistanceTo(p),
            gp => gp.Constrain(plane, false),
            null,
            out var rx, out var firstPick);
        if (rc != Result.Success)
            return rc;
        rx = Math.Abs(rx);

        var xAxis = firstPick.HasValue ? firstPick.Value - center : cplane.XAxis;
        var frame = new Plane(center, xAxis, Vector3d.CrossProduct(cplane.ZAxis, xAxis));
        if (!frame.IsValid)
            frame = cplane;

        // Second axis: perpendicular to the first, in the construction plane.
        rc = FrepInput.GetLength("FEllipsoid", "End of second axis", center, lastY,
            p => center.DistanceTo(p),
            gp => gp.Constrain(new Line(center, center + frame.YAxis)),
            (display, _, ry, color) =>
            {
                if (Math.Abs(ry) > RhinoMath.ZeroTolerance)
                    display.DrawCurve(new Ellipse(frame, rx, Math.Abs(ry)).ToNurbsCurve(), color);
            },
            out var ry, out _);
        if (rc != Result.Success)
            return rc;
        ry = Math.Abs(ry);

        // Third axis: along the construction plane normal.
        rc = FrepInput.GetLength("FEllipsoid", "End of third axis", center, lastZ,
            p => center.DistanceTo(p),
            gp => gp.Constrain(new Line(center, center + frame.ZAxis)),
            (display, _, rz, color) =>
            {
                var preview = Math.Abs(rz) > RhinoMath.ZeroTolerance ? CreateBrep(frame, new Vector3d(rx, ry, Math.Abs(rz))) : null;
                if (preview != null) display.DrawBrepWires(preview, color);
            },
            out var rz, out _);
        if (rc != Result.Success)
            return rc;
        rz = Math.Abs(rz);

        lastX = rx; lastY = ry; lastZ = rz;
        var radii = new Vector3d(rx, ry, rz);
        return FrepInput.AddPrimitive(doc, "FEllipsoid", "ellipsoid", CreateBrep(frame, radii),
            a => WriteSdf(a, frame, radii, doc.ModelUnitSystem));
    }

    /// <summary>A unit sphere on <paramref name="frame"/>, scaled by the semi-axes along the frame axes.</summary>
    public static Brep? CreateBrep(Plane frame, Vector3d r)
    {
        var surface = new Sphere(frame, 1.0).ToNurbsSurface();
        if (surface == null || !surface.Transform(Transform.Scale(frame, r.X, r.Y, r.Z)))
            return null;
        return Brep.CreateFromSurface(surface);
    }

    /// <summary>sdEllipsoid(p, r): r = semi-axis lengths along local X/Y/Z; centered on the local origin.</summary>
    public static void WriteSdf(ObjectAttributes attributes, Plane frame, Vector3d r, UnitSystem units)
    {
        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteVec3(attributes, "r", r.X, r.Y, r.Z);
    }

    /// <summary>
    /// Moves the frame and rescales r. Works for any transform that keeps the ellipsoid axes perpendicular:
    /// move, rotate, mirror, uniform scale, and non-uniform scale along the ellipsoid's own axes.
    /// Other transforms tilt the principal axes off the frame, which this parameterization can't express, so they are rejected.
    /// </summary>
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!TryRead(attributes, out var frame, out var r))
        {
            reason = "missing or unreadable SDF.Frame.* / SDF.r.*";
            return false;
        }

        if (!TryTransform(frame, r, xform, out frame, out r, out reason))
            return false;

        SdfUserText.WriteFrame(attributes, frame);
        SdfUserText.WriteVec3(attributes, "r", r.X, r.Y, r.Z);
        return true;
    }

    /// <summary>Reads the frame (origin = center) and semi-axes r.</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out Vector3d r)
    {
        r = Vector3d.Unset;
        return SdfUserText.TryReadFrame(attributes, out frame) && SdfUserText.TryReadVec3(attributes, "r", out r);
    }

    /// <summary>Pure version of <see cref="TransformSdf"/>; also usable for a live gumball preview.</summary>
    public static bool TryTransform(Plane frame, Vector3d r, Transform xform, out Plane newFrame, out Vector3d newR, out string? reason)
    {
        newR = r;
        if (!SdfTransforms.TryTransformFrame(frame, xform, out newFrame, out var scale, out reason))
        {
            if (reason != null) reason += "; sdEllipsoid can't represent it";
            return false;
        }
        newR = new Vector3d(r.X * scale.X, r.Y * scale.Y, r.Z * scale.Z);
        return true;
    }
}
