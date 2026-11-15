using System.Collections.Concurrent;

namespace Freppuccino.Meshing;

/// Finds pairs of triangles that overlap other than where a mesh allows them to meet:
/// - no shared vertex: they intersect or touch at all;
/// - one shared vertex: they overlap along more than that vertex (their intersection segments, or for coplanar
///   triangles their overlap area, extend beyond it);
/// - one shared edge: they are coplanar and lie on the same side of it (one folded flat onto the other);
/// - three shared vertices: the same triangle twice.
/// These are the cases Rhino's mesh check counts as self-intersections (the tests follow sol's reference checker
/// from the gyroid review). Tolerances are relative to the cell size.
///
/// Triangles are binned on a uniform grid of about two cells, so a triangle is only tested against those near it.
public static class Crossings
{
    /// Every overlapping pair in the mesh, as (lower, higher) triangle index.
    public static List<(int A, int B)> All(IReadOnlyList<V3> vertices, IReadOnlyList<int> tris, double h)
    {
        var bins = new Bins(vertices, tris, h);
        double eps = Eps(h);
        var found = new ConcurrentBag<(int, int)>();
        Parallel.ForEach(bins.Cells, list =>
        {
            for (int p = 0; p < list.Count; p++)
                for (int q = p + 1; q < list.Count; q++)
                {
                    int i = list[p], j = list[q];
                    // Test each pair once: in the bin that holds the low corner of the overlap of their bin ranges.
                    if (!bins.Owns(i, j, list) || !Overlap(vertices, tris, i, j, eps)) continue;
                    found.Add(i < j ? (i, j) : (j, i));
                }
        });
        var result = found.ToList();
        result.Sort();
        return result;
    }

    /// Overlapping pairs that involve at least one of <paramref name="candidates"/>, against every triangle near it.
    public static List<(int A, int B)> Involving(IReadOnlyList<V3> vertices, IReadOnlyList<int> tris, double h, IReadOnlyCollection<int> candidates)
    {
        if (candidates.Count == 0) return [];
        var bins = new Bins(vertices, tris, h);
        double eps = Eps(h);
        var found = new ConcurrentDictionary<(int, int), byte>();
        Parallel.ForEach(candidates, i =>
        {
            var seen = new HashSet<int>();
            foreach (var list in bins.Near(i))
                foreach (int j in list)
                {
                    if (j == i || !seen.Add(j)) continue;
                    if (Overlap(vertices, tris, i, j, eps)) found.TryAdd(i < j ? (i, j) : (j, i), 0);
                }
        });
        var result = found.Keys.ToList();
        result.Sort();
        return result;
    }

    static double Eps(double h) => 1e-10 * Math.Max(h, 1e-3);

    /// Do triangles i and j overlap beyond what they share?
    public static bool Overlap(IReadOnlyList<V3> vs, IReadOnlyList<int> t, int i, int j, double eps)
    {
        V3 a = vs[t[3 * i]], b = vs[t[3 * i + 1]], c = vs[t[3 * i + 2]];
        V3 d = vs[t[3 * j]], e = vs[t[3 * j + 1]], f = vs[t[3 * j + 2]];
        if (!BoxesTouch(a, b, c, d, e, f, eps)) return false;
        Span<V3> common = stackalloc V3[3];
        int shared = 0;
        for (int p = 0; p < 3; p++)
            for (int q = 0; q < 3; q++)
                if (t[3 * i + p] == t[3 * j + q]) common[shared++] = vs[t[3 * i + p]];
        return shared == 0 ? Intersect(a, b, c, d, e, f, eps) : Incident(a, b, c, d, e, f, common[..shared], eps);
    }

