using System;
using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Freppuccino.Commands;

/// <summary>Shared prompts and document plumbing for the F-rep primitive commands.</summary>
internal static class FrepInput
{
    /// <summary>Picks a point; <paramref name="cplane"/> is the active construction plane moved to that point.</summary>
    public static Result GetPoint(string prompt, out Point3d point, out Plane cplane)
    {
        point = Point3d.Unset;
        cplane = Plane.WorldXY;

        var gp = new GetPoint();
        gp.SetCommandPrompt(prompt);
        if (gp.Get() != GetResult.Point)
            return gp.CommandResult();

        point = gp.Point();
        cplane = gp.View()?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
        cplane.Origin = point;
        return Result.Success;
    }

    /// <summary>
    /// Prompts for a length: pick a point (converted to a value by <paramref name="measure"/>), type a number,
    /// or press Enter to accept <paramref name="defaultValue"/>. Zero is rejected and the prompt repeats.
    /// <paramref name="picked"/> is the picked point, or null if the value was typed or defaulted.
    /// The value keeps its sign (typed or measured); callers that need a magnitude take Math.Abs.
    /// </summary>
    public static Result GetLength(
        string command,
        string prompt,
        Point3d basePoint,
        double defaultValue,
        Func<Point3d, double> measure,
        Action<GetPoint>? configure,
        Action<DisplayPipeline, Point3d, double, System.Drawing.Color>? preview,
        out double value,
        out Point3d? picked)
    {
        while (true)
        {
            value = 0.0;
            picked = null;

            var gp = new GetPoint();
            gp.SetCommandPrompt(prompt);
            gp.SetCommandPromptDefault(defaultValue.ToString("0.###", CultureInfo.InvariantCulture));
            gp.SetBasePoint(basePoint, true);
            gp.DrawLineFromPoint(basePoint, true);
            gp.AcceptNumber(true, false);
            gp.AcceptNothing(true);
            configure?.Invoke(gp);
            if (preview != null)
                gp.DynamicDraw += (_, e) => preview(e.Display, e.CurrentPoint, measure(e.CurrentPoint), gp.DynamicDrawColor);

            switch (gp.Get())
            {
                case GetResult.Point:
                    picked = gp.Point();
                    value = measure(picked.Value);
                    break;
                case GetResult.Number:
                    value = gp.Number();
                    break;
                case GetResult.Nothing:
                    value = defaultValue;
                    break;
                default:
                    return gp.CommandResult();
            }

            if (Math.Abs(value) > RhinoMath.ZeroTolerance)
                return Result.Success;

            RhinoApp.WriteLine($"{command}: value must not be zero.");
        }
    }

    /// <summary>
    /// Local frame for an axial primitive: Y = <paramref name="axis"/> (IQ's functions are Y-up),
    /// X = <paramref name="xHint"/> made perpendicular, or any perpendicular if the hint is missing/parallel.
    /// </summary>
    public static Plane AxisFrame(Point3d origin, Vector3d axis, Vector3d xHint)
    {
        axis.Unitize();
        var x = xHint - (xHint * axis) * axis;
        if (!x.Unitize())
        {
            x = Vector3d.Zero;
            x.PerpendicularTo(axis);
            x.Unitize();
        }
        return new Plane(origin, x, axis);
    }

    /// <summary>Adds <paramref name="brep"/> with fresh default attributes carrying the SDF metadata.</summary>
    public static Result AddPrimitive(RhinoDoc doc, string command, string noun, Brep? brep, Action<ObjectAttributes> writeSdf)
    {
        if (brep == null || !brep.IsValid)
        {
            RhinoApp.WriteLine($"{command}: unable to create {noun} geometry.");
            return Result.Failure;
        }

        var attributes = doc.CreateDefaultAttributes();
        writeSdf(attributes);

        if (doc.Objects.AddBrep(brep, attributes) == Guid.Empty)
        {
            RhinoApp.WriteLine($"{command}: unable to add {noun} to the document.");
            return Result.Failure;
        }

        doc.Views.Redraw();
        return Result.Success;
    }
}
