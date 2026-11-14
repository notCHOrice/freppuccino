namespace Freppuccino.Meshing;

/// Minimal double-precision vector for the meshing core, which deliberately has no Rhino dependency
/// (so it can be tested outside Rhino).
public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Length => Math.Sqrt(Dot(this, this));
    public V3 Normalized() { var l = Length; return l > 0 ? this * (1 / l) : this; }
}

/// Same kinds and parameter packing as Scene.SdKind / SdPrimitive.P.
public enum FieldKind { Sphere, Box, Torus, Cylinder, Cone, Ellipsoid, Pyramid }

public enum FieldOp { Union, Intersect, Difference }

/// A primitive (by index) or an earlier node (by index).
public readonly record struct FieldRef(bool IsNode, int Index)
{
    public static FieldRef Primitive(int index) => new(false, index);
    public static FieldRef Node(int index) => new(true, index);
}

/// CPU evaluator for the same field the viewport shader raymarches (see Shaders/FrepShaderGen.cs):
/// IQ primitive distance functions in their local frames, combined by CSG nodes with IQ's quadratic
/// smooth min/max, and a hard minimum over the roots. Keep the two in sync.
///
/// Every primitive here changes by at most 1 per unit distance, and the CSG operations preserve that,
/// so |field| <= distance to the surface: the mesher relies on it to skip empty space. That is why
/// the ellipsoid and pyramid, here and in the shader, are not IQ's formulas (see PrimValue).
///
/// Build it with <see cref="AddPrimitive"/>, <see cref="AddNode"/> and <see cref="AddRoot"/>. With no
/// roots declared, all primitives are smooth-unioned in order with <see cref="Blend"/>.
public sealed class FrepField
{
    readonly record struct Prim(FieldKind Kind, V3 O, V3 X, V3 Y, V3 Z, V3 P);
    readonly record struct Node(FieldOp Op, double K, FieldRef[] Operands);

    readonly List<Prim> prims = [];
    readonly List<Node> nodes = [];
    readonly List<FieldRef> roots = [];
    readonly object sealing = new();
    volatile bool isSealed;

    /// Blend distance of the implicit union used when no roots are declared; 0 = hard union.
    public double Blend { get; init; }

    public int Count => prims.Count;

    /// <paramref name="x"/> and <paramref name="y"/> are unit and perpendicular; Z = X × Y.
    public int AddPrimitive(FieldKind kind, V3 origin, V3 x, V3 y, V3 p)
    {
        prims.Add(new Prim(kind, origin, x, y, V3.Cross(x, y), p));
        return prims.Count - 1;
    }

    public int AddSphere(V3 center, double radius) =>
        AddPrimitive(FieldKind.Sphere, center, new V3(1, 0, 0), new V3(0, 1, 0), new V3(radius, 0, 0));

    public int AddBox(V3 center, V3 x, V3 y, V3 half) => AddPrimitive(FieldKind.Box, center, x, y, half);

    /// Difference is operands[0] minus the rest. Operands must already exist. At least one operand.
    public int AddNode(FieldOp op, double k, IEnumerable<FieldRef> operands)
    {
        nodes.Add(new Node(op, k, [.. operands]));
        return nodes.Count - 1;
    }

    public void AddRoot(FieldRef root) => roots.Add(root);

    public bool HasRoots => roots.Count > 0;

    void Seal()
    {
        if (isSealed) return;
        lock (sealing)
        {
            if (isSealed) return;
            if (roots.Count == 0 && prims.Count > 0)
                roots.Add(FieldRef.Node(AddNode(FieldOp.Union, Blend, Enumerable.Range(0, prims.Count).Select(FieldRef.Primitive))));
            isSealed = true;
        }
    }

    public double Value(V3 p)
    {
        Seal();
        int np = prims.Count, nn = nodes.Count;
        Span<double> v = np + nn <= 256 ? stackalloc double[np + nn] : new double[np + nn];
        for (int i = 0; i < np; i++) v[i] = PrimValue(prims[i], p);
        for (int i = 0; i < nn; i++)
        {
            var n = nodes[i];
            double d = Get(v, np, n.Operands[0]);
            for (int o = 1; o < n.Operands.Length; o++)
            {
                double t = Get(v, np, n.Operands[o]);
                d = n.Op switch
                {
                    FieldOp.Union => Smin(d, t, n.K),
                    FieldOp.Intersect => -Smin(-d, -t, n.K),
                    _ => -Smin(-d, t, n.K),
                };
            }
            v[np + i] = d;
        }
        double result = double.PositiveInfinity;
        foreach (var r in roots) result = Math.Min(result, Get(v, np, r));
        return result;
    }

    static double Get(ReadOnlySpan<double> v, int np, FieldRef r) => v[r.IsNode ? np + r.Index : r.Index];

