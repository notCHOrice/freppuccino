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
/// Square-based pyramid from base center, base corner and height, plus sdPyramid metadata in Attribute User Text:
///   SDF.Type = sdPyramid, SDF.h = height, SDF.s = base side length,
///   frame origin at the base center, local Y = toward the apex, local X/Z = parallel to the base edges.
/// IQ's sdPyramid(p, h) has a unit base, so evaluate as sdPyramid(q / s, h / s) * s. See <see cref="SdfUserText"/>.
/// </summary>
[Guid("3bc7c831-9ea1-415d-b281-10f645e7afcc")]
public class FPyramidCommand : Command
{
    public const string SdfType = "sdPyramid";

    static double lastHalfDiagonal = 1.0, lastHeight = 1.0;

    public override string EnglishName => "FPyramid";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var rc = FrepInput.GetPoint("Center of base", out var center, out var cplane);
        if (rc != Result.Success)
            return rc;

        // Corner of the square base, in the construction plane: its direction sets the base rotation.
        var plane = cplane;
        rc = FrepInput.GetLength("FPyramid", "Corner of base", center, lastHalfDiagonal,
            p => center.DistanceTo(p),
            gp => gp.Constrain(plane, false),
            (display, p, d, color) =>
            {
                var corner = p - center;
                if (Math.Abs(d) > RhinoMath.ZeroTolerance && corner.Unitize())
                    display.DrawPolyline(BaseSquare(BaseFrame(plane, corner), Math.Abs(d) * Math.Sqrt(2.0)), color);
            },
            out var halfDiagonal, out var cornerPick);
        if (rc != Result.Success)
            return rc;
        halfDiagonal = Math.Abs(halfDiagonal);

        var cornerDir = cornerPick.HasValue ? cornerPick.Value - center : cplane.XAxis + cplane.YAxis;
        if (!cornerDir.Unitize())
            cornerDir = cplane.XAxis;
        var baseFrame = BaseFrame(cplane, cornerDir);
        double side = halfDiagonal * Math.Sqrt(2.0);
        double tolerance = doc.ModelAbsoluteTolerance;

        // Height along the construction plane normal; picking below the base flips the pyramid.
        rc = FrepInput.GetLength("FPyramid", "Height", center, lastHeight,
            p => (p - center) * baseFrame.YAxis,
            gp => gp.Constrain(new Line(center, center + baseFrame.YAxis)),
            (display, _, h, color) =>
            {
                var preview = Math.Abs(h) > RhinoMath.ZeroTolerance ? CreateBrep(Orient(baseFrame, h), side, Math.Abs(h), tolerance) : null;
                if (preview != null) display.DrawBrepWires(preview, color);
            },
            out var height, out _);
        if (rc != Result.Success)
            return rc;

        var frame = Orient(baseFrame, height);
        height = Math.Abs(height);
        lastHalfDiagonal = halfDiagonal;
        lastHeight = height;

        return FrepInput.AddPrimitive(doc, "FPyramid", "pyramid", CreateBrep(frame, side, height, tolerance),
            a => WriteSdf(a, frame, height, side, doc.ModelUnitSystem));
    }

    /// <summary>Frame at the base center: Y = construction plane normal, X/Z along the base edges (45° from the corner).</summary>
    static Plane BaseFrame(Plane cplane, Vector3d cornerDir)
    {
        var x = cornerDir;
        x.Rotate(-Math.PI / 4.0, cplane.ZAxis);
        return FrepInput.AxisFrame(cplane.Origin, cplane.ZAxis, x);
    }

    /// <summary>Flips the frame upside down (about its X axis) for a negative height.</summary>
    static Plane Orient(Plane baseFrame, double height) =>
        height >= 0 ? baseFrame : new Plane(baseFrame.Origin, baseFrame.XAxis, -baseFrame.YAxis);

    static Point3d[] BaseCorners(Plane frame, double side)
    {
        double s = side * 0.5;
        return [frame.PointAt(-s, 0, -s), frame.PointAt(s, 0, -s), frame.PointAt(s, 0, s), frame.PointAt(-s, 0, s)];
    }

    static Polyline BaseSquare(Plane frame, double side)
    {
        var c = BaseCorners(frame, side);
        return new Polyline([c[0], c[1], c[2], c[3], c[0]]);
    }

    /// <summary>Closed solid: square base (local XZ plane) + 4 triangles meeting at the apex (0, h, 0).</summary>
    public static Brep? CreateBrep(Plane frame, double side, double height, double tolerance)
    {
        var c = BaseCorners(frame, side);
        var apex = frame.PointAt(0, height, 0);
        var faces = new[]
        {
            Brep.CreateFromCornerPoints(c[0], c[1], c[2], c[3], tolerance),
            Brep.CreateFromCornerPoints(c[0], c[1], apex, tolerance),
            Brep.CreateFromCornerPoints(c[1], c[2], apex, tolerance),
            Brep.CreateFromCornerPoints(c[2], c[3], apex, tolerance),
            Brep.CreateFromCornerPoints(c[3], c[0], apex, tolerance),
        };
        if (Array.Exists(faces, f => f == null))
            return null;

        var joined = Brep.JoinBreps(faces, tolerance);
        return joined is { Length: 1 } ? joined[0] : null;
    }

    /// <summary>sdPyramid: h = height along local Y, s = base side length; base centered on the local origin.</summary>
    public static void WriteSdf(ObjectAttributes attributes, Plane frame, double h, double s, UnitSystem units)
    {
        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteFloat(attributes, "h", h);
        SdfUserText.WriteFloat(attributes, "s", s);
    }

    /// <summary>
    /// Moves the frame, scales h along the axis and s across it. Works for move, rotate, mirror, uniform scale
    /// and scale along the axis. A non-square base can't be represented by sdPyramid, so that is rejected.
    /// </summary>
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!TryRead(attributes, out var frame, out var h, out var s))
        {
            reason = "missing or unreadable SDF.Frame.* / SDF.h / SDF.s";
            return false;
        }

        if (!TryTransform(frame, h, s, xform, out frame, out h, out s, out reason))
            return false;

        SdfUserText.WriteFrame(attributes, frame);
        SdfUserText.WriteFloat(attributes, "h", h);
        SdfUserText.WriteFloat(attributes, "s", s);
        return true;
    }

    /// <summary>Reads the frame (origin = base center, Y = toward apex), height h and base side s.</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out double h, out double s)
    {
        h = s = 0.0;
        return SdfUserText.TryReadFrame(attributes, out frame) &&
               SdfUserText.TryReadFloat(attributes, "h", out h) &&
               SdfUserText.TryReadFloat(attributes, "s", out s);
    }

    /// <summary>Pure version of <see cref="TransformSdf"/>; also usable for a live gumball preview.</summary>
    public static bool TryTransform(Plane frame, double h, double s, Transform xform, out Plane newFrame, out double newH, out double newS, out string? reason)
    {
        newH = h;
        newS = s;
        if (!SdfTransforms.TryTransformFrame(frame, xform, out newFrame, out var scale, out reason))
        {
            if (reason != null) reason += "; sdPyramid can't represent it";
            return false;
        }

        if (!SdfTransforms.NearlyEqual(scale.X, scale.Z))
        {
            reason = "non-uniform scale across the base makes it rectangular; sdPyramid can't represent it";
            return false;
        }

        newH = h * scale.Y;
        newS = s * (scale.X + scale.Z) * 0.5;
        return true;
    }
}
