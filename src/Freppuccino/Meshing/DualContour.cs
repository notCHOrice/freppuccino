namespace Freppuccino.Meshing;

/// Uniform-grid Dual Contouring (Ju et al. 2002): each surface cell gets a vertex placed by minimising a
/// quadric error built from the surface crossings and normals on the cell's edges, so sharp edges and
/// corners are kept. One quad (two triangles) per sign-changing grid edge.
///
/// A cell crossed by several separate sheets gets one vertex per sheet (as in Manifold Dual Contouring,
/// Schaefer et al. 2007), which keeps the mesh manifold where plain Dual Contouring pinches sheets together.
///
/// First cut, to be refined: no octree yet, features thinner than a cell can be missed, and triangles
/// can still self-intersect near sharp features.
///
/// Known topology defect: when both cells sharing a four-crossing face route all four crossings through
/// one sheet (a tunnel on each side), the four quads around that face share one mesh edge, which is then
/// non-manifold. It shows up where features are about a cell wide (under 1% of random 4-primitive scenes
/// at resolutions 12-40). Re-pairing the face in one cell alone is not a fix: both cells must agree.
/// FrepMesh reports NON-MANIFOLD when it happens; raising the resolution usually clears it.
public static class DualContour
{
    public sealed record Result(List<V3> Vertices, List<int> Triangles, int Nx, int Ny, int Nz, double CellSize);

    const int Block = 8;

    /// How far a vertex may sit outside its cell, in cells (see CellVertex).
    public static double Slack = 0.5;