    static bool BoxesTouch(V3 a, V3 b, V3 c, V3 d, V3 e, V3 f, double eps) =>
        Math.Min(a.X, Math.Min(b.X, c.X)) <= Math.Max(d.X, Math.Max(e.X, f.X)) + eps && Math.Min(d.X, Math.Min(e.X, f.X)) <= Math.Max(a.X, Math.Max(b.X, c.X)) + eps &&
        Math.Min(a.Y, Math.Min(b.Y, c.Y)) <= Math.Max(d.Y, Math.Max(e.Y, f.Y)) + eps && Math.Min(d.Y, Math.Min(e.Y, f.Y)) <= Math.Max(a.Y, Math.Max(b.Y, c.Y)) + eps &&
        Math.Min(a.Z, Math.Min(b.Z, c.Z)) <= Math.Max(d.Z, Math.Max(e.Z, f.Z)) + eps && Math.Min(d.Z, Math.Min(e.Z, f.Z)) <= Math.Max(a.Z, Math.Max(b.Z, c.Z)) + eps;

    static double C(V3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

    /// The axis to drop when projecting a plane with normal <paramref name="n"/> to 2D.
    static int Drop(V3 n) => Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;

    /// Twice the signed area of p, q, r projected along axis <paramref name="drop"/>.
    static double Orient(V3 p, V3 q, V3 r, int drop)
    {
        int x = (drop + 1) % 3, y = (drop + 2) % 3;
        return (C(q, x) - C(p, x)) * (C(r, y) - C(p, y)) - (C(q, y) - C(p, y)) * (C(r, x) - C(p, x));
    }

    /// Triangles with no common vertex: do they intersect or touch?
    static bool Intersect(V3 a, V3 b, V3 c, V3 d, V3 e, V3 f, double eps)
    {
        var n = V3.Cross(b - a, c - a);
        var m = V3.Cross(e - d, f - d);
        double nl = n.Length, ml = m.Length;
        if (nl == 0 || ml == 0) return false;
        double p = V3.Dot(n, d - a), q = V3.Dot(n, e - a), r = V3.Dot(n, f - a), tol = eps * nl;
        if ((p > tol && q > tol && r > tol) || (p < -tol && q < -tol && r < -tol)) return false;
        double s = V3.Dot(m, a - d), u = V3.Dot(m, b - d), w = V3.Dot(m, c - d), mt = eps * ml;
        if ((s > mt && u > mt && w > mt) || (s < -mt && u < -mt && w < -mt)) return false;
        if (Math.Abs(p) <= tol && Math.Abs(q) <= tol && Math.Abs(r) <= tol && Math.Abs(s) <= mt && Math.Abs(u) <= mt && Math.Abs(w) <= mt)
            return Coplanar(a, b, c, d, e, f, Drop(n), eps);
        return Segment(a, b, d, e, f, eps) || Segment(b, c, d, e, f, eps) || Segment(c, a, d, e, f, eps) ||
               Segment(d, e, a, b, c, eps) || Segment(e, f, a, b, c, eps) || Segment(f, d, a, b, c, eps);
    }

    /// Does segment a-b meet triangle p-q-r (boundaries included, within eps)?
    static bool Segment(V3 a, V3 b, V3 p, V3 q, V3 r, double eps)
    {
        var dir = b - a;
        V3 e1 = q - p, e2 = r - p;
        var hv = V3.Cross(dir, e2);
        double det = V3.Dot(e1, hv);
        if (Math.Abs(det) <= 1e-13 * e1.Length * e2.Length * dir.Length) return false;
        double inv = 1 / det;
        var s = a - p;
        double u = V3.Dot(s, hv) * inv;
        double tol = eps / Math.Max(1e-30, Math.Max(e1.Length, e2.Length));
        if (u < -tol || u > 1 + tol) return false;
        var k = V3.Cross(s, e1);
        double v = V3.Dot(dir, k) * inv;
        if (v < -tol || u + v > 1 + tol) return false;
        double t = V3.Dot(e2, k) * inv;
        double st = eps / Math.Max(1e-30, dir.Length);
        return t >= -st && t <= 1 + st;
    }

    /// Two coplanar triangles with no common vertex: do they overlap or touch?
    static bool Coplanar(V3 a, V3 b, V3 c, V3 d, V3 e, V3 f, int drop, double eps)
    {
        int x = (drop + 1) % 3, y = (drop + 2) % 3;
        bool Inside(V3 pt, V3 q, V3 r, V3 s)
        {
            double u = Orient(q, r, pt, drop), v = Orient(r, s, pt, drop), w = Orient(s, q, pt, drop);
            return (u >= 0 && v >= 0 && w >= 0) || (u <= 0 && v <= 0 && w <= 0);
        }
        bool On(V3 u, V3 v, V3 w) =>
            C(w, x) >= Math.Min(C(u, x), C(v, x)) - eps && C(w, x) <= Math.Max(C(u, x), C(v, x)) + eps &&
            C(w, y) >= Math.Min(C(u, y), C(v, y)) - eps && C(w, y) <= Math.Max(C(u, y), C(v, y)) + eps;
        bool Edge(V3 p, V3 q, V3 r, V3 s)
        {
            double o1 = Orient(p, q, r, drop), o2 = Orient(p, q, s, drop), o3 = Orient(r, s, p, drop), o4 = Orient(r, s, q, drop);
            if (((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0))) return true;
            return (o1 == 0 && On(p, q, r)) || (o2 == 0 && On(p, q, s)) || (o3 == 0 && On(r, s, p)) || (o4 == 0 && On(r, s, q));
        }
        V3[] t1 = [a, b, c], t2 = [d, e, f];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (Edge(t1[i], t1[(i + 1) % 3], t2[j], t2[(j + 1) % 3])) return true;
        return Inside(a, d, e, f) || Inside(d, a, b, c);
    }

    /// Triangles sharing <paramref name="common"/> (1 to 3 vertices): do they overlap beyond it?
    static bool Incident(V3 a, V3 b, V3 c, V3 d, V3 e, V3 f, ReadOnlySpan<V3> common, double eps)
    {
        var n = V3.Cross(b - a, c - a);
        var m = V3.Cross(e - d, f - d);
        if (n.Length == 0 || m.Length == 0) return false;
        if (common.Length == 3) return true;
        var cross = V3.Cross(n, m);
        if (cross.Length / (n.Length * m.Length) > 1e-10)
        {
            // Not coplanar: both meet the line where their planes cross in an interval; sharing only an edge is fine,
            // sharing a vertex is fine when the intervals overlap only at that vertex.
            if (common.Length == 2) return false;
            var dir = cross * (1 / cross.Length);
            var i1 = Slice(a, b, c, d, m * (1 / m.Length), dir, eps);
            var i2 = Slice(d, e, f, a, n * (1 / n.Length), dir, eps);
            return Math.Min(i1.Max, i2.Max) - Math.Max(i1.Min, i2.Min) > eps;
        }
        int drop = Drop(n);
        if (common.Length == 2)
        {
            // Coplanar with a common edge: an overlap if the third vertices are on the same side of it.
            V3 x = common[0], y = common[1];
            var third1 = a != x && a != y ? a : b != x && b != y ? b : c;
            var third2 = d != x && d != y ? d : e != x && e != y ? e : f;
            double p = Orient(x, y, third1, drop), q = Orient(x, y, third2, drop), tol = eps * (x - y).Length;
            return (p > tol && q > tol) || (p < -tol && q < -tol);
        }
        // Coplanar with a common vertex: clip one to the other; positive area is overlap beyond the vertex.
        var poly = new List<V3> { a, b, c };
        V3[] clip = [d, e, f];
        double orientation = Orient(d, e, f, drop) >= 0 ? 1 : -1;
        for (int i = 0; i < 3 && poly.Count > 0; i++)
        {
            var output = new List<V3>();
            V3 p = clip[i], q = clip[(i + 1) % 3];
            for (int j = 0; j < poly.Count; j++)
            {
                V3 x = poly[j], y = poly[(j + 1) % poly.Count];
                double sx = orientation * Orient(p, q, x, drop), sy = orientation * Orient(p, q, y, drop);
                if (sx >= 0) output.Add(x);
                if ((sx < 0 && sy > 0) || (sx > 0 && sy < 0)) output.Add(x + (y - x) * (sx / (sx - sy)));
            }
            poly = output;
        }
        double area = 0;
        for (int i = 1; i + 1 < poly.Count; i++) area += Math.Abs(Orient(poly[0], poly[i], poly[i + 1], drop)) * 0.5;
        double scale = Math.Max((b - a).Length, Math.Max((c - a).Length, (e - d).Length));
        return area > eps * scale;
    }

    /// The interval along <paramref name="dir"/> where triangle a-b-c meets the plane through p with the given normal.
    static (double Min, double Max) Slice(V3 a, V3 b, V3 c, V3 p, V3 normal, V3 dir, double eps)
    {
        Span<V3> pts = [a, b, c];
        Span<double> dist = [V3.Dot(a - p, normal), V3.Dot(b - p, normal), V3.Dot(c - p, normal)];
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < 3; i++)
        {
            double di = dist[i];
            if (Math.Abs(di) <= eps)
            {
                double t = V3.Dot(pts[i], dir);
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }
            int j = (i + 1) % 3;
            double dj = dist[j];
            if ((di > eps && dj < -eps) || (di < -eps && dj > eps))
            {
                var x = pts[i] + (pts[j] - pts[i]) * (di / (di - dj));
                double t = V3.Dot(x, dir);
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }
        }
        return (lo, hi);
    }

