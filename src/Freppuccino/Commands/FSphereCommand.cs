using System;
using System.Globalization;
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
/// Center + radius sphere (like Rhino's Sphere command, default mode), plus sdSphere metadata in
/// Attribute User Text: SDF.Type = sdSphere, SDF.s = radius, frame origin at the center and axes
/// from the active construction plane. See <see cref="SdfUserText"/>.
/// </summary>
[Guid("8c2a6f3e-1d47-4b9a-a5e2-6f0d3c7b9e41")]
public class FSphereCommand : Command
{
    public const string SdfType = "sdSphere";

    // Remembered between runs, like Rhino's own commands.
    static double lastRadius = 1.0;
    static bool useDiameter;

    public override string EnglishName => "FSphere";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var gc = new GetPoint();
        gc.SetCommandPrompt("Center of sphere");
        if (gc.Get() != GetResult.Point)
            return gc.CommandResult();

        var frame = gc.View()?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
        frame.Origin = gc.Point();

        var rc = GetRadius(frame, out var radius);
        if (rc != Result.Success)
            return rc;

        var sphere = new Sphere(frame, radius);
        var brep = sphere.IsValid ? sphere.ToBrep() : null;
        if (brep == null)
        {
            RhinoApp.WriteLine("FSphere: unable to create sphere geometry.");
            return Result.Failure;
        }

        var attributes = doc.CreateDefaultAttributes();
        WriteSdf(attributes, frame, radius, doc.ModelUnitSystem);

        if (doc.Objects.AddBrep(brep, attributes) == Guid.Empty)
        {
            RhinoApp.WriteLine("FSphere: unable to add sphere to the document.");
            return Result.Failure;
        }

        doc.Views.Redraw();
        return Result.Success;
    }

    /// <summary>Radius (or diameter) by picking a point or typing a number; Enter accepts the default.</summary>
    static Result GetRadius(Plane frame, out double radius)
    {
        radius = 0.0;
        var center = frame.Origin;

        while (true)
        {
            var label = useDiameter ? "Diameter" : "Radius";
            var shown = useDiameter ? 2.0 * lastRadius : lastRadius;

            var gp = new GetPoint();
            gp.SetCommandPrompt(label);
            gp.SetCommandPromptDefault(shown.ToString("0.###", CultureInfo.InvariantCulture));
            gp.SetBasePoint(center, true);
            gp.DrawLineFromPoint(center, true);
            gp.AcceptNumber(true, false);
            gp.AcceptNothing(true);
            var toggle = gp.AddOption(useDiameter ? "Radius" : "Diameter");
            gp.DynamicDraw += (_, e) =>
            {
                double r = center.DistanceTo(e.CurrentPoint);
                if (useDiameter) r *= 0.5;
                if (r > RhinoMath.ZeroTolerance)
                    e.Display.DrawSphere(new Sphere(frame, r), gp.DynamicDrawColor);
            };

            switch (gp.Get())
            {
                case GetResult.Point:
                    radius = center.DistanceTo(gp.Point());
                    if (useDiameter) radius *= 0.5;
                    break;
                case GetResult.Number:
                    radius = Math.Abs(gp.Number());
                    if (useDiameter) radius *= 0.5;
                    break;
                case GetResult.Nothing:
                    radius = lastRadius;
                    break;
                case GetResult.Option when gp.OptionIndex() == toggle:
                    useDiameter = !useDiameter;
                    continue;
                default:
                    return gp.CommandResult();
            }

            if (radius <= RhinoMath.ZeroTolerance)
            {
                RhinoApp.WriteLine("FSphere: radius must be greater than zero.");
                continue;
            }

            lastRadius = radius;
            return Result.Success;
        }
    }

    /// <summary>sdSphere(p, s): s is the radius; the sphere is centered on the local origin.</summary>
    public static void WriteSdf(ObjectAttributes attributes, Plane frame, double radius, UnitSystem units)
    {
        SdfUserText.WriteHeader(attributes, SdfType, frame, units);
        SdfUserText.WriteFloat(attributes, "s", radius);
    }

    /// <summary>
    /// Moves the frame and rescales s. Works for move, rotate, mirror and uniform scale.
    /// Anything else (non-uniform scale in any direction, shear) makes an ellipsoid, so the object is
    /// rewritten as sdEllipsoid (SDF.s replaced by SDF.r.*).
    /// </summary>
    public static bool TransformSdf(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        if (!TryRead(attributes, out var frame, out var s))
        {
            reason = "missing or unreadable SDF.Frame.* / SDF.s";
            return false;
        }

        if (TryTransform(frame, s, xform, out var newFrame, out var newS, out _))
        {
            SdfUserText.WriteFrame(attributes, newFrame);
            SdfUserText.WriteFloat(attributes, "s", newS);
            reason = null;
            return true;
        }

        if (!TryTransformToEllipsoid(frame, s, xform, out newFrame, out var r, out reason))
            return false;

        attributes.DeleteUserString(SdfUserText.Prefix + "s");
        attributes.SetUserString(SdfUserText.TypeKey, FEllipsoidCommand.SdfType);
        SdfUserText.WriteFrame(attributes, newFrame);
        SdfUserText.WriteVec3(attributes, "r", r.X, r.Y, r.Z);
        return true;
    }

    /// <summary>The ellipsoid a sphere becomes under a non-uniform transform; fails only if it flattens.</summary>
    public static bool TryTransformToEllipsoid(Plane frame, double s, Transform xform, out Plane newFrame, out Vector3d r, out string? reason) =>
        SdfTransforms.TryTransformEllipsoid(frame, new Vector3d(s, s, s), xform, out newFrame, out r, out reason);

    /// <summary>Reads the frame (origin = sphere center) and radius s.</summary>
    public static bool TryRead(ObjectAttributes attributes, out Plane frame, out double s)
    {
        s = 0.0;
        return SdfUserText.TryReadFrame(attributes, out frame) && SdfUserText.TryReadFloat(attributes, "s", out s);
    }

    /// <summary>Pure version of <see cref="TransformSdf"/>; also used for the live gumball preview.</summary>
    public static bool TryTransform(Plane frame, double s, Transform xform, out Plane newFrame, out double newS, out string? reason)
    {
        newS = s;
        if (!SdfTransforms.TryTransformFrame(frame, xform, out newFrame, out var scale, out reason))
        {
            if (reason != null) reason += "; sdSphere can't represent it";
            return false;
        }

        double max = Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
        double min = Math.Min(scale.X, Math.Min(scale.Y, scale.Z));
        const double uniformTolerance = 1e-6;
        if (max - min > uniformTolerance * max)
        {
            reason = "non-uniform scale turns the sphere into an ellipsoid";
            return false;
        }

        newS = s * (scale.X + scale.Y + scale.Z) / 3.0;
        return true;
    }
}