    /// <paramref name="resolution"/> = cells along the longest side of the field's bounds.
    public static Result Mesh(FrepField field, int resolution)
    {
        var (bmin, bmax) = field.Bounds();
        var size = bmax - bmin;
        double h = Math.Max(size.X, Math.Max(size.Y, size.Z)) / resolution;
        // Two cells of padding: every sample on the grid boundary is strictly outside.
        var origin = bmin - new V3(2 * h, 2 * h, 2 * h);
        int nx = (int)Math.Ceiling(size.X / h) + 4, ny = (int)Math.Ceiling(size.Y / h) + 4, nz = (int)Math.Ceiling(size.Z / h) + 4;
        int sx = nx + 1, sy = ny + 1, sz = nz + 1;

        V3 P(int i, int j, int k) => new(origin.X + i * h, origin.Y + j * h, origin.Z + k * h);

        // 1. Samples. The field is 1-Lipschitz, so a block of samples whose centre is farther from the
        //    surface than the block's radius has no sign change: fill it without evaluating.
        var f = new float[(long)sx * sy * sz];
        int bx = (sx + Block - 1) / Block, by = (sy + Block - 1) / Block, bz = (sz + Block - 1) / Block;
        Parallel.For(0, bz, kb =>
        {
            int k0 = kb * Block, k1 = Math.Min(k0 + Block, sz);
            for (int jb = 0; jb < by; jb++)
            {
                int j0 = jb * Block, j1 = Math.Min(j0 + Block, sy);
                for (int ib = 0; ib < bx; ib++)
                {
                    int i0 = ib * Block, i1 = Math.Min(i0 + Block, sx);
                    double ex = (i1 - 1 - i0) * h * 0.5, ey = (j1 - 1 - j0) * h * 0.5, ez = (k1 - 1 - k0) * h * 0.5;
                    var c = new V3(origin.X + i0 * h + ex, origin.Y + j0 * h + ey, origin.Z + k0 * h + ez);
                    double fc = field.Value(c);
                    bool skip = Math.Abs(fc) > Math.Sqrt(ex * ex + ey * ey + ez * ez) * 1.001 + 1e-9 * h;
                    for (int k = k0; k < k1; k++)
                        for (int j = j0; j < j1; j++)
                        {
                            long row = ((long)k * sy + j) * sx;
                            for (int i = i0; i < i1; i++)
                                f[row + i] = skip ? (float)fc : (float)field.Value(P(i, j, k));
                        }
                }
            }
        });

        bool In(int i, int j, int k) => f[((long)k * sy + j) * sx + i] < 0;

        // 2. One vertex per surface sheet in each cell that has a sign change.
        //    Packed = 2 bits per cell edge: which of the cell's vertices that edge's quad uses.
        var slabs = new (List<(int Cell, int Packed, int Count)> Cells, List<V3> Points)[nz];
        Parallel.For(0, nz, k =>
        {
            var cells = new List<(int, int, int)>();
            var points = new List<V3>();
            Span<bool> s = stackalloc bool[8];
            Span<int> sheet = stackalloc int[12];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int inside = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        s[c] = In(i + (c & 1), j + ((c >> 1) & 1), k + (c >> 2));
                        if (s[c]) inside++;
                    }
                    if (inside == 0 || inside == 8) continue;
                    int count = Sheets(s, sheet), packed = 0;
                    for (int e = 0; e < 12; e++)
                        if (sheet[e] >= 0) packed |= sheet[e] << (2 * e);
                    for (int c = 0; c < count; c++) points.Add(CellVertex(field, P(i, j, k), h, s, sheet, c));
                    cells.Add(((k * ny + j) * nx + i, packed, count));
                }
            slabs[k] = (cells, points);
        });

        var vertices = new List<V3>();
        var index = new Dictionary<int, (int Base, int Packed)>();
        foreach (var (cells, points) in slabs)
        {
            int at = vertices.Count;
            foreach (var (cell, packed, count) in cells)
            {
                index[cell] = (at, packed);
                at += count;
            }
            vertices.AddRange(points);
        }

        // 3. One quad per sign-changing grid edge, joining the four cells around it, wound so the
        //    normal points from inside to outside. Padding guarantees those four cells exist.
        var tris = new List<int>();
        // Vertex of cell (i, j, k) on the sheet crossing its edge number `edge` (see Edges).
        int V(int i, int j, int k, int edge)
        {
            var (at, packed) = index[(k * ny + j) * nx + i];
            return at + ((packed >> (2 * edge)) & 3);
        }
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (flip) (b, d) = (d, b);
            // Split along the diagonal that lies closer to the surface, so a crease running through
            // the quad becomes a triangle edge rather than being cut across.
            V3 pa = vertices[a], pb = vertices[b], pc = vertices[c], pd = vertices[d];
            double ac = Math.Abs(field.Value((pa + pc) * 0.5)), bd = Math.Abs(field.Value((pb + pd) * 0.5));
            if (ac <= bd) { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(d); }
            else { tris.Add(a); tris.Add(b); tris.Add(d); tris.Add(b); tris.Add(c); tris.Add(d); }
        }
        for (int k = 0; k < sz; k++)
            for (int j = 0; j < sy; j++)
                for (int i = 0; i < sx; i++)
                {
                    bool a = In(i, j, k);
                    if (i < nx && a != In(i + 1, j, k))
                        Quad(V(i, j - 1, k - 1, 3), V(i, j, k - 1, 2), V(i, j, k, 0), V(i, j - 1, k, 1), !a);
                    if (j < ny && a != In(i, j + 1, k))
                        Quad(V(i - 1, j, k - 1, 7), V(i - 1, j, k, 5), V(i, j, k, 4), V(i, j, k - 1, 6), !a);
                    if (k < nz && a != In(i, j, k + 1))
                        Quad(V(i - 1, j - 1, k, 11), V(i, j - 1, k, 10), V(i, j, k, 8), V(i - 1, j, k, 9), !a);
                }

        return new Result(vertices, tris, nx, ny, nz, h);
    }

    // Corner c = (c & 1, (c >> 1) & 1, c >> 2); the 12 cell edges as corner pairs:
    // x-edges 0-3 (index = y + 2z), y-edges 4-7 (4 + x + 2z), z-edges 8-11 (8 + x + 2y).
    static readonly (int A, int B)[] Edges =
    [
        (0, 1), (2, 3), (4, 5), (6, 7),
        (0, 2), (1, 3), (4, 6), (5, 7),
        (0, 4), (1, 5), (2, 6), (3, 7),
    ];

    // The 6 cell faces: corners in cyclic order, and the edge from each corner to the next.
    static readonly int[][] FaceCorners = [[0, 2, 6, 4], [1, 3, 7, 5], [0, 1, 5, 4], [2, 3, 7, 6], [0, 1, 3, 2], [4, 5, 7, 6]];
    static readonly int[][] FaceEdges = [.. FaceCorners.Select(f => Enumerable.Range(0, 4).Select(i =>
        Array.FindIndex(Edges, e => (e.A, e.B) == (f[i], f[(i + 1) % 4]) || (e.B, e.A) == (f[i], f[(i + 1) % 4]))).ToArray())];

    /// Groups the cell's sign-changing edges into surface sheets: two crossings on the same face belong
    /// to the same sheet. On a face with four crossings the two edges at each inside corner are paired;
    /// the rule depends only on the face's corner signs, so the neighbouring cell agrees.
    /// Writes a sheet number (or -1) per edge and returns the sheet count (at most 4).
    static int Sheets(ReadOnlySpan<bool> inside, Span<int> sheet)
    {
        Span<int> parent = stackalloc int[12];
        for (int e = 0; e < 12; e++) parent[e] = e;

        Span<int> crossing = stackalloc int[4];
        for (int f = 0; f < 6; f++)
        {
            int[] c = FaceCorners[f], fe = FaceEdges[f];
            int n = 0;
            for (int i = 0; i < 4; i++)
                if (inside[c[i]] != inside[c[(i + 1) % 4]]) crossing[n++] = fe[i];
            if (n == 2) parent[Find(parent, crossing[0])] = Find(parent, crossing[1]);
            else if (n == 4)
                for (int i = 0; i < 4; i++)
                    if (inside[c[i]]) parent[Find(parent, fe[i])] = Find(parent, fe[(i + 3) % 4]);
        }

        int count = 0;
        Span<int> number = stackalloc int[12];
        number.Fill(-1);
        for (int e = 0; e < 12; e++)
        {
            if (inside[Edges[e].A] == inside[Edges[e].B]) { sheet[e] = -1; continue; }
            int root = Find(parent, e);
            if (number[root] < 0) number[root] = count++;
            sheet[e] = number[root];
        }
        return count;
    }

    static int Find(Span<int> parent, int e)
    {
        while (parent[e] != e) e = parent[e] = parent[parent[e]];
        return e;
    }

    /// The vertex for sheet number <paramref name="which"/> of the cell.
    static V3 CellVertex(FrepField field, V3 cellMin, double h, ReadOnlySpan<bool> inside, ReadOnlySpan<int> sheet, int which)
    {
        Span<V3> pts = stackalloc V3[12];
        Span<V3> nrm = stackalloc V3[12];
        int n = 0;
        var mass = new V3(0, 0, 0);
        for (int edge = 0; edge < 12; edge++)
        {
            if (sheet[edge] != which) continue;
            var (a, b) = Edges[edge];
            var pa = Corner(cellMin, h, a);
            var pb = Corner(cellMin, h, b);
            var (pin, pout) = inside[a] ? (pa, pb) : (pb, pa);
            // Bisection: robust at creases, where the field is not smooth.
            for (int it = 0; it < 12; it++)
            {
                var mid = (pin + pout) * 0.5;
                if (field.Value(mid) < 0) pin = mid; else pout = mid;
            }
            var p = (pin + pout) * 0.5;
            field.Value(p, out var g);
            pts[n] = p;
            nrm[n] = g.Normalized();
            mass += p;
            n++;
        }
        mass *= 1.0 / n;

        // Minimise Σ (nᵢ·(x − pᵢ))² about the mass point. Small eigenvalues are dropped, so on a flat
        // or gently curved patch the vertex stays near the mass point instead of flying off.
        Span<double> m = stackalloc double[6]; // xx, xy, xz, yy, yz, zz
        var rhs = new V3(0, 0, 0);
        for (int e = 0; e < n; e++)
        {
            var q = nrm[e];
            m[0] += q.X * q.X; m[1] += q.X * q.Y; m[2] += q.X * q.Z;
            m[3] += q.Y * q.Y; m[4] += q.Y * q.Z; m[5] += q.Z * q.Z;
            rhs += q * V3.Dot(q, pts[e] - mass);
        }
        Span<double> val = stackalloc double[3];
        Span<V3> vec = stackalloc V3[3];
        SymmetricEigen(m, val, vec);
        double max = Math.Max(val[0], Math.Max(val[1], val[2]));
        var x = mass;
        for (int e = 0; e < 3; e++)
            if (val[e] > 0.1 * max)
                x += vec[e] * (V3.Dot(vec[e], rhs) / val[e]);

        // A crease can clip a cell without passing through it (its crossings then lie on both sides of a
        // crease that is just outside). Clamping the vertex to the cell would pull it off the crease and
        // serrate every edge that is not grid-aligned, so let it leave by up to half a cell. The limit
        // still bounds how far a bad solve can move it.
        double slack = Slack * h;
        return new V3(
            Math.Clamp(x.X, cellMin.X - slack, cellMin.X + h + slack),
            Math.Clamp(x.Y, cellMin.Y - slack, cellMin.Y + h + slack),
            Math.Clamp(x.Z, cellMin.Z - slack, cellMin.Z + h + slack));
    }

    static V3 Corner(V3 min, double h, int c) => new(min.X + (c & 1) * h, min.Y + ((c >> 1) & 1) * h, min.Z + (c >> 2) * h);

    /// Cyclic Jacobi for a symmetric 3×3 (xx, xy, xz, yy, yz, zz).
    static void SymmetricEigen(ReadOnlySpan<double> m, Span<double> values, Span<V3> vectors)
    {
        Span<double> a = stackalloc double[9] { m[0], m[1], m[2], m[1], m[3], m[4], m[2], m[4], m[5] };
        Span<double> v = stackalloc double[9] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        for (int sweep = 0; sweep < 12; sweep++)
        {
            double off = Math.Abs(a[1]) + Math.Abs(a[2]) + Math.Abs(a[5]);
            if (off < 1e-15) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    double apq = a[p * 3 + q];
                    if (Math.Abs(apq) < 1e-300) continue;
                    double theta = (a[q * 3 + q] - a[p * 3 + p]) / (2 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int r = 0; r < 3; r++) // A <- A·J
                    {
                        double arp = a[r * 3 + p], arq = a[r * 3 + q];
                        a[r * 3 + p] = c * arp - s * arq;
                        a[r * 3 + q] = s * arp + c * arq;
                    }
                    for (int r = 0; r < 3; r++) // A <- Jᵀ·A
                    {
                        double apr = a[p * 3 + r], aqr = a[q * 3 + r];
                        a[p * 3 + r] = c * apr - s * aqr;
                        a[q * 3 + r] = s * apr + c * aqr;
                    }
                    for (int r = 0; r < 3; r++) // V <- V·J
                    {
                        double vrp = v[r * 3 + p], vrq = v[r * 3 + q];
                        v[r * 3 + p] = c * vrp - s * vrq;
                        v[r * 3 + q] = s * vrp + c * vrq;
                    }
                }
        }
        for (int e = 0; e < 3; e++)
        {
            values[e] = a[e * 3 + e];
            vectors[e] = new V3(v[e], v[3 + e], v[6 + e]);
        }
    }
}