    /// Gradient by central differences over <paramref name="step"/>. At a crease it mixes both sides, but
    /// only within <paramref name="step"/> of the crease.
    public V3 Gradient(V3 p, double step)
    {
        double i = 0.5 / step;
        return new V3(
            (Value(new V3(p.X + step, p.Y, p.Z)) - Value(new V3(p.X - step, p.Y, p.Z))) * i,
            (Value(new V3(p.X, p.Y + step, p.Z)) - Value(new V3(p.X, p.Y - step, p.Z))) * i,
            (Value(new V3(p.X, p.Y, p.Z + step)) - Value(new V3(p.X, p.Y, p.Z - step))) * i);
    }

    /// IQ quadratic smooth minimum; k = blend distance, 0 = hard.
    static double Smin(double a, double b, double k)
    {
        if (k <= 0) return Math.Min(a, b);
        double h = Math.Max(k - Math.Abs(a - b), 0) / k;
        return Math.Min(a, b) - h * h * k * 0.25;
    }

    // IQ, https://iquilezles.org/articles/distfunctions/, ported from the GLSL in FrepShaderGen (local space, Y-up).
    static double PrimValue(in Prim s, V3 world)
    {
        var d = world - s.O;
        if (s.Kind == FieldKind.Sphere) return d.Length - s.P.X;
        double x = V3.Dot(d, s.X), y = V3.Dot(d, s.Y), z = V3.Dot(d, s.Z);
        switch (s.Kind)
        {
            case FieldKind.Box:
            {
                double qx = Math.Abs(x) - s.P.X, qy = Math.Abs(y) - s.P.Y, qz = Math.Abs(z) - s.P.Z;
                return Len(Math.Max(qx, 0), Math.Max(qy, 0), Math.Max(qz, 0)) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
            }
            case FieldKind.Torus:
                return Len(Len(x, z) - s.P.X, y) - s.P.Y;
            case FieldKind.Cylinder: // P.X = half height, P.Y = radius
            {
                double dx = Len(x, z) - s.P.Y, dy = Math.Abs(y) - s.P.X;
                return Math.Min(Math.Max(dx, dy), 0) + Len(Math.Max(dx, 0), Math.Max(dy, 0));
            }
            case FieldKind.Cone: // P.X = half height, P.Y = radius at -h, P.Z = radius at +h
            {
                double h = s.P.X, r1 = s.P.Y, r2 = s.P.Z;
                double qx = Len(x, z), qy = y;
                double k2x = r2 - r1, k2y = 2 * h;
                double cax = qx - Math.Min(qx, qy < 0 ? r1 : r2), cay = Math.Abs(qy) - h;
                double t = Math.Clamp(((r2 - qx) * k2x + (h - qy) * k2y) / (k2x * k2x + k2y * k2y), 0, 1);
                double cbx = qx - r2 + k2x * t, cby = qy - h + k2y * t;
                double sign = cbx < 0 && cay < 0 ? -1 : 1;
                return sign * Math.Sqrt(Math.Min(cax * cax + cay * cay, cbx * cbx + cby * cby));
            }
            case FieldKind.Ellipsoid:
            {
                // A unit sphere scaled by r, times the smallest semi-axis: exact surface, and it changes by
                // at most 1 per unit distance. IQ's k0 (k0 - 1) / k1 is a better distance estimate, but
                // its value at the centre depends on the direction of approach; under a smooth CSG node
                // that becomes a sign jump.
                double k0 = Len(x / s.P.X, y / s.P.Y, z / s.P.Z);
                return (k0 - 1) * Math.Min(s.P.X, Math.Min(s.P.Y, s.P.Z));
            }
            case FieldKind.Pyramid: // P.X = height, P.Y = base side; base centred on the origin, apex at +Y
            {
                // The five face planes' signed distances combined by max (the pyramid is their
                // intersection): exact surface, changing by at most 1 per unit distance. IQ's sdPyramid
                // jumps in value across the base (inside, it measures distance to the slanted faces only).
                double h = s.P.X, half = s.P.Y * 0.5;
                double slanted = (h * Math.Max(Math.Abs(x), Math.Abs(z)) + half * (y - h)) / Len(h, half);
                return Math.Max(slanted, -y);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(s));
        }
    }

    static double Len(double a, double b) => Math.Sqrt(a * a + b * b);
    static double Len(double a, double b, double c) => Math.Sqrt(a * a + b * b + c * c);

    /// World-axis bounds containing the surface. Empty (Min > Max on some axis) if there is none.
    ///
    /// Computed as bounds of sublevel sets {field <= t}, from the roots at t = 0 down the CSG tree, so
    /// that cutters, intersected-away regions and primitives no root reaches do not inflate them:
    ///   union:      smin >= min - d, so {smin <= t} lies in the union of the operands' {<= t + d}, where
    ///               d = k/4 for two operands and k for more (the deficit below the plain minimum
    ///               compounds as x' = x + (k - x)²/4k, which approaches but never exceeds k);
    ///   intersect:  smax >= max, so it lies in the overlap of the operands' {<= t};
    ///   difference: it lies in the base's {<= t}.
    /// The offset is in field value, not distance: a primitive whose field underestimates distance
    /// (ellipsoid, pyramid) grows by more than t, which <see cref="PrimBounds"/> accounts for.
    public (V3 Min, V3 Max) Bounds()
    {
        Seal();
        var all = Empty;
        foreach (var r in roots) all = Join(all, Sublevel(r, 0));
        return all;
    }

