using Rhino.Geometry;

namespace Freppuccino.Csg;

/// Reads warp reference geometry (Twist/Bend axis line, Repeat box) from Rhino objects.
public static class FrepRefs
{
    const double Tol = 1e-9;

    /// Any linear curve with nonzero length; start → end is the axis direction.
    public static bool TryLine(GeometryBase? g, out Line line)
    {
        line = default;
        if (g is not Curve c || !c.IsLinear(Rhino.RhinoMath.ZeroTolerance * 1e3)) return false;
        line = new Line(c.PointAtStart, c.PointAtEnd);
        return line.Length > Tol;
    }

    /// Box-shaped brep or extrusion (any orientation).
    public static bool TryBox(GeometryBase? g, out Box box)
    {
        box = default;
        var brep = g switch { Brep b => b, Extrusion e => e.ToBrep(), _ => null };
        if (brep == null || !brep.IsBox() || !brep.Faces[0].TryGetPlane(out var face)) return false;
        var z = face.Normal;
        foreach (var e in brep.Edges)
        {
            var x = e.PointAtEnd - e.PointAtStart;
            if (!x.Unitize() || Math.Abs(x * z) > 1e-6) continue;
            var frame = new Plane(brep.GetBoundingBox(false).Center, x, Vector3d.CrossProduct(z, x));
            brep.GetBoundingBox(frame, out box);
            return box.IsValid && box.X.Length > Tol && box.Y.Length > Tol && box.Z.Length > Tol;
        }
        return false;
    }

    public static bool Fits(FrepOp op, GeometryBase? g) =>
        op == FrepOp.Repeat ? TryBox(g, out _) : TryLine(g, out _);
}
