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

    /// A factor L such that |field| <= L × distance to the surface, which lets the mesher skip space that
    /// is provably empty. 1 for exact distance functions and their unions, intersections and differences.
    /// The ellipsoid is not a distance and overestimates it; 1.5 × the ratio of its semi-axes is used
    /// (measured |field| / distance: up to 3.6 at ratio 6, and below the ratio in every case tried).
    public double Lipschitz
    {
        get
        {
            double l = 1;
            foreach (var p in prims)
                if (p.Kind == FieldKind.Ellipsoid)
                    l = Math.Max(l, 1.5 * Math.Max(p.P.X, Math.Max(p.P.Y, p.P.Z)) / Math.Min(p.P.X, Math.Min(p.P.Y, p.P.Z)));
            return l;
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
            case FieldKind.Ellipsoid: // a bound, not exact
            {
                double k0 = Len(x / s.P.X, y / s.P.Y, z / s.P.Z);
                double k1 = Len(x / (s.P.X * s.P.X), y / (s.P.Y * s.P.Y), z / (s.P.Z * s.P.Z));
                return k1 > 0 ? k0 * (k0 - 1) / k1 : -Math.Min(s.P.X, Math.Min(s.P.Y, s.P.Z));
            }
            case FieldKind.Pyramid: // P.X = height, P.Y = base side; base centred on the origin, apex at +Y
            {
                double side = s.P.Y, h = s.P.X / side;
                double px = Math.Abs(x / side), py = y / side, pz = Math.Abs(z / side);
                double m2 = h * h + 0.25;
                if (pz > px) (px, pz) = (pz, px);
                px -= 0.5; pz -= 0.5;
                double qx = pz, qy = h * py - 0.5 * px, qz = h * px + 0.5 * py;
                double a0 = Math.Max(-qx, 0);
                double t = Math.Clamp((qy - 0.5 * pz) / (m2 + 0.25), 0, 1);
                double a = m2 * (qx + a0) * (qx + a0) + qy * qy;
                double b = m2 * (qx + 0.5 * t) * (qx + 0.5 * t) + (qy - m2 * t) * (qy - m2 * t);
                double d2 = Math.Min(qy, -qx * m2 - qy * 0.5) > 0 ? 0 : Math.Min(a, b);
                // The shader's version ends with sign(max(q.z, -p.y)): inside, it measures distance to the
                // slanted faces only, so its value jumps across the base. That is harmless when raymarching
                // from outside but breaks meshing, which needs the sign to change only where the value passes
                // through zero. Same surface, continuous across the base: slanted faces ∩ half-space y >= 0.
                double slanted = Math.Sqrt((d2 + qz * qz) / m2) * (qz < 0 ? -1 : 1);
                return Math.Max(slanted, -py) * side;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(s));
        }
    }

    static double Len(double a, double b) => Math.Sqrt(a * a + b * b);
    static double Len(double a, double b, double c) => Math.Sqrt(a * a + b * b + c * c);

    /// World-axis bounds containing the surface. Intersections and differences only remove material, so
    /// the primitives' bounds suffice for them. A smooth union bulges beyond its operands: by at most
    /// k/4 for two, and for more the deficit x below the plain minimum compounds as x' = x + (k - x)²/4k,
    /// which approaches but never exceeds k. Nested unions add up, so pad by the sum over union nodes.
    public (V3 Min, V3 Max) Bounds()
    {
        Seal();
        double inf = double.PositiveInfinity;
        V3 min = new(inf, inf, inf), max = new(-inf, -inf, -inf);
        foreach (var s in prims)
        {
            // Local half-extents, and the offset of the local box's centre along Y.
            var (e, up) = s.Kind switch
            {
                FieldKind.Sphere => (new V3(s.P.X, s.P.X, s.P.X), 0.0),
                FieldKind.Torus => (new V3(s.P.X + s.P.Y, s.P.Y, s.P.X + s.P.Y), 0.0),
                FieldKind.Cylinder => (new V3(s.P.Y, s.P.X, s.P.Y), 0.0),
                FieldKind.Cone => (new V3(Math.Max(s.P.Y, s.P.Z), s.P.X, Math.Max(s.P.Y, s.P.Z)), 0.0),
                FieldKind.Pyramid => (new V3(s.P.Y * 0.5, s.P.X * 0.5, s.P.Y * 0.5), s.P.X * 0.5),
                _ => (s.P, 0.0),
            };
            var c = s.O + s.Y * up;
            var w = new V3(
                Math.Abs(s.X.X) * e.X + Math.Abs(s.Y.X) * e.Y + Math.Abs(s.Z.X) * e.Z,
                Math.Abs(s.X.Y) * e.X + Math.Abs(s.Y.Y) * e.Y + Math.Abs(s.Z.Y) * e.Z,
                Math.Abs(s.X.Z) * e.X + Math.Abs(s.Y.Z) * e.Y + Math.Abs(s.Z.Z) * e.Z);
            min = new(Math.Min(min.X, c.X - w.X), Math.Min(min.Y, c.Y - w.Y), Math.Min(min.Z, c.Z - w.Z));
            max = new(Math.Max(max.X, c.X + w.X), Math.Max(max.Y, c.Y + w.Y), Math.Max(max.Z, c.Z + w.Z));
        }
        double bulge = 0;
        foreach (var n in nodes)
            if (n.Op == FieldOp.Union && n.Operands.Length > 1)
                bulge += Math.Max(n.K, 0) * (n.Operands.Length > 2 ? 1.0 : 0.25);
        var pad = new V3(bulge, bulge, bulge);
        return (min - pad, max + pad);
    }
}