    static readonly (V3 Min, V3 Max) Empty = (
        new V3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new V3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    static bool IsEmpty((V3 Min, V3 Max) b) => !(b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z);

    static (V3 Min, V3 Max) Join((V3 Min, V3 Max) a, (V3 Min, V3 Max) b) => (
        new V3(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
        new V3(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)));

    /// Bounds of {field of <paramref name="r"/> <= <paramref name="t"/>}, t >= 0.
    (V3 Min, V3 Max) Sublevel(FieldRef r, double t)
    {
        if (!r.IsNode) return PrimBounds(prims[r.Index], t);
        var n = nodes[r.Index];
        if (n.Op == FieldOp.Difference) return Sublevel(n.Operands[0], t);
        if (n.Op == FieldOp.Union)
        {
            double d = n.Operands.Length < 2 ? 0 : Math.Max(n.K, 0) * (n.Operands.Length > 2 ? 1.0 : 0.25);
            var box = Empty;
            foreach (var o in n.Operands) box = Join(box, Sublevel(o, t + d));
            return box;
        }
        var overlap = Sublevel(n.Operands[0], t);
        for (int o = 1; o < n.Operands.Length; o++)
        {
            var other = Sublevel(n.Operands[o], t);
            overlap = (
                new V3(Math.Max(overlap.Min.X, other.Min.X), Math.Max(overlap.Min.Y, other.Min.Y), Math.Max(overlap.Min.Z, other.Min.Z)),
                new V3(Math.Min(overlap.Max.X, other.Max.X), Math.Min(overlap.Max.Y, other.Max.Y), Math.Min(overlap.Max.Z, other.Max.Z)));
        }
        return IsEmpty(overlap) ? Empty : overlap;
    }

    /// World-axis bounds of {primitive field <= t}, t >= 0, from a box in the primitive's local frame.
    static (V3 Min, V3 Max) PrimBounds(in Prim s, double t)
    {
        // Local box: low and high corner. Exact distance fields grow by t in every direction.
        V3 lo, hi;
        switch (s.Kind)
        {
            case FieldKind.Sphere: hi = new V3(s.P.X + t, s.P.X + t, s.P.X + t); lo = hi * -1; break;
            case FieldKind.Box: hi = new V3(s.P.X + t, s.P.Y + t, s.P.Z + t); lo = hi * -1; break;
            case FieldKind.Torus: hi = new V3(s.P.X + s.P.Y + t, s.P.Y + t, s.P.X + s.P.Y + t); lo = hi * -1; break;
            case FieldKind.Cylinder: hi = new V3(s.P.Y + t, s.P.X + t, s.P.Y + t); lo = hi * -1; break;
            case FieldKind.Cone:
            {
                double r = Math.Max(s.P.Y, s.P.Z) + t;
                hi = new V3(r, s.P.X + t, r); lo = hi * -1;
                break;
            }
            case FieldKind.Ellipsoid:
            {
                // (|p/r| - 1) min(r) <= t  <=>  |p/r| <= 1 + t / min(r): the semi-axes scale.
                double scale = 1 + t / Math.Min(s.P.X, Math.Min(s.P.Y, s.P.Z));
                hi = s.P * scale; lo = hi * -1;
                break;
            }
            case FieldKind.Pyramid:
            {
                // -y <= t and (h m + b (y - h)) / L <= t with m = max(|x|, |z|), b = half the base side:
                // y in [-t, h + t L / b], and at y = -t, m <= b + t (L + b) / h.
                double h = s.P.X, b = s.P.Y * 0.5, len = Len(h, b);
                double m = b + t * (len + b) / h;
                lo = new V3(-m, -t, -m); hi = new V3(m, h + t * len / b, m);
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(s));
        }
        var mid = (lo + hi) * 0.5;
        var e = (hi - lo) * 0.5;
        var c = s.O + s.X * mid.X + s.Y * mid.Y + s.Z * mid.Z;
        var w = new V3(
            Math.Abs(s.X.X) * e.X + Math.Abs(s.Y.X) * e.Y + Math.Abs(s.Z.X) * e.Z,
            Math.Abs(s.X.Y) * e.X + Math.Abs(s.Y.Y) * e.Y + Math.Abs(s.Z.Y) * e.Z,
            Math.Abs(s.X.Z) * e.X + Math.Abs(s.Y.Z) * e.Y + Math.Abs(s.Z.Z) * e.Z);
        return (c - w, c + w);
    }
}
