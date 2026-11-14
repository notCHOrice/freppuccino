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
/// Same interaction as Rhino's Torus command, plus sdTorus metadata in Attribute User Text:
///   SDF.Type = sdTorus, SDF.t.x = major radius, SDF.t.y = minor (tube) radius,
///   frame origin at the torus center, local Y = torus axis (IQ's torus lies in the local XZ plane).
/// See <see cref="SdfUserText"/>.
/// </summary>
[Guid("d82450bb-772b-4b35-9450-708057869891")]
public class FTorusCommand : Command
{
    public const string SdfType = "sdTorus";

    public override string EnglishName => "FTorus";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Torus torus;
        using (var gt = new GetTorus())
        {
            var rc = gt.Get(out torus);
            if (rc != Result.Success)
                return rc;
        }

        double major = Math.Abs(torus.MajorRadius), minor = Math.Abs(torus.MinorRadius);
        if (!torus.IsValid || major <= RhinoMath.ZeroTolerance || minor <= RhinoMath.ZeroTolerance)
        {
            RhinoApp.WriteLine("FTorus: torus is not valid.");
            return Result.Failure;
        }

        var frame = FrepInput.AxisFrame(torus.Plane.Origin, torus.Plane.ZAxis, torus.Plane.XAxis);
        return FrepInput.AddPrimitive(doc, "FTorus", "torus", torus.ToBrep(),
            a => WriteSdf(a, frame, major, minor, doc.ModelUnitSystem));
    }

    /// <summary>sdTorus(p, t): t.x = major radius, t.y = minor radius; centered on the local origin, axis = local Y.</summary>
    public static void WriteSdf(ObjectAttributes attributes, Plane frame, double major, double minor, UnitSystem units)
    {
        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteVec2(attributes, "t", major, minor);
    }

    /// <summary>
    /// Moves the frame and rescales t. Works for move, rotate, mirror and uniform scale.
    /// Non-uniform scale makes the ring or tube elliptical, which sdTorus can't represent, so it is rejected.
    /// </summary>
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!TryRead(attributes, out var frame, out var t))
        {
            reason = "missing or unreadable SDF.Frame.* / SDF.t.*";
            return false;
        }

        if (!TryTransform(frame, t, xform, out frame, out t, out reason))
            return false;

        SdfUserText.WriteFrame(attributes, frame);
        SdfUserText.WriteVec2(attributes, "t", t.X, t.Y);
        return true;
    }

    /// <summary>Reads the frame (origin = torus center, Y = axis) and t = (major, minor).</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out Vector2d t)
    {
        t = Vector2d.Unset;
        if (!SdfUserText.TryReadFrame(attributes, out frame) || !SdfUserText.TryReadVec2(attributes, "t", out var x, out var y))
            return false;
        t = new Vector2d(x, y);
        return true;
    }

    /// <summary>Pure version of <see cref="TransformSdf"/>; also usable for a live gumball preview.</summary>
    public static bool TryTransform(Plane frame, Vector2d t, Transform xform, out Plane newFrame, out Vector2d newT, out string? reason)
    {
        newT = t;
        if (!SdfTransforms.TryTransformFrame(frame, xform, out newFrame, out var scale, out reason))
        {
            if (reason != null) reason += "; sdTorus can't represent it";
            return false;
        }

        if (!SdfTransforms.NearlyEqual(scale.X, scale.Y) || !SdfTransforms.NearlyEqual(scale.Y, scale.Z))
        {
            reason = "non-uniform scale makes the torus elliptical; sdTorus can't represent it";
            return false;
        }

        double s = (scale.X + scale.Y + scale.Z) / 3.0;
        newT = new Vector2d(t.X * s, t.Y * s);
        return true;
    }
}