    /// Triangles binned by their bounding boxes.
    sealed class Bins
    {
        readonly Dictionary<(int, int, int), List<int>> map = [];
        readonly (int X0, int Y0, int Z0, int X1, int Y1, int Z1)[] box;
        readonly Dictionary<List<int>, (int, int, int)> keyOf = new(ReferenceEqualityComparer.Instance);
        readonly double size;

        public Bins(IReadOnlyList<V3> vs, IReadOnlyList<int> t, double h)
        {
            size = 2 * h;
            double pad = Eps(h);
            int n = t.Count / 3;
            box = new (int, int, int, int, int, int)[n];
            for (int i = 0; i < n; i++)
            {
                V3 a = vs[t[3 * i]], b = vs[t[3 * i + 1]], c = vs[t[3 * i + 2]];
                // Padded by the tolerance, so triangles that only touch across a bin boundary still share a bin.
                var bx = box[i] = (
                    Floor(Math.Min(a.X, Math.Min(b.X, c.X)) - pad), Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - pad), Floor(Math.Min(a.Z, Math.Min(b.Z, c.Z)) - pad),
                    Floor(Math.Max(a.X, Math.Max(b.X, c.X)) + pad), Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y)) + pad), Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) + pad));
                for (int x = bx.X0; x <= bx.X1; x++)
                    for (int y = bx.Y0; y <= bx.Y1; y++)
                        for (int z = bx.Z0; z <= bx.Z1; z++)
                        {
                            if (!map.TryGetValue((x, y, z), out var list))
                            {
                                map[(x, y, z)] = list = [];
                                keyOf[list] = (x, y, z);
                            }
                            list.Add(i);
                        }
            }
        }

        int Floor(double v) => (int)Math.Floor(v / size);

        public IEnumerable<List<int>> Cells => map.Values;

        /// Is <paramref name="cell"/> the bin at the low corner of the overlap of i's and j's bin ranges?
        public bool Owns(int i, int j, List<int> cell)
        {
            var (x, y, z) = keyOf[cell];
            var a = box[i];
            var b = box[j];
            return x == Math.Max(a.X0, b.X0) && y == Math.Max(a.Y0, b.Y0) && z == Math.Max(a.Z0, b.Z0);
        }

        public IEnumerable<List<int>> Near(int i)
        {
            var bx = box[i];
            for (int x = bx.X0; x <= bx.X1; x++)
                for (int y = bx.Y0; y <= bx.Y1; y++)
                    for (int z = bx.Z0; z <= bx.Z1; z++)
                        if (map.TryGetValue((x, y, z), out var list)) yield return list;
        }
    }
}
