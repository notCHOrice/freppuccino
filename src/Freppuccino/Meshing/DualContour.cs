namespace Freppuccino.Meshing;

/// Dual Contouring (Ju et al. 2002): each surface cell gets a vertex placed by minimising a quadric
/// error built from the surface crossings and normals on the cell's edges, so sharp edges and corners
/// are kept. One polygon per sign-changing cell edge joins the cells around it.
///
/// The field is sampled on a uniform grid. With a simplification tolerance the cells are then merged
/// bottom-up into an octree wherever one vertex still fits the merged surface within the tolerance and
/// the merge keeps the topology, so flat and gently curved regions get large cells while creases and
/// tight curvature keep the full resolution (adaptive Dual Contouring, same paper).
///
/// A finest-level cell crossed by several separate sheets gets one vertex per sheet (as in Manifold
/// Dual Contouring, Schaefer et al. 2007), which keeps the mesh manifold where plain Dual Contouring
/// pinches sheets together.
///
/// To be refined: nothing is sampled finer than the grid, so features thinner than a cell can be missed
/// and seams where primitives intersect are only as good as the resolution; triangles can
/// self-intersect near sharp features.
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

    /// How far a vertex may sit outside its cell, in finest cells (see CellVertex).
    public static double Slack = 1.0;

    /// Eigenvalues of the fit below this fraction of the largest are treated as zero (see Qef.Solve).
    public static double Cutoff = 0.1;

    /// Counts topology defects in a result: edges not shared by exactly two triangles, and vertices whose
    /// surrounding triangles do not form a single closed fan (a pinched vertex passes the edge count).
    public static (int Edges, int Vertices) Defects(Result mesh)
    {
        var t = mesh.Triangles;
        var edges = new Dictionary<(int, int), int>();
        // For each vertex, the two neighbours of every triangle around it: next[v][a] = b.
        var fans = new Dictionary<int, int>[mesh.Vertices.Count];
        int badVertices = 0;
        var broken = new bool[mesh.Vertices.Count];
        for (int i = 0; i < t.Count; i += 3)
            for (int c = 0; c < 3; c++)
            {
                int v = t[i + c], a = t[i + (c + 1) % 3], b = t[i + (c + 2) % 3];
                var key = v < a ? (v, a) : (a, v);
                edges[key] = edges.GetValueOrDefault(key) + 1;
                // A consistently wound closed fan gives each neighbour exactly one successor.
                if (!(fans[v] ??= []).TryAdd(a, b)) broken[v] = true;
            }
        for (int v = 0; v < fans.Length; v++)
        {
            var fan = fans[v];
            if (fan == null) continue;
            bool ok = !broken[v];
            if (ok)
            {
                int start = fan.Keys.First(), at = start, steps = 0;
                do { ok = fan.TryGetValue(at, out at); steps++; } while (ok && at != start && steps <= fan.Count);
                ok = ok && at == start && steps == fan.Count;
            }
            if (!ok) badVertices++;
        }
        return (edges.Values.Count(n => n != 2), badVertices);
    }

    /// Sampled field on the uniform grid. Coordinates handed to <see cref="Qef"/> are relative to Origin.
    sealed class Grid(FrepField field, V3 origin, double h, int nx, int ny, int nz)
    {
        public readonly FrepField Field = field;
        public readonly V3 Origin = origin;
        public readonly double H = h;
        public readonly int Nx = nx, Ny = ny, Nz = nz, Sx = nx + 1, Sy = ny + 1, Sz = nz + 1;
        public readonly float[] F = new float[(long)(nx + 1) * (ny + 1) * (nz + 1)];

        public V3 P(int i, int j, int k) => new(Origin.X + i * H, Origin.Y + j * H, Origin.Z + k * H);

        /// Inside test at a sample; anything off the grid is outside.
        public bool In(int i, int j, int k) =>
            (uint)i < (uint)Sx && (uint)j < (uint)Sy && (uint)k < (uint)Sz && F[((long)k * Sy + j) * Sx + i] < 0;

        public int Cell(int i, int j, int k) => (k * Ny + j) * Nx + i;
        public bool HasCell(int i, int j, int k) => (uint)i < (uint)Nx && (uint)j < (uint)Ny && (uint)k < (uint)Nz;
    }

    /// Quadric error Σ (nᵢ·(x − pᵢ))² in accumulable form, plus the mass point of the pᵢ.
    struct Qef
    {
        double xx, xy, xz, yy, yz, zz, bx, by, bz, c;
        V3 mass;
        public int N;

        public void Add(V3 n, V3 p)
        {
            double d = V3.Dot(n, p);
            xx += n.X * n.X; xy += n.X * n.Y; xz += n.X * n.Z; yy += n.Y * n.Y; yz += n.Y * n.Z; zz += n.Z * n.Z;
            bx += n.X * d; by += n.Y * d; bz += n.Z * d; c += d * d;
            mass += p; N++;
        }

        public void Add(in Qef o)
        {
            xx += o.xx; xy += o.xy; xz += o.xz; yy += o.yy; yz += o.yz; zz += o.zz;
            bx += o.bx; by += o.by; bz += o.bz; c += o.c;
            mass += o.mass; N += o.N;
        }

        readonly V3 Times(V3 v) => new(xx * v.X + xy * v.Y + xz * v.Z, xy * v.X + yy * v.Y + yz * v.Z, xz * v.X + yz * v.Y + zz * v.Z);

        /// Minimiser, solved about the mass point. Small eigenvalues are dropped, so on a flat or gently
        /// curved patch the vertex stays near the mass point instead of flying off.
        public readonly V3 Solve()
        {
            var m = mass * (1.0 / N);
            var rhs = new V3(bx, by, bz) - Times(m);
            Span<double> a = stackalloc double[6] { xx, xy, xz, yy, yz, zz };
            Span<double> val = stackalloc double[3];
            Span<V3> vec = stackalloc V3[3];
            SymmetricEigen(a, val, vec);
            double max = Math.Max(val[0], Math.Max(val[1], val[2]));
            var x = m;
            for (int e = 0; e < 3; e++)
                if (val[e] > Cutoff * max)
                    x += vec[e] * (V3.Dot(vec[e], rhs) / val[e]);
            return x;
        }

        /// Root-mean-square distance from <paramref name="x"/> to the planes.
        public readonly double Rms(V3 x) =>
            Math.Sqrt(Math.Max(V3.Dot(x, Times(x)) - 2 * V3.Dot(x, new V3(bx, by, bz)) + c, 0) / N);
    }

    /// <paramref name="resolution"/> = finest cells along the longest side of the field's bounds.
    /// <paramref name="simplify"/> = how far, in finest cells (RMS), a merged cell's vertex may be from the
    /// surface planes it replaces; 0 keeps the uniform grid.
    public static Result Mesh(FrepField field, int resolution, double simplify = 0)
    {
        var (bmin, bmax) = field.Bounds();
        var size = bmax - bmin;
        double h = Math.Max(size.X, Math.Max(size.Y, size.Z)) / resolution;
        // Two cells of padding: every sample on the grid boundary is strictly outside.
        var g = new Grid(field, bmin - new V3(2 * h, 2 * h, 2 * h), h,
            (int)Math.Ceiling(size.X / h) + 4, (int)Math.Ceiling(size.Y / h) + 4, (int)Math.Ceiling(size.Z / h) + 4);
        int nx = g.Nx, ny = g.Ny, nz = g.Nz, sx = g.Sx, sy = g.Sy, sz = g.Sz;
        var f = g.F;

        // 1. Samples. The field is 1-Lipschitz, so a block of samples whose centre is farther from the
        //    surface than the block's radius has no sign change: fill it without evaluating.
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
                    var c = new V3(g.Origin.X + i0 * h + ex, g.Origin.Y + j0 * h + ey, g.Origin.Z + k0 * h + ez);
                    double fc = field.Value(c);
                    bool skip = Math.Abs(fc) > Math.Sqrt(ex * ex + ey * ey + ez * ez) * 1.001 + 1e-9 * h;
                    for (int k = k0; k < k1; k++)
                        for (int j = j0; j < j1; j++)
                        {
                            long row = ((long)k * sy + j) * sx;
                            for (int i = i0; i < i1; i++)
                                f[row + i] = skip ? (float)fc : (float)field.Value(g.P(i, j, k));
                        }
                }
            }
        });

        // 2. One vertex per surface sheet in each cell that has a sign change.
        //    Packed = 2 bits per cell edge: which of the cell's vertices that edge's polygon uses.
        var slabs = new (List<(int Cell, int Packed, int Count)> Cells, List<V3> Points, List<Qef> Qefs)[nz];
        Parallel.For(0, nz, k =>
        {
            var cells = new List<(int, int, int)>();
            var points = new List<V3>();
            var qefs = new List<Qef>();
            Span<bool> s = stackalloc bool[8];
            Span<int> sheet = stackalloc int[12];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int inside = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        s[c] = g.In(i + (c & 1), j + ((c >> 1) & 1), k + (c >> 2));
                        if (s[c]) inside++;
                    }
                    if (inside == 0 || inside == 8) continue;
                    int count = Sheets(s, sheet), packed = 0;
                    for (int e = 0; e < 12; e++)
                        if (sheet[e] >= 0) packed |= sheet[e] << (2 * e);
                    for (int c = 0; c < count; c++)
                    {
                        points.Add(CellVertex(g, i, j, k, s, sheet, c, out var q));
                        qefs.Add(q);
                    }
                    cells.Add((g.Cell(i, j, k), packed, count));
                }
            slabs[k] = (cells, points, qefs);
        });

        var vertices = new List<V3>();
        var quadrics = new List<Qef>();
        var index = new Dictionary<int, (int Base, int Packed, int Count)>();
        foreach (var (cells, points, qefs) in slabs)
        {
            int at = vertices.Count;
            foreach (var (cell, packed, count) in cells)
            {
                index[cell] = (at, packed, count);
                at += count;
            }
            vertices.AddRange(points);
            quadrics.AddRange(qefs);
        }

        // 3. Polygons.
        var tris = new List<int>();
        var octree = new Octree(g, index, vertices, quadrics, tris);
        octree.Build(simplify * h);
        octree.Contour();

        // Merging leaves the vertices of merged cells unused.
        var remap = new int[vertices.Count];
        Array.Fill(remap, -1);
        var used = new List<V3>();
        for (int t = 0; t < tris.Count; t++)
        {
            int v = tris[t];
            if (remap[v] < 0) { remap[v] = used.Count; used.Add(vertices[v]); }
            tris[t] = remap[v];
        }
        return new Result(used, tris, nx, ny, nz, h);
    }

    /// Octree over the grid's cells. A node at level L covers 2^L cells per side; level 0 nodes are the
    /// grid cells themselves. Only merged cells are stored: everything else is derived from the grid.
    sealed class Octree(Grid g, Dictionary<int, (int Base, int Packed, int Count)> cells, List<V3> vertices, List<Qef> quadrics, List<int> tris)
    {
        readonly record struct Node(int L, int I, int J, int K)
        {
            public long Key => ((long)L << 60) | ((long)I << 40) | ((long)J << 20) | (uint)K;
            public Node Child(int bx, int by, int bz) => new(L - 1, 2 * I + bx, 2 * J + by, 2 * K + bz);
            public Node Parent => new(L + 1, I >> 1, J >> 1, K >> 1);
        }

        enum Kind { Empty, Leaf, Internal }

        readonly int levels = (int)Math.Ceiling(Math.Log2(Math.Max(g.Nx, Math.Max(g.Ny, g.Nz))));
        readonly Dictionary<long, int> merged = [];   // merged cell (level >= 1) -> its vertex
        readonly HashSet<long> occupied = [];         // nodes (level >= 1) containing surface cells

        Kind KindOf(Node n)
        {
            if (n.L == 0) return g.HasCell(n.I, n.J, n.K) && cells.ContainsKey(g.Cell(n.I, n.J, n.K)) ? Kind.Leaf : Kind.Empty;
            if (merged.ContainsKey(n.Key)) return Kind.Leaf;
            return occupied.Contains(n.Key) ? Kind.Internal : Kind.Empty;
        }

        /// Records which nodes hold surface, and merges cells bottom-up while one vertex fits within
        /// <paramref name="tolerance"/> (model units; 0 = no merging).
        public void Build(double tolerance)
        {
            var level = new HashSet<Node>();
            foreach (var cell in cells.Keys)
                level.Add(new Node(1, cell % g.Nx >> 1, cell / g.Nx % g.Ny >> 1, cell / (g.Nx * g.Ny) >> 1));
            Span<bool> corner = stackalloc bool[8];
            Span<int> sheet = stackalloc int[12];
            for (int l = 1; l <= levels; l++)
            {
                var parents = new HashSet<Node>();
                foreach (var n in level)
                {
                    occupied.Add(n.Key);
                    if (l < levels) parents.Add(n.Parent);
                    if (tolerance <= 0 || !TryMerge(n, tolerance, corner, sheet, out var x, out var q)) continue;
                    merged[n.Key] = vertices.Count;
                    vertices.Add(x);
                    quadrics.Add(q);
                }
                level = parents;
            }
        }

        bool TryMerge(Node n, double tolerance, Span<bool> corner, Span<int> sheet, out V3 x, out Qef q)
        {
            x = default;
            q = default;
            // Every child must be empty or a single-vertex leaf.
            for (int c = 0; c < 8; c++)
            {
                var child = n.Child(c & 1, (c >> 1) & 1, c >> 2);
                if (child.L == 0)
                {
                    if (!g.HasCell(child.I, child.J, child.K) || !cells.TryGetValue(g.Cell(child.I, child.J, child.K), out var e)) continue;
                    if (e.Count != 1) return false;
                    q.Add(quadrics[e.Base]);
                }
                else if (merged.TryGetValue(child.Key, out int v)) q.Add(quadrics[v]);
                else if (occupied.Contains(child.Key)) return false;
            }

            // Topology safety (Ju et al. 2002, section 4.1): the merged cell must hold a single sheet, and
            // the samples at the middle of each of its edges, faces and its centre must agree with one of
            // the corners of that edge, face or cell, so no sign feature is lost between the corners.
            int size = 1 << n.L, half = size >> 1, i0 = n.I * size, j0 = n.J * size, k0 = n.K * size;
            for (int c = 0; c < 8; c++) corner[c] = g.In(i0 + (c & 1) * size, j0 + ((c >> 1) & 1) * size, k0 + (c >> 2) * size);
            if (Sheets(corner, sheet) != 1) return false;
            for (int m = 0; m < 27; m++)
            {
                int mx = m % 3, my = m / 3 % 3, mz = m / 9;
                if (mx != 1 && my != 1 && mz != 1) continue; // a corner
                bool sign = g.In(i0 + mx * half, j0 + my * half, k0 + mz * half), agrees = false;
                for (int c = 0; c < 8 && !agrees; c++)
                {
                    int cx = c & 1, cy = (c >> 1) & 1, cz = c >> 2;
                    if ((mx == 1 || mx == 2 * cx) && (my == 1 || my == 2 * cy) && (mz == 1 || mz == 2 * cz)) agrees = corner[c] == sign;
                }
                if (!agrees) return false;
            }

            // The merged cell's one vertex joins every polygon whose edge lies on the cell's boundary, so
            // the surface must cross that boundary in a single closed loop. The sign test above does not
            // guarantee it (a crease can make the surface leave and re-enter through one face), and two
            // loops would pinch two fans of triangles together at the vertex.
            if (BoundaryLoops(i0, j0, k0, size) != 1) return false;

            var lo = new V3(i0 * g.H, j0 * g.H, k0 * g.H);
            x = Clamp(q.Solve(), lo, size * g.H, g.H);
            if (q.Rms(x) > tolerance) return false;
            x += g.Origin;
            return true;
        }

        /// Number of closed loops in which the surface crosses the boundary of the cube of
        /// <paramref name="size"/> cells at sample (i0, j0, k0), traced over the finest grid squares.
        int BoundaryLoops(int i0, int j0, int k0, int size)
        {
            var parent = new Dictionary<long, long>();
            long Find(long e)
            {
                if (!parent.TryGetValue(e, out long up)) { parent[e] = e; return e; }
                if (up == e) return e;
                return parent[e] = Find(up);
            }
            Span<int> at = stackalloc int[3];
            Span<long> edge = stackalloc long[4];
            Span<bool> sign = stackalloc bool[4];
            for (int axis = 0; axis < 3; axis++)
            {
                int p = P(axis), q = Q(axis);
                for (int side = 0; side <= size; side += size)
                    for (int u = 0; u < size; u++)
                        for (int v = 0; v < size; v++)
                        {
                            // Corners in cyclic order (u,v), (u+1,v), (u+1,v+1), (u,v+1); edge n joins corner n to n+1.
                            for (int n = 0; n < 4; n++)
                            {
                                at[0] = i0; at[1] = j0; at[2] = k0;
                                at[axis] += side; at[p] += u + (n is 1 or 2 ? 1 : 0); at[q] += v + (n >= 2 ? 1 : 0);
                                sign[n] = g.In(at[0], at[1], at[2]);
                                // An edge is named by its lower sample and its direction.
                                if (n == 2) at[p]--;
                                if (n == 3) at[q]--;
                                edge[n] = (((long)at[2] * g.Sy + at[1]) * g.Sx + at[0]) * 3 + (n is 0 or 2 ? p : q);
                            }
                            int crossings = 0;
                            for (int n = 0; n < 4; n++) if (sign[n] != sign[(n + 1) % 4]) crossings++;
                            if (crossings == 2)
                            {
                                long first = -1;
                                for (int n = 0; n < 4; n++)
                                {
                                    if (sign[n] == sign[(n + 1) % 4]) continue;
                                    if (first < 0) first = Find(edge[n]); else parent[Find(edge[n])] = first;
                                }
                            }
                            else if (crossings == 4)
                                for (int n = 0; n < 4; n++)
                                    if (sign[n]) parent[Find(edge[n])] = Find(edge[(n + 3) % 4]);
                        }
            }
            int loops = 0;
            foreach (var (e, up) in parent) if (e == up) loops++;
            return loops;
        }

        public void Contour() => CellProc(new Node(levels, 0, 0, 0));

        // Axis d = 0, 1, 2; the other two in cyclic order, so that d = p × q.
        static int P(int d) => (d + 1) % 3;
        static int Q(int d) => (d + 2) % 3;

        static Node ChildAt(Node n, int a0, int b0, int a1, int b1, int a2, int b2)
        {
            Span<int> bit = stackalloc int[3];
            bit[a0] = b0; bit[a1] = b1; bit[a2] = b2;
            return n.Child(bit[0], bit[1], bit[2]);
        }

        void CellProc(Node n)
        {
            if (KindOf(n) != Kind.Internal) return;
            for (int c = 0; c < 8; c++) CellProc(n.Child(c & 1, (c >> 1) & 1, c >> 2));
            int half = 1 << (n.L - 1), i0 = n.I << n.L, j0 = n.J << n.L, k0 = n.K << n.L;
            Span<Node> around = stackalloc Node[4];
            Span<int> start = stackalloc int[3];
            for (int d = 0; d < 3; d++)
            {
                int p = P(d), q = Q(d);
                // The four faces between children across axis d.
                for (int u = 0; u < 2; u++)
                    for (int v = 0; v < 2; v++)
                        FaceProc(d, ChildAt(n, d, 0, p, u, q, v), ChildAt(n, d, 1, p, u, q, v));
                // The two edges along d through the centre.
                for (int s = 0; s < 2; s++)
                {
                    around[0] = ChildAt(n, d, s, p, 0, q, 0);
                    around[1] = ChildAt(n, d, s, p, 1, q, 0);
                    around[2] = ChildAt(n, d, s, p, 1, q, 1);
                    around[3] = ChildAt(n, d, s, p, 0, q, 1);
                    start[0] = i0; start[1] = j0; start[2] = k0;
                    start[p] += half; start[q] += half; start[d] += s * half;
                    EdgeProc(d, around, start[0], start[1], start[2], half);
                }
            }
        }

        /// <paramref name="a"/> and <paramref name="b"/> share a face across <paramref name="axis"/>, a on the low side.
        void FaceProc(int axis, Node a, Node b)
        {
            Kind ka = KindOf(a), kb = KindOf(b);
            if (ka == Kind.Empty || kb == Kind.Empty || (ka == Kind.Leaf && kb == Kind.Leaf)) return;
            int p = P(axis), q = Q(axis);
            for (int u = 0; u < 2; u++)
                for (int v = 0; v < 2; v++)
                    FaceProc(axis,
                        ka == Kind.Internal ? ChildAt(a, axis, 1, p, u, q, v) : a,
                        kb == Kind.Internal ? ChildAt(b, axis, 0, p, u, q, v) : b);

            // The shared face's extent is that of the smaller node.
            var small = a.L <= b.L ? a : b;
            int half = 1 << (small.L - 1);
            Span<int> origin = stackalloc int[3] { small.I << small.L, small.J << small.L, small.K << small.L };
            if (small == a) origin[axis] += 2 * half;
            Span<Node> around = stackalloc Node[4];
            Span<int> start = stackalloc int[3];
            // The four edges in the face through its centre: two along each in-face axis d, the other being w.
            for (int pass = 0; pass < 2; pass++)
            {
                int d = pass == 0 ? p : q, w = pass == 0 ? q : p;
                for (int s = 0; s < 2; s++)
                {
                    // Quadrants around the edge, counter-clockwise about +d, in terms of (P(d), Q(d)).
                    for (int quadrant = 0; quadrant < 4; quadrant++)
                    {
                        int cp = quadrant is 1 or 2 ? 1 : 0, cq = quadrant >= 2 ? 1 : 0;
                        int onAxis = P(d) == axis ? cp : cq, onW = P(d) == axis ? cq : cp;
                        var side = onAxis == 0 ? a : b;
                        around[quadrant] = KindOf(side) == Kind.Internal ? ChildAt(side, axis, 1 - onAxis, w, onW, d, s) : side;
                    }
                    origin.CopyTo(start);
                    start[w] += half; start[d] += s * half;
                    EdgeProc(d, around, start[0], start[1], start[2], half);
                }
            }
        }

        // Which of a finest cell's 12 edges is the shared one, per quadrant around an edge along d.
        static readonly int[][] EdgeInQuadrant = [[3, 2, 0, 1], [7, 5, 4, 6], [11, 10, 8, 9]];

        /// The four nodes around an edge segment along <paramref name="d"/>, counter-clockwise about +d.
        /// The segment starts at sample (i, j, k) and is <paramref name="length"/> cells long.
        void EdgeProc(int d, ReadOnlySpan<Node> around, int i, int j, int k, int length)
        {
            bool anyInternal = false;
            for (int n = 0; n < 4; n++)
            {
                var kind = KindOf(around[n]);
                if (kind == Kind.Empty) return;
                anyInternal |= kind == Kind.Internal;
            }

            if (anyInternal)
            {
                int p = P(d), q = Q(d), half = length >> 1;
                Span<Node> next = stackalloc Node[4];
                Span<int> start = stackalloc int[3];
                for (int s = 0; s < 2; s++)
                {
                    for (int quadrant = 0; quadrant < 4; quadrant++)
                    {
                        // The child touching the edge is the one towards it: opposite to the quadrant.
                        int cp = quadrant is 1 or 2 ? 0 : 1, cq = quadrant >= 2 ? 0 : 1;
                        var n = around[quadrant];
                        next[quadrant] = KindOf(n) == Kind.Internal ? ChildAt(n, d, s, p, cp, q, cq) : n;
                    }
                    start[0] = i; start[1] = j; start[2] = k;
                    start[d] += s * half;
                    EdgeProc(d, next, start[0], start[1], start[2], half);
                }
                return;
            }

            // All leaves: a minimal edge. One polygon if the surface crosses it.
            Span<int> end = stackalloc int[3] { i, j, k };
            end[d] += length;
            bool inside = g.In(i, j, k);
            if (inside == g.In(end[0], end[1], end[2])) return;
            Span<int> poly = stackalloc int[4];
            int count = 0;
            for (int quadrant = 0; quadrant < 4; quadrant++)
            {
                var n = around[quadrant];
                int v;
                if (n.L == 0)
                {
                    var (at, packed, _) = cells[g.Cell(n.I, n.J, n.K)];
                    v = at + ((packed >> (2 * EdgeInQuadrant[d][quadrant])) & 3);
                }
                else v = merged[n.Key];
                if (count > 0 && (poly[count - 1] == v || poly[0] == v)) continue; // a larger cell spans two quadrants
                poly[count++] = v;
            }
            if (count < 3) return;
            // Wind so the normal points from inside to outside.
            if (!inside) poly[..count].Reverse();
            if (count == 3) { tris.Add(poly[0]); tris.Add(poly[1]); tris.Add(poly[2]); return; }
            // Split along the diagonal that lies closer to the surface, so a crease running through
            // the quad becomes a triangle edge rather than being cut across.
            int a = poly[0], b = poly[1], c = poly[2], e = poly[3];
            double ac = Math.Abs(g.Field.Value((vertices[a] + vertices[c]) * 0.5)), be = Math.Abs(g.Field.Value((vertices[b] + vertices[e]) * 0.5));
            if (ac <= be) { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(e); }
            else { tris.Add(a); tris.Add(b); tris.Add(e); tris.Add(b); tris.Add(c); tris.Add(e); }
        }
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

    /// The vertex for sheet number <paramref name="which"/> of cell (i, j, k), and the quadric it minimises.
    static V3 CellVertex(Grid g, int i, int j, int k, ReadOnlySpan<bool> inside, ReadOnlySpan<int> sheet, int which, out Qef q)
    {
        var cellMin = g.P(i, j, k);
        double h = g.H;
        q = default;
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
                if (g.Field.Value(mid) < 0) pin = mid; else pout = mid;
            }
            var p = (pin + pout) * 0.5;
            g.Field.Value(p, out var grad);
            q.Add(grad.Normalized(), p - g.Origin);
        }
        return Clamp(q.Solve(), cellMin - g.Origin, h, h) + g.Origin;
    }

    /// A crease can clip a cell without passing through it (its crossings then lie on both sides of a
    /// crease that is just outside). Clamping the vertex to the cell would pull it off the crease and
    /// serrate every edge that is not grid-aligned, so let it leave by up to <see cref="Slack"/> finest
    /// cells. The limit still bounds how far a bad solve can move it.
    static V3 Clamp(V3 x, V3 min, double size, double h)
    {
        double slack = Slack * h;
        return new V3(
            Math.Clamp(x.X, min.X - slack, min.X + size + slack),
            Math.Clamp(x.Y, min.Y - slack, min.Y + size + slack),
            Math.Clamp(x.Z, min.Z - slack, min.Z + size + slack));
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
