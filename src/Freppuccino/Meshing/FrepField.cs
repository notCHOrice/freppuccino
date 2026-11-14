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

/// CPU evaluator for the same field the viewport shader raymarches (see Shaders/FrepShaderGen.cs):
/// spheres then boxes, folded left to right with IQ's quadratic smooth-min. Keep the two in sync.
/// The field is 1-Lipschitz (exact primitive SDFs, and smin's gradient is a convex combination),
/// which <see cref="DualContour"/> relies on to skip empty space.
public sealed class FrepField
{
    readonly record struct Sphere(V3 C, double R);
    /// Centre, unit axes, half-extents along those axes.
    readonly record struct Box(V3 C, V3 X, V3 Y, V3 Z, V3 B);

    readonly List<Sphere> spheres = [];
    readonly List<Box> boxes = [];

    /// Smooth-union blend distance k; 0 = hard union.
    public double Blend { get; init; }

    public int Count => spheres.Count + boxes.Count;

    public void AddSphere(V3 center, double radius) => spheres.Add(new Sphere(center, radius));

    /// <paramref name="x"/> and <paramref name="y"/> are unit and perpendicular; Z = X × Y.
    public void AddBox(V3 center, V3 x, V3 y, V3 half) => boxes.Add(new Box(center, x, y, V3.Cross(x, y), half));

    public double Value(V3 p)
    {
        double d = double.NaN;
        bool first = true;
        foreach (var s in spheres) { var v = (p - s.C).Length - s.R; d = first ? v : Smin(d, v); first = false; }
        foreach (var b in boxes) { var v = BoxValue(p, b, out _, false); d = first ? v : Smin(d, v); first = false; }
        return d;
    }

    /// Value and analytic gradient. At a hard crease the gradient is that of the nearer branch.
    public double Value(V3 p, out V3 grad)
    {
        double d = double.NaN;
        grad = default;
        bool first = true;
        foreach (var s in spheres)
        {
            var r = p - s.C;
            var len = r.Length;
            Fold(len - s.R, len > 0 ? r * (1 / len) : new V3(0, 0, 1), ref d, ref grad, ref first);
        }
        foreach (var b in boxes)
        {
            var v = BoxValue(p, b, out var g, true);
            Fold(v, g, ref d, ref grad, ref first);
        }
        return d;
    }

    void Fold(double v, V3 g, ref double d, ref V3 grad, ref bool first)
    {
        if (first) { d = v; grad = g; first = false; return; }
        // smin(a, b) = min(a, b) - h²k/4, h = max(k - |a - b|, 0)/k  =>  gradient = mix(nearer, farther, h/2)
        double k = Blend;
        double h = k > 0 ? Math.Max(k - Math.Abs(d - v), 0) / k : 0;
        var (near, far) = d < v ? (grad, g) : (g, grad);
        grad = near * (1 - h * 0.5) + far * (h * 0.5);
        d = Math.Min(d, v) - h * h * k * 0.25;
    }

    double Smin(double a, double b)
    {
        double k = Blend;
        if (k <= 0) return Math.Min(a, b);
        double h = Math.Max(k - Math.Abs(a - b), 0) / k;
        return Math.Min(a, b) - h * h * k * 0.25;
    }

    static double BoxValue(V3 p, in Box b, out V3 grad, bool wantGrad)
    {
        var d = p - b.C;
        double lx = V3.Dot(d, b.X), ly = V3.Dot(d, b.Y), lz = V3.Dot(d, b.Z);
        double qx = Math.Abs(lx) - b.B.X, qy = Math.Abs(ly) - b.B.Y, qz = Math.Abs(lz) - b.B.Z;
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0), oz = Math.Max(qz, 0);
        double outside = Math.Sqrt(ox * ox + oy * oy + oz * oz);
        double inside = Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0);
        grad = default;
        if (wantGrad)
        {
            double gx = 0, gy = 0, gz = 0;
            if (outside > 0) { gx = ox / outside; gy = oy / outside; gz = oz / outside; }
            else if (qx >= qy && qx >= qz) gx = 1;
            else if (qy >= qz) gy = 1;
            else gz = 1;
            grad = b.X * (lx < 0 ? -gx : gx) + b.Y * (ly < 0 ? -gy : gy) + b.Z * (lz < 0 ? -gz : gz);
        }
        return outside + inside;
    }

    /// World-axis bounds of the zero set. One smooth union bulges at most k/4 beyond its two operands.
    /// Folding more primitives in sequence compounds: the deficit x below the plain minimum grows as
    /// x' = x + (k - x)²/4k, which approaches but never exceeds k.
    public (V3 Min, V3 Max) Bounds()
    {
        double inf = double.PositiveInfinity;
        V3 min = new(inf, inf, inf), max = new(-inf, -inf, -inf);
        void Grow(V3 c, V3 e)
        {
            min = new(Math.Min(min.X, c.X - e.X), Math.Min(min.Y, c.Y - e.Y), Math.Min(min.Z, c.Z - e.Z));
            max = new(Math.Max(max.X, c.X + e.X), Math.Max(max.Y, c.Y + e.Y), Math.Max(max.Z, c.Z + e.Z));
        }
        foreach (var s in spheres) Grow(s.C, new V3(s.R, s.R, s.R));
        foreach (var b in boxes)
            Grow(b.C, new V3(
                Math.Abs(b.X.X) * b.B.X + Math.Abs(b.Y.X) * b.B.Y + Math.Abs(b.Z.X) * b.B.Z,
                Math.Abs(b.X.Y) * b.B.X + Math.Abs(b.Y.Y) * b.B.Y + Math.Abs(b.Z.Y) * b.B.Z,
                Math.Abs(b.X.Z) * b.B.X + Math.Abs(b.Y.Z) * b.B.Y + Math.Abs(b.Z.Z) * b.B.Z));
        double bulge = Math.Max(Blend, 0) * (Count > 2 ? 1.0 : 0.25);
        var pad = new V3(bulge, bulge, bulge);
        return (min - pad, max + pad);
    }
}
