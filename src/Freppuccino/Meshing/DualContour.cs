using System.Collections.Concurrent;

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
/// Fold-overs: letting a vertex leave its cell keeps creases sharp but can fold a triangle over its
/// neighbour, which shows up as a self-intersection. Quads are split along the diagonal that does not
/// fold, and merges or sharp vertex placements that still end up in a folded triangle are undone.
///
/// Crossings: where a wedge or skin is thinner than a cell, the grid under-resolves it and vertices that each
/// look right can still make triangles pass through each other. A vertex whose placement is off the surface goes
/// to an in-cell point, and the vertices of triangles that actually cross (<see cref="Crossings"/>) are demoted
/// step by step and the mesh contoured again until none cross; <see cref="Result.Crossings"/> reports any left.
///
/// Refinement (MeshAt): base cells that do not resolve the surface, or whose polygons miss it by more than a quarter
/// of the finest cell, are sampled up to 2^K times finer, with enough of their neighbours to join them consistently.
/// Features that leave no trace at base resolution, and features thinner than the finest cell, can still be missed;
/// the repair then makes such places valid, not accurate.
///
/// Tunnel faces: when both cells sharing a four-crossing face route all four crossings through one
/// sheet, the surface passes through that face as two strips joining the same two cell vertices, so
/// the four polygons around the face would share one mesh edge. <see cref="RepairTunnels"/> subdivides
/// one strip so each has its own edges. Other non-manifold cases, where features are about a cell
/// wide, can remain; <see cref="Defects"/> counts them and FrepMesh reports them.
public static class DualContour
{
    /// Crossings: triangle pairs that still pass through each other (see <see cref="Meshing.Crossings"/>), from a check
    /// of the whole output; 0 unless the repair in Mesh ran out of vertices to demote or of rounds.
    /// Demoted: vertex placements the repair stepped down.
    /// Refine: extra levels actually used (lowered from the request when over budget or below the minimum cell size).
    /// RefinedCells: base cells sampled finer. Unresolved: finest-level vertices in the output at their in-cell point,
    /// placed there or demoted there by the crossing repair (a numerical fallback, not proof of a thin feature). UnsafeSamples: base samples whose sign changed when
    /// evaluated again (the field changed faster than its Lipschitz bound, so skipping samples was unsafe there).
    /// ProbeFailures: output triangles with a coarse or merged vertex that a probe (centroid and edge midpoints,
    /// distance estimated as |f| / |grad f|) still finds farther than 0.25 MinCellSize from the surface, after the
    /// refinement passes for them ran out (see MeshAt); 0 without refinement, where it is not checked.
    /// AccuracyCellSize: the cell of the achieved Refine level, which the accuracy probes use. MinCellSize: the smallest
    /// cell actually sampled (deeper where ExtraDepth was used). ExtraDepth: deep levels actually used (0 if none).
    /// DeepCells: fine cells given extra depth. Contradictory: finest vertices whose sheet still holds opposing surface
    /// normals that its vertex does not fit. DeepPassesRejected: deep passes built but not kept because they left more
    /// crossing pairs than the mesh kept (whose own depth and counts are the ones reported).
    /// Fragments: small disconnected components, no more than 4 finest cells across. Often these are bits of a
    /// feature thinner than the finest cell whose links were too thin to sample, but not always. Kept, only counted.
    public sealed record Result(List<V3> Vertices, List<int> Triangles, int Nx, int Ny, int Nz, double CellSize, int Crossings = 0, int Demoted = 0,
        double MinCellSize = 0, int Refine = 0, int RefinedCells = 0, int Unresolved = 0, int UnsafeSamples = 0, int Fragments = 0, int ProbeFailures = 0,
        double AccuracyCellSize = 0, int ExtraDepth = 0, int DeepCells = 0, int Contradictory = 0, int DeepPassesRejected = 0);

    const int Block = 8;

    /// How far a vertex may sit outside its cell, in finest cells (see CellVertex).
    public static double Slack = 1.0;

    /// Eigenvalues of the fit below this fraction of the largest are treated as zero (see Place).
    public static double Cutoff = 0.1;

    /// Cutoff for the sharper second attempt in Place; 0 disables it.
    public static double SharpCutoff = 1e-3;

    /// A placement farther than this from the surface, in finest cells (|f| / |grad f|), means the cell under-resolves
    /// the surface there (a wedge or skin thinner than a cell): the vertex goes to its in-cell point (see Place).
    public static double OffSurface = 0.1;

    /// Most rounds of the crossing repair (see Mesh). Each round demotes at least one vertex one step, and every
    /// vertex has a few steps at most, so the repair ends anyway; this bounds the time.
    public static int RepairRounds = 16;

    /// Budget for refinement: estimated working memory (samples plus about 400 bytes per finest surface cell) and
    /// finest surface cells. Over either, the attempt is dropped and repeated one level coarser.
    public static long MemoryBudget = 1_500_000_000;
    public static long FineCellBudget = 4_000_000;

    /// Restarts after a coarse cell is still in a crossing at its last placement: it is refined and the mesh built
    /// again, at most this many times.
    public static int RefineRestarts = 3;

    /// With refinement, a coarse cell in a crossing that has no sharp placement left to undo is refined and the mesh
    /// built again, instead of moving its vertex to the in-cell point (which can cut across a crease the cell held).
    /// More accurate, but the second build roughly doubles the time; off by default.
    public static bool RefineStuckCoarse = false;

    /// Passes of refinement for output triangles that miss the surface by more than 0.25 finest cells (see MeshAt).
    public static int AccuracyPasses = 2;

    /// Deep levels below the Refine level, used only in fine cells that still hold contradictory surface evidence
    /// (see MeshAt). Internal; 0 to 3.
    public static int ExtraDepth = 2;

    /// In a crossing pair, only the vertices highest on the ladder take a step (a merge before a sharp placement
    /// before a stable one), instead of all six; vertices already on their feature are not moved for a neighbour.
    public static bool GentleDemotion = true;

    /// Resolve faces crossed four times by the field at the face centre (see FaceCentreInside), instead of always
    /// pairing the crossings around the inside corners.
    public static bool FaceCentres = true;

    /// Test hooks: base cells (base coordinates) forced into the refined set, and fine cells (fine coordinates) forced
    /// into the deep set, so fixtures can place junctions where they want them. Null in normal use.
    public static Func<int, int, int, bool>? ForceRefine, ForceDeep;

    /// Optional progress and timing messages (for test harnesses).
    public static Action<string>? Trace;
    static readonly System.Diagnostics.Stopwatch clock = new();
    static void Mark(string what) => Trace?.Invoke($"  [{clock.ElapsedMilliseconds,6} ms] {what}");

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

    /// Field samples. Indices are on the finest lattice (spacing H). The base lattice is every Step-th point
    /// (spacing Hb = H·Step, Step = 2^K); its samples are a dense array. Finer samples exist only in blocks, one per
    /// refined or interface base cell (see Mesh); reading one that no block holds is a bug. Every position comes from
    /// one formula on its global index (P), so a point that two blocks share has one value.
    /// Coordinates handed to <see cref="Qef"/> are relative to Origin.
    sealed class Grid
    {
        public readonly FrepField Field;
        public readonly V3 Origin;
        /// H: finest (deep, C) spacing; HB: fine (B) spacing, the Refine level's cell; Hb: base (A) spacing.
        public readonly double H, HB, Hb;
        /// K: fine levels below base; E: deep levels below fine. Step = base cell in finest units (2^(K+E));
        /// StepB = fine cell in finest units (2^E); PerBase = fine cells per base cell edge (2^K).
        public readonly int K, E, Step, StepB, PerBase;
        public readonly int Nbx, Nby, Nbz;                // base cells
        public readonly int NBx, NBy, NBz;                // fine (B) cells
        public readonly int Nx, Ny, Nz, Sx, Sy, Sz;       // finest cells and samples
        public readonly float[] F;                        // base samples
        /// Fine samples, (PerBase + 1)³ per refined or interface base cell, on the B lattice.
        public readonly ConcurrentDictionary<int, float[]> Blocks = new();
        /// Deep samples, (StepB + 1)³ per fine cell given extra depth or next to one, on the C lattice.
        public readonly ConcurrentDictionary<long, float[]> Deep = new();

        /// Fine cells built for a refined base cell without deep cells, reused by later builds (same samples, same result).
        public readonly ConcurrentDictionary<int, (List<(long Cell, int Packed, int Count)> Cells, List<(V3, Qef, V3?, V3, bool, double, long, bool, bool)> Points, int Bad)> FineCellCache = new();

        /// Estimated bytes held by FineCellCache, charged in the closures' budget checks (a build counts the entries it
        /// uses through its own cell count instead): each list's allocated capacity times its element's actual size,
        /// plus 256 bytes per entry for the dictionary node, the two list objects and their array headers (a
        /// conservative allowance; an empty entry still holds them).
        public long FineCacheBytes;
        public static long CacheBytes((List<(long Cell, int Packed, int Count)> Cells, List<(V3, Qef, V3?, V3, bool, double, long, bool, bool)> Points, int Bad) e) =>
            256 + (long)e.Cells.Capacity * CellEntrySize + (long)e.Points.Capacity * PointEntrySize;
        static readonly long CellEntrySize = System.Runtime.CompilerServices.Unsafe.SizeOf<(long, int, int)>();
        static readonly long PointEntrySize = System.Runtime.CompilerServices.Unsafe.SizeOf<(V3, Qef, V3?, V3, bool, double, long, bool, bool)>();

        /// Base cells already accepted as interface cells by Close (see InterfaceOk); kept across closures.
        public readonly HashSet<int> InterfaceAccepted = [];

        public Grid(FrepField field, V3 origin, double hb, int k, int e, int nbx, int nby, int nbz)
        {
            Field = field;
            Origin = origin;
            K = k;
            E = e;
            Step = 1 << (k + e);
            StepB = 1 << e;
            PerBase = 1 << k;
            Hb = hb;
            HB = hb / PerBase;
            H = hb / Step;
            Nbx = nbx; Nby = nby; Nbz = nbz;
            NBx = nbx << k; NBy = nby << k; NBz = nbz << k;
            Nx = nbx << (k + e); Ny = nby << (k + e); Nz = nbz << (k + e);
            Sx = Nx + 1; Sy = Ny + 1; Sz = Nz + 1;
            F = new float[(long)(nbx + 1) * (nby + 1) * (nbz + 1)];
        }

        public V3 P(int i, int j, int k) => new(Origin.X + i * H, Origin.Y + j * H, Origin.Z + k * H);

        public long BaseSample(int bi, int bj, int bk) => ((long)bk * (Nby + 1) + bj) * (Nbx + 1) + bi;

        /// The sample at a finest-lattice index on the grid: base array, fine block or deep block, by lattice.
        public float Value(int i, int j, int k)
        {
            int ma = Step - 1;
            if (((i | j | k) & ma) == 0) return F[BaseSample(i >> (K + E), j >> (K + E), k >> (K + E))];
            int mb = StepB - 1;
            if (((i | j | k) & mb) == 0)
            {
                // A fine-lattice point: a fine block, by fine index.
                int fi = i >> E, fj = j >> E, fk = k >> E, m = PerBase - 1, side = PerBase + 1;
                int bi = fi >> K, bj = fj >> K, bk = fk >> K;
                // A point on a base cell's face belongs to the blocks on both sides; either holds it.
                for (int c = 0; c < 8; c++)
                {
                    if (((c & 1) != 0 && (fi & m) != 0) || ((c & 2) != 0 && (fj & m) != 0) || ((c & 4) != 0 && (fk & m) != 0)) continue;
                    int ci = bi - (c & 1), cj = bj - ((c >> 1) & 1), ck = bk - (c >> 2);
                    if ((uint)ci >= (uint)Nbx || (uint)cj >= (uint)Nby || (uint)ck >= (uint)Nbz) continue;
                    if (Blocks.TryGetValue(BaseCell(ci, cj, ck), out var block))
                        return block[((fk - (ck << K)) * side + (fj - (cj << K))) * side + (fi - (ci << K))];
                }
                throw new InvalidOperationException($"DualContour: no fine sample at ({i}, {j}, {k}).");
            }
            {
                // A deep-lattice point: a deep block, by fine cell.
                int di = i >> E, dj = j >> E, dk = k >> E, side = StepB + 1;
                for (int c = 0; c < 8; c++)
                {
                    if (((c & 1) != 0 && (i & mb) != 0) || ((c & 2) != 0 && (j & mb) != 0) || ((c & 4) != 0 && (k & mb) != 0)) continue;
                    int ci = di - (c & 1), cj = dj - ((c >> 1) & 1), ck = dk - (c >> 2);
                    if (!HasFineCell(ci, cj, ck)) continue;
                    if (Deep.TryGetValue(FineCell(ci, cj, ck), out var block))
                        return block[((k - (ck << E)) * side + (j - (cj << E))) * side + (i - (ci << E))];
                }
                throw new InvalidOperationException($"DualContour: no deep sample at ({i}, {j}, {k}).");
            }
        }

        public long FineCell(int fi, int fj, int fk) => ((long)fk * NBy + fj) * NBx + fi;
        public bool HasFineCell(int fi, int fj, int fk) => (uint)fi < (uint)NBx && (uint)fj < (uint)NBy && (uint)fk < (uint)NBz;
        public (int, int, int) FineCoords(long cell) => ((int)(cell % NBx), (int)(cell / NBx % NBy), (int)(cell / ((long)NBx * NBy)));

        /// Inside test at a sample; anything off the grid is outside.
        public bool In(int i, int j, int k) =>
            (uint)i < (uint)Sx && (uint)j < (uint)Sy && (uint)k < (uint)Sz && Value(i, j, k) < 0;

        public long Cell(int i, int j, int k) => ((long)k * Ny + j) * Nx + i;
        public bool HasCell(int i, int j, int k) => (uint)i < (uint)Nx && (uint)j < (uint)Ny && (uint)k < (uint)Nz;

        public int BaseCell(int bi, int bj, int bk) => (bk * Nby + bj) * Nbx + bi;
        public bool HasBaseCell(int bi, int bj, int bk) => (uint)bi < (uint)Nbx && (uint)bj < (uint)Nby && (uint)bk < (uint)Nbz;
        public (int, int, int) BaseCoords(int cell) => (cell % Nbx, cell / Nbx % Nby, cell / (Nbx * Nby));

        readonly ConcurrentDictionary<(int, int, int), bool> centres = new();

        /// Bytes charged per cached face centre in the memory budget. Measured about 61 retained bytes per entry
        /// including dictionary growth (sol, #339); charged at twice that.
        public const long CentreBytes = 128;

        /// Bytes the face-centre cache is charged in the budget: its whole allowance (CentreCap entries) when it has
        /// one, reserved in advance so the cache can never grow into memory already given to samples or cells (sol
        /// #379); otherwise what it holds.
        public long CentreMemory => CentreCap == long.MaxValue ? centres.Count * CentreBytes : CentreCap * CentreBytes;

        /// Inside test at a point given in doubled finest-lattice coordinates (so face centres are integers). One
        /// formula and one cache for every caller, so every consumer of a face decides it the same way. A value of
        /// exactly 0 counts as outside, as at the samples.
        public bool InsideDoubled(int x2, int y2, int z2)
        {
            if (centres.TryGetValue((x2, y2, z2), out bool known)) return known;
            // Bounded per insertion: past its allowance the attempt is abandoned (the caller retries coarser).
            if (centres.Count >= CentreCap) throw new BudgetExceeded();
            return centres.GetOrAdd((x2, y2, z2), key => Field.Value(new V3(
                Origin.X + key.Item1 * (H * 0.5), Origin.Y + key.Item2 * (H * 0.5), Origin.Z + key.Item3 * (H * 0.5))) < 0);
        }

        /// Entries the face-centre cache may hold: a quarter of the memory budget, unless nothing can be coarser.
        public long CentreCap = long.MaxValue;

        /// Evaluates the (PerBase + 1)³ fine samples of base cell (bi, bj, bk).
        public float[] Sample(int bi, int bj, int bk)
        {
            int side = PerBase + 1;
            var block = new float[side * side * side];
            int i0 = bi << K, j0 = bj << K, k0 = bk << K;
            for (int k = 0; k < side; k++)
                for (int j = 0; j < side; j++)
                    for (int i = 0; i < side; i++)
                        block[(k * side + j) * side + i] = (float)Field.Value(P((i0 + i) << E, (j0 + j) << E, (k0 + k) << E));
            return block;
        }

        /// Evaluates the (StepB + 1)³ deep samples of fine cell (fi, fj, fk).
        public float[] SampleDeep(int fi, int fj, int fk)
        {
            int side = StepB + 1;
            var block = new float[side * side * side];
            int i0 = fi << E, j0 = fj << E, k0 = fk << E;
            for (int k = 0; k < side; k++)
                for (int j = 0; j < side; j++)
                    for (int i = 0; i < side; i++)
                        block[(k * side + j) * side + i] = (float)Field.Value(P(i0 + i, j0 + j, k0 + k));
            return block;
        }
    }

    /// Thrown when a bounded allocation would exceed the budget mid-phase; the attempt is retried coarser.
    sealed class BudgetExceeded : Exception { }

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

        public readonly V3 Mass => mass * (1.0 / N);

        readonly V3 Times(V3 v) => new(xx * v.X + xy * v.Y + xz * v.Z, xy * v.X + yy * v.Y + yz * v.Z, xz * v.X + yz * v.Y + zz * v.Z);

        /// Minimiser, solved about the mass point. Small eigenvalues are dropped, so on a flat or gently
        /// curved patch the vertex stays near the mass point instead of flying off.
        public readonly V3 Solve(double cutoff)
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
                if (val[e] > cutoff * max)
                    x += vec[e] * (V3.Dot(vec[e], rhs) / val[e]);
            return x;
        }

        /// Number of eigenvalues above <paramref name="cutoff"/> × the largest: how many independent plane
        /// directions the fit keeps (3 at a corner, 2 along a crease, 1 on a smooth patch).
        public readonly int Rank(double cutoff)
        {
            Span<double> a = stackalloc double[6] { xx, xy, xz, yy, yz, zz };
            Span<double> val = stackalloc double[3];
            Span<V3> vec = stackalloc V3[3];
            SymmetricEigen(a, val, vec);
            double max = Math.Max(val[0], Math.Max(val[1], val[2]));
            int rank = 0;
            for (int e = 0; e < 3; e++)
                if (val[e] > cutoff * max) rank++;
            return rank;
        }

        /// Root-mean-square distance from <paramref name="x"/> to the planes.
        public readonly double Rms(V3 x) =>
            Math.Sqrt(Math.Max(V3.Dot(x, Times(x)) - 2 * V3.Dot(x, new V3(bx, by, bz)) + c, 0) / N);
    }

    /// <paramref name="resolution"/> = base cells along the longest side of the field's bounds.
    /// <paramref name="simplify"/> = how far, in base cells (RMS), a merged cell's vertex may be from the
    /// surface planes it replaces; 0 keeps the uniform grid.
    /// <paramref name="refine"/> = extra levels (0 to 3) where the base grid under-resolves the field: such cells, and
    /// enough of their neighbours to join them consistently, are sampled at cells of 1 / 2^refine (see MeshAt).
    /// <paramref name="minCell"/> = the finest cell may not be smaller than this (model units); refine is lowered to fit.
    public static Result Mesh(FrepField field, int resolution, double simplify = 0, int refine = 0, double minCell = 0)
    {
        var (bmin, bmax) = field.Bounds();
        var size = bmax - bmin;
        if (!(size.X > 0 && size.Y > 0 && size.Z > 0) || double.IsInfinity(size.X + size.Y + size.Z))
            return new Result([], [], 0, 0, 0, 0); // no surface
        double h = Math.Max(size.X, Math.Max(size.Y, size.Z)) / resolution;
        clock.Restart();
        int k = Math.Clamp(refine, 0, 3);
        while (k > 0 && h / (1 << k) < minCell) k--;
        for (; ; k--)
        {
            Result? result;
            int e = k > 0 ? Math.Clamp(ExtraDepth, 0, 3) : 0;
            while (e > 0 && h / (1 << (k + e)) < minCell) e--;
            // Over budget: fewer deep levels first (a fresh grid, as positions and caches depend on the depth), then a
            // lower Refine level.
            for (result = null; result == null && e >= 0; e--)
            {
                try { result = MeshAt(field, bmin, size, h, simplify, k, e); }
                catch (BudgetExceeded) when (k > 0) { result = null; }
                // Thrown inside a parallel loop it arrives wrapped; only a budget overrun is a retry, nothing else.
                catch (AggregateException ae) when (k > 0 && ae.Flatten().InnerExceptions.All(x => x is BudgetExceeded)) { result = null; }
            }
            if (result != null) return result;
        }
    }

    /// One attempt at <paramref name="k"/> extra levels; null if it went over budget (never at k = 0).
    static Result? MeshAt(FrepField field, V3 bmin, V3 size, double h, double simplify, int k, int e)
    {
        // Two cells of padding: every sample on the grid boundary is strictly outside.
        var g = new Grid(field, bmin - new V3(2 * h, 2 * h, 2 * h), h, k, e,
            (int)Math.Ceiling(size.X / h) + 4, (int)Math.Ceiling(size.Y / h) + 4, (int)Math.Ceiling(size.Z / h) + 4);
        if (k > 0) g.CentreCap = MemoryBudget / Grid.CentreBytes / 4;
        int nbx = g.Nbx, nby = g.Nby, nbz = g.Nbz, sx = nbx + 1, sy = nby + 1, sz = nbz + 1;
        var f = g.F;

        // 1. Base samples. |field| never exceeds the distance to the surface (see FrepField), so a block of
        //    samples whose centre is farther from the surface than the block's radius has no sign change:
        //    fill it without evaluating.
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
                    for (int kk = k0; kk < k1; kk++)
                        for (int j = j0; j < j1; j++)
                        {
                            long row = ((long)kk * sy + j) * sx;
                            for (int i = i0; i < i1; i++)
                                f[row + i] = skip ? (float)fc : (float)field.Value(g.P(i << (k + e), j << (k + e), kk << (k + e)));
                        }
                }
            }
        });

        // 2. Base cells: one vertex per surface sheet in each base cell with a sign change, and whether the base
        //    grid resolves the surface there.
        Mark("base samples");
        var bases = BaseCells(g);
        Mark($"base cells: {bases.Count}");
        var refined = new HashSet<int>();
        if (k > 0)
        {
            foreach (var (cell, info) in bases)
                if (info.Unresolved) refined.Add(cell);
            if (ForceRefine != null)
                for (int bk = 0; bk < g.Nbz; bk++)
                    for (int bj = 0; bj < g.Nby; bj++)
                        for (int bi = 0; bi < g.Nbx; bi++)
                            if (ForceRefine(bi, bj, bk)) refined.Add(g.BaseCell(bi, bj, bk));
            // Accuracy: wherever the mesh stays coarse it must be within a quarter of the finest cell of the surface,
            // so probe the polygons the base cells will form and refine where they miss.
            int seeded = SeedByAccuracy(g, bases, refined, 0.25 * g.HB);
            Mark($"accuracy seeds: {seeded} cells");
        }

        int unsafeSamples = 0;
        var forbidden = new HashSet<long>(); // merged cells that missed the surface: not merged again
        int accuracyPass = 0;
        for (int restart = 0; ; restart++)
        {
            // 3. Close the refined set (k > 0 only): see Close.
            var near = new HashSet<int>();
            if (k > 0 && !Close(g, refined, near, bases, ref unsafeSamples)) return null;
            Mark($"closure: {refined.Count} refined, {near.Count} interface, {g.Blocks.Count} blocks");

            bool last = restart >= RefineRestarts;
            var mesh = Build(g, bases, refined, near, forbidden, simplify * h, last, NoDeep, NoDeep, out var stuck, out var missCells, out var missMerged,
                out var deepCandidates, out bool over);
            if (over) return null;
            if (k == 0) return mesh;
            // A coarse cell still in a crossing at its last placement: refine it too and build again.
            bool again = mesh.Crossings > 0 && stuck.Count > 0 && restart < RefineRestarts;
            // Output triangles off the surface at a coarse or merged vertex: refine those cells, stop those merges.
            // A pass runs only if it changes something (a cell joins the refined set or a merge is forbidden).
            if (mesh.ProbeFailures > 0 && accuracyPass < AccuracyPasses && restart < RefineRestarts)
            {
                int before = refined.Count + forbidden.Count;
                refined.UnionWith(missCells);
                forbidden.UnionWith(missMerged);
                if (refined.Count + forbidden.Count > before) { again = true; accuracyPass++; }
            }
            if (!again)
            {
                mesh = mesh with { UnsafeSamples = unsafeSamples };
                if (e > 0 && ForceDeep != null)
                    for (int fk = 0; fk < g.NBz; fk++)
                        for (int fj = 0; fj < g.NBy; fj++)
                            for (int fi = 0; fi < g.NBx; fi++)
                                if (ForceDeep(fi, fj, fk)) deepCandidates.Add(g.FineCell(fi, fj, fk));
                if (e == 0 || deepCandidates.Count == 0) return mesh;
                return Deepen(g, bases, refined, near, forbidden, simplify * h, mesh, deepCandidates, ref unsafeSamples);
            }
            refined.UnionWith(stuck);
        }
    }

    static readonly HashSet<long> NoDeep = [];

    /// Passes of "collect contradictory fine cells, give them extra depth, build again".
    public static int DeepPasses = 2;

    /// Diagnostic switch: false keeps every deep pass even if it leaves more crossings (to inspect them).
    public static bool RejectWorseDeep = true;

    /// The deep tier: fine cells whose surface evidence the fine lattice cannot represent (one sheet holding opposing
    /// normals that its vertex does not fit, an all-fine triangle that misses the surface by more than 0.25 of the
    /// Refine level's cell, or a crossing the repair could not remove) are sampled 2^E times finer, with enough of
    /// their neighbours to join them (CloseDeep), and the mesh is built again; at most DeepPasses times. Null if over
    /// budget (the caller retries with fewer deep levels). Returns the last mesh built.
    static Result? Deepen(Grid g, Dictionary<int, BaseInfo> bases, HashSet<int> refined, HashSet<int> near, HashSet<long> forbidden, double tolerance,
        Result levelMesh, HashSet<long> candidates, ref int unsafeSamples)
    {
        var deep = new HashSet<long>(candidates);
        var deepNear = new HashSet<long>();
        var mesh = levelMesh;
        int rejected = 0;
        // The level-K mesh is kept as the fallback while the deep tier is built: reserve its memory.
        static long Bytes(Result r) => (long)r.Vertices.Count * 24 + (long)r.Triangles.Count * 4;
        long reserved = Bytes(levelMesh);
        for (int pass = 0; pass < DeepPasses; pass++)
        {
            if (!CloseDeep(g, refined, near, bases, deep, deepNear, ref unsafeSamples, reserved)) return null;
            Mark($"deep closure: {deep.Count} deep, {deepNear.Count} interface, {g.Deep.Count} deep blocks");
            var built = Build(g, bases, refined, near, forbidden, tolerance, true, deep, deepNear, out _, out _, out _, out var more, out bool over, reserved);
            if (over) return null;
            // A deeper pass is kept only if it leaves no more crossing pairs than the best mesh so far (the level mesh
            // to begin with): extra depth must not trade validity for accuracy.
            if (built.Crossings <= mesh.Crossings || !RejectWorseDeep) mesh = built with { UnsafeSamples = unsafeSamples };
            else
            {
                rejected++;
                Mark($"deep pass {pass}: {built.Crossings} crossing pairs, against {mesh.Crossings}; kept the previous mesh");
            }
            reserved = Bytes(mesh); // the mesh kept so far is retained for the next pass
            more.ExceptWith(deep);
            if (more.Count == 0) break;
            deep.UnionWith(more);
        }
        return mesh with { DeepPassesRejected = rejected };
    }

    /// Closes the deep set the way Close closes the refined set, one tier down:
    /// 1. every fine-lattice neighbour (26) of a deep cell must be a sampled fine cell: its base cell joins the refined
    ///    set, and the base-level closure is rerun, until none is missing;
    /// 2. a fine cell next to the deep set and not in it is an interface cell, with a block of deep samples. It stays
    ///    empty if none of its fine edges changes sign and every deep sample has its corners' sign; it stays one vertex
    ///    if it has one sheet, no face crossed four times, at most one sign change along each fine edge, no deep sample
    ///    of a face whose corners agree with another sign, and one loop over its faces on the deep lattice; otherwise it
    ///    joins the deep set. To a fixed point.
    /// 3. a deep sample at a fine or base position must have the sign stored there: fine blocks are fully evaluated and
    ///    the same position is the same point, so a difference is a bug and throws.
    /// False if over budget.
    static bool CloseDeep(Grid g, HashSet<int> refined, HashSet<int> near, Dictionary<int, BaseInfo> bases, HashSet<long> deep, HashSet<long> deepNear,
        ref int unsafeSamples, long reserved)
    {
        int sb = g.StepB, side = sb + 1, per = g.PerBase;
        long deepBytes = (long)side * side * side * 4, fineBytes = (long)(per + 1) * (per + 1) * (per + 1) * 4;
        var verdict = new Dictionary<long, bool>();
        while (true)
        {
            // 1. Surround the deep set with fine cells.
            bool grewBase = false;
            foreach (long cell in deep)
            {
                var (fi, fj, fk) = g.FineCoords(cell);
                for (int dk = -1; dk <= 1; dk++)
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int ni = fi + di, nj = fj + dj, nk = fk + dk;
                            if (!g.HasFineCell(ni, nj, nk)) continue;
                            if (refined.Add(g.BaseCell(ni >> g.K, nj >> g.K, nk >> g.K))) grewBase = true;
                        }
            }
            if (grewBase)
            {
                if (!Close(g, refined, near, bases, ref unsafeSamples, (long)g.Deep.Count * deepBytes + reserved)) return false;
                continue;
            }

            // 2. Interface cells and deep blocks.
            deepNear.Clear();
            foreach (long cell in deep)
            {
                var (fi, fj, fk) = g.FineCoords(cell);
                for (int dk = -1; dk <= 1; dk++)
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            if (!g.HasFineCell(fi + di, fj + dj, fk + dk)) continue;
                            long n = g.FineCell(fi + di, fj + dj, fk + dk);
                            if (!deep.Contains(n)) deepNear.Add(n);
                        }
            }
            var missing = deep.Concat(deepNear).Where(c => !g.Deep.ContainsKey(c)).ToList();
            long Memory() => (long)g.Deep.Count * deepBytes + (long)g.Blocks.Count * fineBytes + (long)deep.Count * sb * sb * sb * 400 + g.CentreMemory + g.FineCacheBytes + reserved;
            if (Memory() + (long)missing.Count * deepBytes > MemoryBudget) return false;
            Parallel.ForEach(missing, cell =>
            {
                var (fi, fj, fk) = g.FineCoords(cell);
                g.Deep[cell] = g.SampleDeep(fi, fj, fk);
            });
            // 3. Sample identity at the fine-lattice corners of every new deep block.
            foreach (long cell in missing)
            {
                var (fi, fj, fk) = g.FineCoords(cell);
                var block = g.Deep[cell];
                for (int c = 0; c < 8; c++)
                {
                    int ci = (c & 1) * sb, cj = ((c >> 1) & 1) * sb, ck = (c >> 2) * sb;
                    bool deepSign = block[(ck * side + cj) * side + ci] < 0;
                    bool stored = g.In(fi * sb + ci, fj * sb + cj, fk * sb + ck);
                    if (deepSign != stored)
                        throw new InvalidOperationException($"DualContour: deep sample disagrees with the stored sample at fine cell ({fi}, {fj}, {fk}).");
                }
            }

            bool grew = false;
            foreach (long cell in deepNear.ToList())
            {
                if (verdict.TryGetValue(cell, out bool ok) && ok) continue;
                ok = DeepInterfaceOk(g, cell);
                verdict[cell] = ok;
                if (!ok) { deep.Add(cell); grew = true; }
            }
            if (Memory() > MemoryBudget) return false;
            if (!grew) return true;
            if ((long)deep.Count * sb * sb * sb > 4 * FineCellBudget) return false;
        }
    }

    /// Can interface fine cell <paramref name="cell"/> stay empty or one vertex next to deep cells (see CloseDeep)?
    static bool DeepInterfaceOk(Grid g, long cell)
    {
        var (fi, fj, fk) = g.FineCoords(cell);
        int sb = g.StepB, side = sb + 1, i0 = fi * sb, j0 = fj * sb, k0 = fk * sb;
        var block = g.Deep[cell];
        if (!SignComponentsOk(block, side)) return false;
        bool At(int i, int j, int k) => block[(k * side + j) * side + i] < 0;
        Span<bool> corner = stackalloc bool[8];
        int inside = 0;
        for (int c = 0; c < 8; c++)
        {
            corner[c] = At((c & 1) * sb, ((c >> 1) & 1) * sb, (c >> 2) * sb);
            if (corner[c]) inside++;
        }
        // Each fine edge changes sign at most once along its deep samples.
        foreach (var (a, b) in Edges)
        {
            int ax = a & 1, ay = (a >> 1) & 1, az = a >> 2, dx = (b & 1) - ax, dy = ((b >> 1) & 1) - ay, dz = (b >> 2) - az;
            int changes = 0;
            bool prev = corner[a];
            for (int t = 1; t <= sb; t++)
            {
                bool now = At(ax * sb + dx * t, ay * sb + dy * t, az * sb + dz * t);
                if (now != prev) changes++;
                prev = now;
            }
            if (changes > 1) return false;
        }
        if (inside == 0 || inside == 8)
        {
            // Stays empty only if nothing changes sign anywhere in it.
            foreach (float v in block)
                if ((v < 0) != corner[0]) return false;
            return true;
        }
        // No hidden sign change on a face whose corners agree.
        for (int f = 0; f < 6; f++)
        {
            int[] fc = FaceCorners[f];
            bool sign = corner[fc[0]];
            if (corner[fc[1]] != sign || corner[fc[2]] != sign || corner[fc[3]] != sign) continue;
            int axis = f / 2, at = (f % 2) * sb;
            for (int u = 0; u <= sb; u++)
                for (int v = 0; v <= sb; v++)
                {
                    bool s = axis == 0 ? At(at, u, v) : axis == 1 ? At(u, at, v) : At(u, v, at);
                    if (s != sign) return false;
                }
        }
        if (FourCrossingFace(corner)) return false;
        Span<int> sheet = stackalloc int[12];
        if (Sheets(corner, sheet, g, i0, j0, k0, sb) != 1) return false;
        return Octree.BoundaryLoops(g, i0, j0, k0, sb, 1) == 1;
    }

    /// Distance estimate from point p to the surface, |f| / |grad f|; +infinity when the gradient is unusable.
    static double Distance(Grid g, V3 p, double step)
    {
        double f = Math.Abs(g.Field.Value(p));
        if (f == 0) return 0;
        double len = g.Field.Gradient(p, step).Length;
        double d = f / len;
        return len > 0 && double.IsFinite(d) ? d : double.PositiveInfinity;
    }

    /// Adds to <paramref name="refined"/> the base cells around every base edge whose polygon (the four cells' vertices
    /// for that edge) misses the surface by more than <paramref name="tolerance"/> at its centre or the middle of a
    /// side: the polygons the coarse cells will be meshed with. Returns how many cells were added.
    static int SeedByAccuracy(Grid g, Dictionary<int, BaseInfo> bases, HashSet<int> refined, double tolerance)
    {
        int step = g.Step;
        var found = new ConcurrentBag<int>();
        Parallel.For(0, g.Nbz + 1, bk =>
        {
            Span<int> cell = stackalloc int[4];
            Span<V3> corner = stackalloc V3[4];
            Span<int> at = stackalloc int[3];
            for (int bj = 0; bj <= g.Nby; bj++)
                for (int bi = 0; bi <= g.Nbx; bi++)
                    for (int d = 0; d < 3; d++)
                    {
                        at[0] = bi; at[1] = bj; at[2] = bk;
                        bool inside = g.In(bi * step, bj * step, bk * step);
                        at[d]++;
                        if (at[d] > (d == 0 ? g.Nbx : d == 1 ? g.Nby : g.Nbz)) continue;
                        if (inside == g.In(at[0] * step, at[1] * step, at[2] * step)) continue;
                        int p = Octree.P(d), q = Octree.Q(d);
                        bool ok = true, anyCoarse = false;
                        for (int quadrant = 0; quadrant < 4 && ok; quadrant++)
                        {
                            at[0] = bi; at[1] = bj; at[2] = bk;
                            at[p] += (quadrant is 1 or 2 ? 1 : 0) - 1;
                            at[q] += (quadrant >= 2 ? 1 : 0) - 1;
                            if (!g.HasBaseCell(at[0], at[1], at[2])) { ok = false; break; }
                            int c = g.BaseCell(at[0], at[1], at[2]);
                            if (!bases.TryGetValue(c, out var info)) { ok = false; break; }
                            cell[quadrant] = c;
                            corner[quadrant] = info.Points[(info.Packed >> (2 * Octree.EdgeInQuadrant[d][quadrant])) & 3];
                            anyCoarse |= !refined.Contains(c);
                        }
                        if (!ok || !anyCoarse) continue;
                        double unit = 1e-5 * g.Hb;
                        bool miss = Distance(g, (corner[0] + corner[1] + corner[2] + corner[3]) * 0.25, unit) > tolerance;
                        for (int s = 0; s < 4 && !miss; s++)
                            miss = Distance(g, (corner[s] + corner[(s + 1) % 4]) * 0.5, unit) > tolerance;
                        if (miss)
                            for (int s = 0; s < 4; s++) found.Add(cell[s]);
                    }
        });
        int before = refined.Count;
        refined.UnionWith(found);
        return refined.Count - before;
    }

    /// A base cell with a sign change: its vertices (one per sheet) and whether the base grid fails to resolve the
    /// surface there, which puts it in the refined set: more than one sheet, a face crossed four times (a possible
    /// tunnel), a placement off the surface, a fit residual over 0.05 cells, or two normals more than 90° apart.
    sealed record BaseInfo(int Packed, int Count, V3[] Points, Qef[] Qefs, V3?[] Stable, bool Unresolved);

    static Dictionary<int, BaseInfo> BaseCells(Grid g)
    {
        int nbx = g.Nbx, nby = g.Nby, nbz = g.Nbz, step = g.Step;
        var slabs = new List<(int, BaseInfo)>[nbz];
        Parallel.For(0, nbz, bk =>
        {
            var list = new List<(int, BaseInfo)>();
            Span<bool> s = stackalloc bool[8];
            Span<int> sheet = stackalloc int[12];
            for (int bj = 0; bj < nby; bj++)
                for (int bi = 0; bi < nbx; bi++)
                {
                    int inside = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        s[c] = g.In((bi + (c & 1)) * step, (bj + ((c >> 1) & 1)) * step, (bk + (c >> 2)) * step);
                        if (s[c]) inside++;
                    }
                    if (inside == 0 || inside == 8) continue;
                    int count = Sheets(s, sheet, g, bi * step, bj * step, bk * step, step), packed = 0;
                    for (int e = 0; e < 12; e++)
                        if (sheet[e] >= 0) packed |= sheet[e] << (2 * e);
                    bool unresolved = count > 1 || FourCrossingFace(s);
                    var points = new V3[count];
                    var qefs = new Qef[count];
                    var stables = new V3?[count];
                    for (int c = 0; c < count; c++)
                    {
                        points[c] = CellVertex(g, bi * step, bj * step, bk * step, step, g.Hb, s, sheet, c, out qefs[c], out stables[c], out bool bad);
                        unresolved |= bad;
                    }
                    list.Add((g.BaseCell(bi, bj, bk), new BaseInfo(packed, count, points, qefs, stables, unresolved)));
                }
            slabs[bk] = list;
        });
        var result = new Dictionary<int, BaseInfo>();
        foreach (var list in slabs)
            foreach (var (cell, info) in list) result[cell] = info;
        return result;
    }

    static bool FourCrossingFace(ReadOnlySpan<bool> inside)
    {
        foreach (var c in FaceCorners)
            if (inside[c[0]] != inside[c[1]] && inside[c[1]] != inside[c[2]] && inside[c[2]] != inside[c[3]] && inside[c[3]] != inside[c[0]])
                return true;
        return false;
    }

    /// Grows the refined set until it can be joined to the rest consistently, and samples it finer.
    ///
    /// A base cell that is not refined but shares a face, edge or corner with a refined one is an interface cell: it
    /// keeps its one base vertex, but the refined side meshes the faces they share on the fine lattice. Every refined
    /// and interface cell gets a block of fine samples. An interface cell stays unrefined only if, on the fine lattice,
    /// each of its base edges changes sign at most once and its fine samples agree with what one base vertex can
    /// represent: none change sign if its corners don't, and otherwise the surface crosses its six faces in exactly one
    /// loop. If not, it is refined and its neighbours become interface cells. This repeats until nothing changes.
    ///
    /// Each block also re-evaluates the base samples at its corners, which block skipping in step 1 may have filled
    /// without evaluating. If one changes sign, the field changed faster than its bound promised there: the true value
    /// is written back and the base cells around it are refined.
    ///
    /// False if the samples or the refined set go over budget.
    /// <paramref name="resident"/>: bytes already held elsewhere for this attempt (deep blocks, a kept mesh), charged too.
    static bool Close(Grid g, HashSet<int> refined, HashSet<int> near, Dictionary<int, BaseInfo> bases, ref int unsafeSamples, long resident = 0)
    {
        int side = g.PerBase + 1, per = g.PerBase;
        long blockBytes = (long)side * side * side * 4;
        // Interface verdicts depend only on a cell's own samples and face decisions, so they hold across calls.
        var checkedOk = g.InterfaceAccepted;
        while (true)
        {
            // Interface cells of the current set.
            near.Clear();
            foreach (int cell in refined)
            {
                var (bi, bj, bk) = g.BaseCoords(cell);
                for (int dk = -1; dk <= 1; dk++)
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            if (!g.HasBaseCell(bi + di, bj + dj, bk + dk)) continue;
                            int n = g.BaseCell(bi + di, bj + dj, bk + dk);
                            if (!refined.Contains(n)) near.Add(n);
                        }
            }
            // Sample every block not yet sampled.
            var missing = refined.Concat(near).Where(c => !g.Blocks.ContainsKey(c)).ToList();
            if ((g.Blocks.Count + (long)missing.Count) * blockBytes + (long)refined.Count * per * per * per * 64 + g.CentreMemory + g.FineCacheBytes + resident > MemoryBudget)
                return false;
            Parallel.ForEach(missing, cell =>
            {
                var (bi, bj, bk) = g.BaseCoords(cell);
                g.Blocks[cell] = g.Sample(bi, bj, bk);
            });

            bool grew = false;
            // Base samples that change sign when evaluated: write back, refine the cells around them.
            foreach (int cell in missing)
            {
                var (bi, bj, bk) = g.BaseCoords(cell);
                var block = g.Blocks[cell];
                for (int c = 0; c < 8; c++)
                {
                    int ci = bi + (c & 1), cj = bj + ((c >> 1) & 1), ck = bk + (c >> 2);
                    float truth = block[(((c >> 2) * per) * side + ((c >> 1) & 1) * per) * side + (c & 1) * per];
                    long at = g.BaseSample(ci, cj, ck);
                    if ((g.F[at] < 0) == (truth < 0)) { g.F[at] = truth; continue; }
                    g.F[at] = truth;
                    unsafeSamples++;
                    for (int n = 0; n < 8; n++)
                    {
                        int ni = ci - (n & 1), nj = cj - ((n >> 1) & 1), nk = ck - (n >> 2);
                        if (g.HasBaseCell(ni, nj, nk) && refined.Add(g.BaseCell(ni, nj, nk))) grew = true;
                    }
                }
            }
            if (!grew)
                foreach (int cell in near)
                {
                    if (checkedOk.Contains(cell)) continue;
                    if (InterfaceOk(g, cell)) checkedOk.Add(cell);
                    else { refined.Add(cell); grew = true; }
                }
            // Interface checks above may have added face centres since the estimate: charge them before going on.
            if (g.Blocks.Count * blockBytes + (long)refined.Count * per * per * per * 64 + g.CentreMemory + g.FineCacheBytes + resident > MemoryBudget)
                return false;
            if (!grew) return true;
            if ((long)refined.Count * per * per * per > 4 * FineCellBudget) return false;
        }
    }

    /// The samples of a cube <paramref name="side"/> samples wide (x fastest): at most one 6-connected set of inside
    /// samples and one of outside samples, each containing a corner of the cube with its sign. An interface cell must
    /// pass this to stay empty or one vertex: otherwise it holds sampled surface (an island or a void) its corners
    /// don't show. A conservative filter (sol #357-#362): it may promote a cell whose sets join only diagonally.
    static bool SignComponentsOk(float[] block, int side)
    {
        int n = side * side * side;
        var label = new int[n];
        var stack = new Stack<int>();
        int insideSets = 0, outsideSets = 0;
        int last = side - 1;
        for (int start = 0; start < n; start++)
        {
            if (label[start] != 0) continue;
            bool sign = block[start] < 0;
            bool hasCorner = false;
            label[start] = 1;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int at = stack.Pop();
                int x = at % side, y = at / side % side, z = at / (side * side);
                if ((x == 0 || x == last) && (y == 0 || y == last) && (z == 0 || z == last)) hasCorner = true;
                if (x > 0) Visit(at - 1);
                if (x < last) Visit(at + 1);
                if (y > 0) Visit(at - side);
                if (y < last) Visit(at + side);
                if (z > 0) Visit(at - side * side);
                if (z < last) Visit(at + side * side);
            }
            if (!hasCorner) return false;
            if (sign) insideSets++; else outsideSets++;
            if (insideSets > 1 || outsideSets > 1) return false;

            void Visit(int m)
            {
                if (label[m] != 0 || (block[m] < 0) != sign) return;
                label[m] = 1;
                stack.Push(m);
            }
        }
        return true;
    }

    /// Can interface cell <paramref name="cell"/> stay one base vertex next to refined cells (see Close)?
    static bool InterfaceOk(Grid g, int cell)
    {
        var (bi, bj, bk) = g.BaseCoords(cell);
        // Block-local indices are on the fine lattice (step = fine cells per base edge); positions passed on are finest.
        int step = g.PerBase, side = step + 1, i0 = bi * g.Step, j0 = bj * g.Step, k0 = bk * g.Step;
        var block = g.Blocks[cell];
        if (!SignComponentsOk(block, side)) return false;
        bool At(int i, int j, int k) => block[(k * side + j) * side + i] < 0;
        Span<bool> corner = stackalloc bool[8];
        int inside = 0;
        for (int c = 0; c < 8; c++)
        {
            corner[c] = At((c & 1) * step, ((c >> 1) & 1) * step, (c >> 2) * step);
            if (corner[c]) inside++;
        }
        // Each base edge changes sign at most once along its fine samples.
        foreach (var (a, b) in Edges)
        {
            int ax = a & 1, ay = (a >> 1) & 1, az = a >> 2, dx = (b & 1) - ax, dy = ((b >> 1) & 1) - ay, dz = (b >> 2) - az;
            int changes = 0;
            bool prev = corner[a];
            for (int t = 1; t <= step; t++)
            {
                bool now = At(ax * step + dx * t, ay * step + dy * t, az * step + dz * t);
                if (now != prev) changes++;
                prev = now;
            }
            if (changes > 1) return false;
        }
        if (inside == 0 || inside == 8)
        {
            // No vertex: nothing may change sign anywhere in the cell.
            foreach (float v in block)
                if ((v < 0) != corner[0]) return false;
            return true;
        }
        Span<int> sheet = stackalloc int[12];
        if (Sheets(corner, sheet, g, i0, j0, k0, g.Step) != 1) return false;
        return Octree.BoundaryLoops(g, i0, j0, k0, g.Step, g.StepB) == 1;
    }

    /// Steps 4 and on, for the base cells and refined set given: fine cells inside the refined set, coarse leaves
    /// elsewhere, the octree with merging, contouring, and the crossing repair. <paramref name="stuck"/> returns the
    /// coarse cells still in a crossing at their last placement; <paramref name="over"/> is set if over budget.
    /// <paramref name="last"/>: no restart follows, so coarse cells take their in-cell step too rather than wait to be
    /// refined.
    /// <paramref name="forbidden"/>: merged cells not to merge again. <paramref name="missCells"/> and
    /// <paramref name="missMerged"/> return the owners of output triangles that miss the surface (Result.ProbeFailures).
    /// <paramref name="deep"/>: fine cells given extra depth (made of deep cells); <paramref name="deepNear"/>: the
    /// interface cells around them. <paramref name="deepCandidates"/> returns fine cells outside the deep set that
    /// should join it (see Deepen); empty without deep levels.
    static Result Build(Grid g, Dictionary<int, BaseInfo> bases, HashSet<int> refined, HashSet<int> near, HashSet<long> forbidden, double tolerance, bool last,
        HashSet<long> deep, HashSet<long> deepNear,
        out HashSet<int> stuck, out HashSet<int> missCells, out HashSet<long> missMerged, out HashSet<long> deepCandidates, out bool over, long reserved = 0)
    {
        stuck = [];
        missCells = [];
        missMerged = [];
        deepCandidates = [];
        over = false;
        int k = g.K, step = g.Step, per = g.PerBase, sb = g.StepB;
        double h = g.Hb;
        long fineBlockBytes = (long)(per + 1) * (per + 1) * (per + 1) * 4;
        var vertices = new List<V3>();
        var quadrics = new List<Qef>();
        var index = new Dictionary<long, (int Base, int Packed, int Count)>();
        // Vertices placed by the sharp solve (see Place), with the stable position to fall back to.
        var fallback = new Dictionary<int, V3>();
        // Per vertex, its cell (minimum corner and side), for the in-cell point that is the last step of the crossing
        // repair; NaN for a merged vertex, which is unmerged instead.
        var cellMin = new List<V3>();
        var cellSide = new List<double>();
        var coarse = new Dictionary<long, int>();      // level-k node of an unrefined base cell -> its vertex
        var coarseCell = new Dictionary<int, int>();   // that vertex -> its base cell
        // Finest-cell vertices at their in-cell point (placed there, or demoted there by the repair).
        var atInCell = new HashSet<int>();
        // Per fine or deep vertex, the fine cell that owns it; and the vertices whose sheet holds opposing normals.
        var fineOwner = new Dictionary<int, long>();
        var opposingVertex = new HashSet<int>();
        var contradictoryVertex = new HashSet<int>();

        void Add(V3 point, Qef q, V3? stable, V3 min, double sideLength)
        {
            if (stable is { } st) fallback[vertices.Count] = st;
            vertices.Add(point);
            quadrics.Add(q);
            cellMin.Add(min);
            cellSide.Add(sideLength);
        }

        if (k == 0)
        {
            // No refinement: the base cells are the finest cells, exactly as before.
            foreach (var (cell, info) in bases.OrderBy(e => e.Key))
            {
                var (bi, bj, bk) = g.BaseCoords(cell);
                index[new Octree.Node(0, bi, bj, bk).Key] = (vertices.Count, info.Packed, info.Count);
                for (int c = 0; c < info.Count; c++) Add(info.Points[c], info.Qefs[c], info.Stable[c], g.P(bi, bj, bk), h);
            }
        }
        else
        {
            // 4. Fine cells inside the refined set.
            var cellsOf = new (List<(long Cell, int Packed, int Count)> Cells, List<(V3, Qef, V3?, V3, bool, double, long, bool, bool)> Points, int Bad)[refined.Count];
            long deepBlockBytes = (long)(sb + 1) * (sb + 1) * (sb + 1) * 4;
            var list = refined.OrderBy(c => c).ToArray();
            // The budget is checked on the cells actually made, as they are made: an oscillating field can make far
            // more than an estimate from the refined set's size.
            long blockBytes = g.Blocks.Count * fineBlockBytes;
            long made = 0;
            bool overBudget = false;
            Parallel.For(0, list.Length, (n, state) =>
            {
                var (bi, bj, bk) = g.BaseCoords(list[n]);
                // A base cell without deep cells gives the same fine cells every build: reuse them.
                bool anyDeep = false;
                if (deep.Count > 0)
                    for (int fk = bk * per; fk < (bk + 1) * per && !anyDeep; fk++)
                        for (int fj = bj * per; fj < (bj + 1) * per && !anyDeep; fj++)
                            for (int fi = bi * per; fi < (bi + 1) * per && !anyDeep; fi++)
                                anyDeep = deep.Contains(g.FineCell(fi, fj, fk));
                // A base cell that gained deep cells never uses its old record again: drop it.
                if (anyDeep && g.FineCellCache.TryRemove(list[n], out var stale)) Interlocked.Add(ref g.FineCacheBytes, -Grid.CacheBytes(stale));
                if (!anyDeep && g.FineCellCache.TryGetValue(list[n], out var cached))
                {
                    cellsOf[n] = cached;
                    long sum = Interlocked.Add(ref made, cached.Cells.Count);
                    if (sum > FineCellBudget || blockBytes + g.Deep.Count * deepBlockBytes + sum * 400 + g.CentreMemory + reserved > MemoryBudget)
                    {
                        overBudget = true;
                        state.Stop();
                    }
                    return;
                }
                var cells = new List<(long, int, int)>();
                var points = new List<(V3, Qef, V3?, V3, bool, double, long, bool, bool)>();
                int bad = 0;
                Span<bool> s = stackalloc bool[8];
                Span<int> sheet = stackalloc int[12];
                // Fine cells (fi, fj, fk) of this base cell, at finest position (fi, fj, fk) * sb, level E in the octree.
                for (int fk = bk * per; fk < (bk + 1) * per; fk++)
                    for (int fj = bj * per; fj < (bj + 1) * per; fj++)
                        for (int fi = bi * per; fi < (bi + 1) * per; fi++)
                        {
                            int i = fi * sb, j = fj * sb, kk = fk * sb;
                            long owner = g.FineCell(fi, fj, fk);
                            if (deep.Contains(owner))
                            {
                                // A fine cell with extra depth: its deep cells, at level 0.
                                for (int dk = kk; dk < kk + sb; dk++)
                                    for (int dj = j; dj < j + sb; dj++)
                                        for (int di = i; di < i + sb; di++)
                                        {
                                            int din = 0;
                                            for (int c = 0; c < 8; c++)
                                            {
                                                s[c] = g.In(di + (c & 1), dj + ((c >> 1) & 1), dk + (c >> 2));
                                                if (s[c]) din++;
                                            }
                                            if (din == 0 || din == 8) continue;
                                            int dcount = Sheets(s, sheet, g, di, dj, dk, 1), dpacked = 0;
                                            for (int e = 0; e < 12; e++)
                                                if (sheet[e] >= 0) dpacked |= sheet[e] << (2 * e);
                                            for (int c = 0; c < dcount; c++)
                                            {
                                                var dx = CellVertex(g, di, dj, dk, 1, g.H, s, sheet, c, out var dq, out var dstable, out _, out bool dfell, out bool dopp, out bool dcontra);
                                                if (dfell) bad++;
                                                points.Add((dx, dq, dstable, g.P(di, dj, dk), dfell, g.H, owner, dcontra, dopp));
                                            }
                                            cells.Add((new Octree.Node(0, di, dj, dk).Key, dpacked, dcount));
                                        }
                                continue;
                            }
                            int inside = 0;
                            for (int c = 0; c < 8; c++)
                            {
                                s[c] = g.In(i + (c & 1) * sb, j + ((c >> 1) & 1) * sb, kk + (c >> 2) * sb);
                                if (s[c]) inside++;
                            }
                            if (inside == 0 || inside == 8) continue;
                            int count = Sheets(s, sheet, g, i, j, kk, sb), packed = 0;
                            for (int e = 0; e < 12; e++)
                                if (sheet[e] >= 0) packed |= sheet[e] << (2 * e);
                            for (int c = 0; c < count; c++)
                            {
                                var x = CellVertex(g, i, j, kk, sb, g.HB, s, sheet, c, out var q, out var stable, out bool unres, out bool fellBack, out bool opp, out bool contra);
                                if (fellBack) bad++;
                                points.Add((x, q, stable, g.P(i, j, kk), fellBack, g.HB, owner, contra, opp));
                            }
                            cells.Add((new Octree.Node(g.E, fi, fj, fk).Key, packed, count));
                        }
                cellsOf[n] = (cells, points, bad);
                if (!anyDeep && g.FineCellCache.TryAdd(list[n], cellsOf[n])) Interlocked.Add(ref g.FineCacheBytes, Grid.CacheBytes(cellsOf[n]));
                long total = Interlocked.Add(ref made, cells.Count);
                if (total > FineCellBudget || blockBytes + g.Deep.Count * deepBlockBytes + total * 400 + g.CentreMemory + reserved > MemoryBudget)
                {
                    overBudget = true;
                    state.Stop();
                }
            });
            if (overBudget)
            {
                over = true;
                return new Result([], [], 0, 0, 0, h);
            }
            foreach (var (cells, points, bad) in cellsOf)
            {
                if (cells == null) continue;
                int at = vertices.Count;
                foreach (var (cell, packed, count) in cells)
                {
                    index[cell] = (at, packed, count);
                    at += count;
                }
                foreach (var (x, q, stable, min, fellBack, side, owner, contra, opp) in points)
                {
                    if (fellBack) atInCell.Add(vertices.Count);
                    fineOwner[vertices.Count] = owner;
                    if (contra) contradictoryVertex.Add(vertices.Count);
                    if (opp) opposingVertex.Add(vertices.Count);
                    Add(x, q, stable, min, side);
                }
            }
            // Coarse leaves: unrefined base cells with a sign change (one sheet, or they would be refined).
            foreach (var (cell, info) in bases.OrderBy(e => e.Key))
            {
                if (refined.Contains(cell)) continue;
                var (bi, bj, bk) = g.BaseCoords(cell);
                coarse[new Octree.Node(k + g.E, bi, bj, bk).Key] = vertices.Count;
                coarseCell[vertices.Count] = cell;
                Add(info.Points[0], info.Qefs[0], info.Stable[0], g.P(bi * step, bj * step, bk * step), h);
            }
        }

        Mark($"cells: {index.Count} finest, {coarse.Count} coarse, {vertices.Count} vertices");
        // 5. Polygons.
        var tris = new List<int>();
        var sources = new List<long>(); // per triangle: the finest grid edge its polygon was built around, or -1
        // Tunnel repairs whose new vertex stays at the mean of its four neighbours instead of being walked onto the
        // surface (the crossing repair's step for such a vertex), by the mesh edge they split; and per tunnel vertex
        // of the current contour, that edge.
        var flatTunnels = new HashSet<(int, int)>();
        var tunnelOf = new Dictionary<int, (int, int)>();
        var noMerge = new HashSet<long>();
        foreach (int cell in refined.Concat(near))
        {
            var (bi, bj, bk) = g.BaseCoords(cell);
            var node = new Octree.Node(k + g.E, bi, bj, bk);
            for (int l = k + g.E + 1; l <= 30 && node.L < 62; l++) { node = node.Parent; if (!noMerge.Add(node.Key)) break; }
        }
        // Above the fine level, nothing containing a deep cell or its interface merges: its boundary is on the deep lattice.
        foreach (long cell in deep.Concat(deepNear))
        {
            var (fi, fj, fk) = g.FineCoords(cell);
            var node = new Octree.Node(g.E, fi, fj, fk);
            for (int l = g.E + 1; l <= 30 && node.L < 62; l++) { node = node.Parent; if (!noMerge.Add(node.Key)) break; }
        }
        var octree = new Octree(g, index, coarse, noMerge, vertices, quadrics, fallback, cellMin, cellSide, tris, sources, atInCell)
        {
            Forbidden = forbidden,
            RefineCoarse = RefineStuckCoarse && g.K > 0 && !last,
        };
        octree.Build(tolerance);
        // Merging adds face centres (its sheet and loop tests); this is the last place the cache grows.
        if (k > 0 && g.Blocks.Count * fineBlockBytes + g.Deep.Count * (long)(sb + 1) * (sb + 1) * (sb + 1) * 4 + (long)index.Count * 400 + g.CentreMemory + reserved > MemoryBudget)
        {
            over = true;
            return new Result([], [], 0, 0, 0, h);
        }
        Mark("octree");
        void ContourAll()
        {
            tris.Clear();
            sources.Clear();
            octree.Contour();
            // A merged cell's one vertex can fold the triangles that join it to finer neighbours along a
            // crease, and so can a vertex the sharp solve moved. Undo those and contour again; a few rounds
            // settle it.
            for (int round = 0; round < 8 && octree.UndoFolded() > 0; round++)
            {
                tris.Clear();
                sources.Clear();
                octree.Contour();
            }
            RepairTunnels(g, vertices, tris, sources, flatTunnels, tunnelOf);
        }
        ContourAll();
        Mark($"contour: {tris.Count / 3} triangles");

        // 6. Crossings. Where the grid under-resolves the surface (wedges and skins thinner than a cell), vertices
        //    that each look fine can still make triangles pass through each other. Demote every vertex of a
        //    crossing pair one step (a merged cell is unmerged, a sharp placement goes back to its stable one, a
        //    stable one to its in-cell point) and contour again. Demotion only goes down that ladder, so it ends.
        //    After the first round only the triangles that changed are checked, against everything near them (two
        //    unchanged triangles were already checked); a clean incremental round is confirmed by a full check.
        var crossings = Crossings.All(vertices, tris, h);
        bool full = true;
        int demoted = 0;
        for (int round = 0; round < RepairRounds; round++)
        {
            if (crossings.Count == 0)
            {
                if (full) break;
                crossings = Crossings.All(vertices, tris, h);
                full = true;
                if (crossings.Count == 0) break;
            }
            var bad = new HashSet<int>();
            foreach (var (ta, tb) in crossings)
            {
                if (!GentleDemotion)
                {
                    for (int c = 0; c < 3; c++) { bad.Add(tris[3 * ta + c]); bad.Add(tris[3 * tb + c]); }
                    continue;
                }
                // Only the pair's least settled vertices take a step: a merge before a sharp placement before a
                // stable one, so a vertex already on its feature is not moved for a neighbour's sake.
                int top = 0;
                for (int c = 0; c < 3; c++) top = Math.Max(top, Math.Max(octree.Rank(tris[3 * ta + c]), octree.Rank(tris[3 * tb + c])));
                for (int c = 0; c < 3; c++)
                    foreach (int v in new[] { tris[3 * ta + c], tris[3 * tb + c] })
                        if (octree.Rank(v) == top) bad.Add(v);
                foreach (int v in new[] { tris[3 * ta], tris[3 * ta + 1], tris[3 * ta + 2], tris[3 * tb], tris[3 * tb + 1], tris[3 * tb + 2] })
                    if (tunnelOf.ContainsKey(v)) bad.Add(v);
            }
            var before = vertices.ToArray();
            var oldKeys = new HashSet<(int, int, int)>();
            for (int t = 0; t < tris.Count; t += 3) oldKeys.Add(Key(tris, t));
            int changed = octree.Demote(bad, out var bottom);
            foreach (int v in bad)
                if (tunnelOf.TryGetValue(v, out var edge) && flatTunnels.Add(edge)) changed++;
            foreach (int v in bottom)
                if (coarseCell.TryGetValue(v, out int cell)) stuck.Add(cell);
            if (changed == 0) break; // nothing left to demote: the result reports what remains
            demoted += changed;
            ContourAll();
            // Changed triangles: new ones, and those with a vertex that moved (by the demotion or by UndoFolded).
            var candidates = new List<int>();
            for (int t = 0; t < tris.Count; t += 3)
            {
                bool same = oldKeys.Contains(Key(tris, t));
                for (int c = 0; c < 3 && same; c++)
                {
                    int v = tris[t + c];
                    same = v < before.Length && before[v] == vertices[v];
                }
                if (!same) candidates.Add(t / 3);
            }
            crossings = Crossings.Involving(vertices, tris, h, candidates);
            full = false;
            Mark($"repair round {round}: {changed} demoted, {candidates.Count} changed triangles, {crossings.Count} pairs");
        }
        if (!full) crossings = Crossings.All(vertices, tris, h);
        Mark($"repair done: {crossings.Count} pairs");
        // Coarse cells in a remaining crossing at their last placement.
        if (crossings.Count > 0)
            foreach (var (ta, tb) in crossings)
                for (int c = 0; c < 3; c++)
                    foreach (int v in new[] { tris[3 * ta + c], tris[3 * tb + c] })
                        if (coarseCell.TryGetValue(v, out int cell) && octree.AtBottom(v)) stuck.Add(cell);

        // Probe output triangles that have a coarse or merged vertex (the refinement passes target these).
        int probeFailures = 0;
        if (k > 0)
        {
            var missC = new ConcurrentBag<int>();
            var missM = new ConcurrentBag<long>();
            int failures = 0;
            octree.MergedOwner(-1, out _); // build the owner index before the parallel loop
            Parallel.For(0, tris.Count / 3, t =>
            {
                int a = tris[3 * t], b = tris[3 * t + 1], c = tris[3 * t + 2];
                bool owned = false;
                foreach (int v in new[] { a, b, c })
                    owned |= coarseCell.ContainsKey(v) || octree.MergedOwner(v, out _);
                if (!owned) return;
                V3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
                double tol = 0.25 * g.HB, step = 1e-5 * g.Hb;
                bool miss = Distance(g, (pa + pb + pc) * (1.0 / 3), step) > tol || Distance(g, (pa + pb) * 0.5, step) > tol ||
                            Distance(g, (pb + pc) * 0.5, step) > tol || Distance(g, (pc + pa) * 0.5, step) > tol;
                if (!miss) return;
                Interlocked.Increment(ref failures);
                foreach (int v in new[] { a, b, c })
                {
                    if (coarseCell.TryGetValue(v, out int cell)) missC.Add(cell);
                    if (octree.MergedOwner(v, out long key)) missM.Add(key);
                }
            });
            probeFailures = failures;
            missCells.UnionWith(missC);
            missMerged.UnionWith(missM);
            Mark($"probes: {probeFailures} triangles miss; owners {missCells.Count} coarse, {missMerged.Count} merged");
        }

        // Deep candidates (fine cells to give extra depth), with deep levels available: a contradictory sheet whose
        // vertex is in the output; an output triangle of fine vertices only that misses the surface by more than 0.25 of
        // the Refine level's cell; a remaining crossing.
        int contradictory = 0;
        if (k > 0)
        {
            var usedV = new HashSet<int>(tris);
            foreach (int v in usedV)
                if (contradictoryVertex.Contains(v) || (atInCell.Contains(v) && opposingVertex.Contains(v))) contradictory++;
            if (g.E > 0)
            {
                foreach (int v in usedV)
                    if ((contradictoryVertex.Contains(v) || (atInCell.Contains(v) && opposingVertex.Contains(v))) && fineOwner.TryGetValue(v, out long owner))
                        deepCandidates.Add(owner);
                var missFine = new ConcurrentBag<long>();
                Parallel.For(0, tris.Count / 3, t =>
                {
                    int a = tris[3 * t], b = tris[3 * t + 1], c = tris[3 * t + 2];
                    if (!fineOwner.ContainsKey(a) || !fineOwner.ContainsKey(b) || !fineOwner.ContainsKey(c)) return;
                    V3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
                    double tol = 0.25 * g.HB, step = 1e-5 * g.HB;
                    if (Distance(g, (pa + pb + pc) * (1.0 / 3), step) > tol || Distance(g, (pa + pb) * 0.5, step) > tol ||
                        Distance(g, (pb + pc) * 0.5, step) > tol || Distance(g, (pc + pa) * 0.5, step) > tol)
                        foreach (int v in new[] { a, b, c }) missFine.Add(fineOwner[v]);
                });
                deepCandidates.UnionWith(missFine);
                foreach (var (ta, tb) in crossings)
                    for (int c = 0; c < 3; c++)
                        foreach (int v in new[] { tris[3 * ta + c], tris[3 * tb + c] })
                            if (fineOwner.TryGetValue(v, out long owner)) deepCandidates.Add(owner);
                deepCandidates.ExceptWith(deep);
                Mark($"deep candidates: {deepCandidates.Count}");
            }
        }

        // Finest cells in the output that ended at their in-cell point.
        int unresolved = k == 0 ? 0 : tris.Distinct().Count(v => atInCell.Contains(v) && v < cellSide.Count && cellSide[v] <= g.HB);

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
        // The smallest cell of the support grid actually built (not the accuracy target).
        double minCell = deep.Count > 0 ? g.H : g.Blocks.Count > 0 ? g.HB : h;
        return new Result(used, tris, g.Nbx, g.Nby, g.Nbz, h, crossings.Count, demoted, minCell, k, refined.Count, unresolved,
            Fragments: CountFragments(used, tris, 4 * g.HB), ProbeFailures: probeFailures,
            AccuracyCellSize: k == 0 ? h : g.HB, ExtraDepth: deep.Count > 0 ? g.E : 0, DeepCells: deep.Count, Contradictory: contradictory);
    }

    /// Connected pieces of the mesh whose bounding box diagonal is at most <paramref name="size"/>.
    static int CountFragments(List<V3> vertices, List<int> tris, double size)
    {
        var parent = new int[vertices.Count];
        for (int v = 0; v < parent.Length; v++) parent[v] = v;
        int Root(int v)
        {
            while (parent[v] != v) v = parent[v] = parent[parent[v]];
            return v;
        }
        for (int t = 0; t < tris.Count; t += 3)
        {
            int a = Root(tris[t]), b = Root(tris[t + 1]), c = Root(tris[t + 2]);
            parent[b] = a;
            parent[c] = a;
        }
        var boxes = new Dictionary<int, (V3 Lo, V3 Hi)>();
        for (int v = 0; v < vertices.Count; v++)
        {
            int r = Root(v);
            var p = vertices[v];
            boxes[r] = boxes.TryGetValue(r, out var b)
                ? (new V3(Math.Min(b.Lo.X, p.X), Math.Min(b.Lo.Y, p.Y), Math.Min(b.Lo.Z, p.Z)), new V3(Math.Max(b.Hi.X, p.X), Math.Max(b.Hi.Y, p.Y), Math.Max(b.Hi.Z, p.Z)))
                : (p, p);
        }
        return boxes.Values.Count(b => (b.Hi - b.Lo).Length <= size);
    }

    /// A triangle's vertices in ascending order, to recognise it after contouring again.
    static (int, int, int) Key(List<int> tris, int t)
    {
        int a = tris[t], b = tris[t + 1], c = tris[t + 2];
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// Octree over the grid's cells. A node at level L covers 2^L cells per side; level 0 nodes are the
    /// grid cells themselves. Only merged cells are stored: everything else is derived from the grid.
    sealed class Octree(Grid g, Dictionary<long, (int Base, int Packed, int Count)> cells, Dictionary<long, int> coarse, HashSet<long> noMerge,
        List<V3> vertices, List<Qef> quadrics, Dictionary<int, V3> fallback, List<V3> cellMin, List<double> cellSide, List<int> tris, List<long> sources,
        HashSet<int> atInCell)
    {
        public readonly record struct Node(int L, int I, int J, int K)
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
            // Cells (deep at level 0, fine at level E) are leaves at their own level.
            if (cells.ContainsKey(n.Key)) return Kind.Leaf;
            if (n.L == 0) return Kind.Empty;
            if (merged.ContainsKey(n.Key) || coarse.ContainsKey(n.Key)) return Kind.Leaf;
            return occupied.Contains(n.Key) ? Kind.Internal : Kind.Empty;
        }

        static Node Decode(long key) => new((int)(key >> 60), (int)(key >> 40) & 0xFFFFF, (int)(key >> 20) & 0xFFFFF, (int)key & 0xFFFFF);

        /// A coarse cell's last step is to be refined (the caller builds again), not its in-cell point.
        public bool RefineCoarse;

        /// Cells never merged (they missed the surface in an earlier build).
        public HashSet<long> Forbidden = [];

        /// The merged cell whose vertex is v, if any. Valid once merging and repair are done.
        public bool MergedOwner(int v, out long key)
        {
            owners ??= merged.ToDictionary(e => e.Value, e => e.Key);
            return owners.TryGetValue(v, out key);
        }
        Dictionary<int, long>? owners;

        /// The vertex of a leaf node above level 0: a merged cell or a coarse (unrefined base) cell.
        int VertexOf(Node n) => merged.TryGetValue(n.Key, out int v) ? v : coarse[n.Key];

        /// Records which nodes hold surface, and merges cells bottom-up while one vertex fits within
        /// <paramref name="tolerance"/> (model units; 0 = no merging).
        public void Build(double tolerance)
        {
            var level = new HashSet<Node>();
            // Leaves above level 0 (fine cells at level E when there is a deeper level, coarse cells at the base level)
            // join the walk at their own level, in the order they were made.
            var leavesAt = new Dictionary<int, List<Node>>();
            foreach (var key in cells.Keys)
            {
                var n = Decode(key);
                if (n.L == 0) level.Add(n.Parent);
                else { if (!leavesAt.TryGetValue(n.L, out var at)) leavesAt[n.L] = at = []; at.Add(n); }
            }
            foreach (var key in coarse.Keys)
            {
                var n = Decode(key);
                if (!leavesAt.TryGetValue(n.L, out var at)) leavesAt[n.L] = at = [];
                at.Add(n);
            }
            for (int l = 1; l <= levels; l++)
            {
                if (leavesAt.TryGetValue(l, out var joining)) level.UnionWith(joining);
                var parents = new HashSet<Node>();
                var nodes = level.ToArray();
                foreach (var n in nodes)
                {
                    occupied.Add(n.Key);
                    if (l < levels) parents.Add(n.Parent);
                }
                // A merge reads only the level below, so this level's merges can be tried in parallel; they are
                // recorded in the same order as a serial pass would.
                var tries = new (bool Ok, V3 X, Qef Q, V3? Stable)[nodes.Length];
                if (tolerance > 0)
                    Parallel.For(0, nodes.Length, i =>
                    {
                        if (coarse.ContainsKey(nodes[i].Key) || cells.ContainsKey(nodes[i].Key)) return; // already a leaf
                        Span<bool> corner = stackalloc bool[8];
                        Span<int> sheet = stackalloc int[12];
                        bool ok = TryMerge(nodes[i], tolerance, corner, sheet, out var x, out var q, out var stable);
                        tries[i] = (ok, x, q, stable);
                    });
                for (int i = 0; i < nodes.Length; i++)
                {
                    var (ok, x, q, stable) = tries[i];
                    if (!ok) continue;
                    merged[nodes[i].Key] = vertices.Count;
                    if (stable is { } s) fallback[vertices.Count] = s;
                    vertices.Add(x);
                    quadrics.Add(q);
                    cellMin.Add(new V3(double.NaN, double.NaN, double.NaN)); // a merged vertex in a crossing is unmerged, not moved
                    cellSide.Add(double.NaN);
                }
                level = parents;
            }
        }

        bool TryMerge(Node n, double tolerance, Span<bool> corner, Span<int> sheet, out V3 x, out Qef q, out V3? stable)
        {
            x = default;
            q = default;
            stable = null;
            // Above the base level, only cells away from refinement merge: their boundary is all on the base lattice.
            // (noMerge holds only nodes above the level they protect, so it applies at any level.)
            if (noMerge.Contains(n.Key) || Forbidden.Contains(n.Key)) return false;
            // Every child must be empty or a single-vertex leaf.
            for (int c = 0; c < 8; c++)
            {
                var child = n.Child(c & 1, (c >> 1) & 1, c >> 2);
                if (cells.TryGetValue(child.Key, out var e))
                {
                    if (e.Count != 1) return false;
                    q.Add(quadrics[e.Base]);
                }
                else if (child.L == 0) continue;
                else if (merged.TryGetValue(child.Key, out int v)) q.Add(quadrics[v]);
                else if (coarse.TryGetValue(child.Key, out int cv)) q.Add(quadrics[cv]);
                else if (occupied.Contains(child.Key)) return false;
            }

            // Topology safety (Ju et al. 2002, section 4.1): the merged cell must hold a single sheet, and
            // the samples at the middle of each of its edges, faces and its centre must agree with one of
            // the corners of that edge, face or cell, so no sign feature is lost between the corners.
            int size = 1 << n.L, half = size >> 1, i0 = n.I * size, j0 = n.J * size, k0 = n.K * size;
            for (int c = 0; c < 8; c++) corner[c] = g.In(i0 + (c & 1) * size, j0 + ((c >> 1) & 1) * size, k0 + (c >> 2) * size);
            if (Sheets(corner, sheet, g, i0, j0, k0, size) != 1) return false;
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
            // The lattice the cell's boundary is sampled on, and the cell size its thresholds are in: base above the
            // base level, fine down to level E, deep below.
            bool above = n.L > g.K + g.E, fine = n.L > g.E;
            if (BoundaryLoops(g, i0, j0, k0, size, above ? g.Step : fine ? g.StepB : 1) != 1) return false;

            var lo = new V3(i0 * g.H, j0 * g.H, k0 * g.H);
            x = Place(g, q, lo, size * g.H, above ? g.Hb : fine ? g.HB : g.H, out stable, out _);
            if (q.Rms(x) > tolerance) return false;
            x += g.Origin;
            if (stable is { } s) stable = s + g.Origin;
            return true;
        }

        /// Number of closed loops in which the surface crosses the boundary of the cube of <paramref name="size"/>
        /// finest cells at sample (i0, j0, k0), traced over squares of <paramref name="step"/> finest cells.
        public static int BoundaryLoops(Grid g, int i0, int j0, int k0, int size, int step)
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
                    for (int u = 0; u < size; u += step)
                        for (int v = 0; v < size; v += step)
                        {
                            // Corners in cyclic order (u,v), (u+1,v), (u+1,v+1), (u,v+1); edge n joins corner n to n+1.
                            for (int n = 0; n < 4; n++)
                            {
                                at[0] = i0; at[1] = j0; at[2] = k0;
                                at[axis] += side; at[p] += u + (n is 1 or 2 ? step : 0); at[q] += v + (n >= 2 ? step : 0);
                                sign[n] = g.In(at[0], at[1], at[2]);
                                // An edge is named by its lower sample and its direction.
                                if (n == 2) at[p] -= step;
                                if (n == 3) at[q] -= step;
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
                            {
                                at[0] = i0; at[1] = j0; at[2] = k0;
                                at[axis] += side; at[p] += u; at[q] += v;
                                bool around = !FaceCentreInside(g, axis, at[0], at[1], at[2], step);
                                for (int n = 0; n < 4; n++)
                                    if (sign[n] == around) parent[Find(edge[n])] = Find(edge[(n + 3) % 4]);
                            }
                        }
            }
            int loops = 0;
            foreach (var (e, up) in parent) if (e == up) loops++;
            return loops;
        }

        /// For every folded triangle (normal against the field's gradient): undoes the merges whose
        /// vertices it uses, and moves its sharp-placed vertices back to their stable positions.
        /// Returns how many changes were made; the caller contours again.
        public int UndoFolded()
        {
            if (merged.Count == 0 && fallback.Count == 0) return 0;
            var owner = new Dictionary<int, long>();
            foreach (var (key, vertex) in merged) owner[vertex] = key;
            var unmerge = new HashSet<long>();
            var restore = new HashSet<int>();
            var folded = new bool[tris.Count / 3];
            Parallel.For(0, folded.Length, n =>
            {
                int a = tris[3 * n], b = tris[3 * n + 1], c = tris[3 * n + 2];
                bool suspect = owner.ContainsKey(a) || fallback.ContainsKey(a) || owner.ContainsKey(b) || fallback.ContainsKey(b) ||
                               owner.ContainsKey(c) || fallback.ContainsKey(c);
                folded[n] = suspect && Facing(a, b, c) < 0;
            });
            for (int t = 0; t < folded.Length; t++)
            {
                if (!folded[t]) continue;
                for (int i = 0; i < 3; i++)
                {
                    int v = tris[3 * t + i];
                    if (owner.TryGetValue(v, out long key)) unmerge.Add(key);
                    if (fallback.ContainsKey(v)) restore.Add(v);
                }
            }
            foreach (long key in unmerge) merged.Remove(key);
            if (unmerge.Count > 0) owners = null;
            foreach (int v in restore)
            {
                Move(v, fallback[v]);
                fallback.Remove(v);
            }
            return unmerge.Count + restore.Count;
        }

        /// One step down for each of <paramref name="verts"/>: a merged cell is unmerged, a sharp placement goes back to
        /// its stable one, a stable one to its in-cell point. A coarse (unrefined base) cell is never unmerged, as it
        /// has no finer cells: its sharp placement is undone in place, and after that it is returned for refinement. Vertices already at the bottom are returned in
        /// <paramref name="bottom"/>; vertices added by RepairTunnels are left alone. Returns how many changed.
        public int Demote(HashSet<int> verts, out List<int> bottom)
        {
            bottom = [];
            var owner = new Dictionary<int, long>();
            foreach (var (key, vertex) in merged) owner[vertex] = key;
            int changed = 0;
            foreach (int v in verts)
            {
                if (v >= cellMin.Count) continue;
                if (owner.TryGetValue(v, out long key)) { merged.Remove(key); owners = null; }
                else if (fallback.Remove(v, out var stable)) Move(v, stable);
                // With refinement, a coarse cell's last step is to be refined, not to lose its feature to the in-cell
                // point: report it (the caller refines it and builds again).
                else if (RefineCoarse && cellSide[v] == g.Hb) { bottom.Add(v); continue; }
                else if (!AtBottom(v)) { Move(v, InCellOf(v)); atInCell.Add(v); }
                else { bottom.Add(v); continue; }
                changed++;
            }
            return changed;
        }

        /// How far vertex v is from the bottom of its ladder: 3 merged, 2 sharp placement, 1 stable, 0 at the bottom.
        public int Rank(int v)
        {
            if (v >= cellMin.Count) return 1;
            if (MergedOwner(v, out _)) return 3;
            if (fallback.ContainsKey(v)) return 2;
            return AtBottom(v) ? 0 : 1;
        }

        V3 InCellOf(int v) => InCell(g, quadrics[v], cellMin[v] - g.Origin, cellSide[v], cellSide[v]) + g.Origin;

        /// Is vertex v at the last step of its ladder (its in-cell point, with no merge or sharp placement to undo)?
        public bool AtBottom(int v) =>
            v >= cellMin.Count || (!fallback.ContainsKey(v) && (double.IsNaN(cellSide[v]) || (RefineCoarse && cellSide[v] == g.Hb) || InCellOf(v) == vertices[v]));

        // Polygons found by the octree walk, in order: (a, b, c, e, source) with e = -1 for a triangle. Choosing a
        // quad's diagonal needs field samples, so that is done for all quads in parallel afterwards.
        readonly List<(int A, int B, int C, int E, long Source)> polygons = [];

        public void Contour()
        {
            polygons.Clear();
            CellProc(new Node(levels, 0, 0, 0), polygons);
            var viaAc = new bool[polygons.Count];
            Parallel.For(0, polygons.Count, n =>
            {
                var (a, b, c, e, _) = polygons[n];
                if (e >= 0) viaAc[n] = SplitAc(a, b, c, e);
            });
            for (int n = 0; n < polygons.Count; n++)
            {
                var (a, b, c, e, source) = polygons[n];
                if (e < 0) { tris.Add(a); tris.Add(b); tris.Add(c); sources.Add(source); continue; }
                if (viaAc[n]) { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(e); }
                else { tris.Add(a); tris.Add(b); tris.Add(e); tris.Add(b); tris.Add(c); tris.Add(e); }
                sources.Add(source);
                sources.Add(source);
            }
        }

        /// Split quad a, b, c, e along a-c (else b-e)? Along the diagonal that lies closer to the surface, so a
        /// crease running through the quad becomes a triangle edge rather than being cut across; but never a split
        /// that folds a triangle over (its normal against the field's gradient) when the other split does not.
        bool SplitAc(int a, int b, int c, int e)
        {
            double ac = Midpoint(a, c), be = Midpoint(b, e);
            double viaAc = Math.Min(Facing(a, b, c), Facing(a, c, e)), viaBe = Math.Min(Facing(a, b, e), Facing(b, c, e));
            if (viaAc < 0 && viaBe > viaAc + 0.2) ac = double.MaxValue;
            else if (viaBe < 0 && viaAc > viaBe + 0.2) be = double.MaxValue;
            return ac <= be;
        }


        // Field samples taken while contouring, kept across the repair rounds, which contour the same mesh again
        // with few vertices changed. An entry is valid while none of its vertices has moved since (see Move).
        readonly Dictionary<int, int> moves = [];
        readonly ConcurrentDictionary<(int, int, int), (int, int, int, V3)> gradients = new();
        readonly ConcurrentDictionary<(int, int), (int, int, double)> midpoints = new();

        int Stamp(int v) => moves.GetValueOrDefault(v);

        void Move(int v, V3 to)
        {
            vertices[v] = to;
            moves[v] = Stamp(v) + 1;
        }

        /// The field's gradient at the centroid of triangle a, b, c (in any order).
        V3 CentroidGradient(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            int sa = Stamp(a), sb = Stamp(b), sc = Stamp(c);
            if (gradients.TryGetValue((a, b, c), out var e) && e.Item1 == sa && e.Item2 == sb && e.Item3 == sc) return e.Item4;
            var grad = g.Field.Gradient((vertices[a] + vertices[b] + vertices[c]) * (1.0 / 3), 1e-5 * g.H);
            gradients[(a, b, c)] = (sa, sb, sc, grad);
            return grad;
        }

        /// |field| at the midpoint of a and b.
        double Midpoint(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            int sa = Stamp(a), sb = Stamp(b);
            if (midpoints.TryGetValue((a, b), out var e) && e.Item1 == sa && e.Item2 == sb) return e.Item3;
            double f = Math.Abs(g.Field.Value((vertices[a] + vertices[b]) * 0.5));
            midpoints[(a, b)] = (sa, sb, f);
            return f;
        }

        // Axis d = 0, 1, 2; the other two in cyclic order, so that d = p × q.
        public static int P(int d) => (d + 1) % 3;
        public static int Q(int d) => (d + 2) % 3;

        static Node ChildAt(Node n, int a0, int b0, int a1, int b1, int a2, int b2)
        {
            Span<int> bit = stackalloc int[3];
            bit[a0] = b0; bit[a1] = b1; bit[a2] = b2;
            return n.Child(bit[0], bit[1], bit[2]);
        }

        void CellProc(Node n, List<(int A, int B, int C, int E, long Source)> output)
        {
            if (KindOf(n) != Kind.Internal) return;
            if (n.L >= levels - 2)
            {
                // Near the root: walk the eight subtrees in parallel, each into its own list, and keep their order.
                var parts = new List<(int, int, int, int, long)>[8];
                Parallel.For(0, 8, c =>
                {
                    parts[c] = [];
                    CellProc(n.Child(c & 1, (c >> 1) & 1, c >> 2), parts[c]);
                });
                foreach (var part in parts) output.AddRange(part);
            }
            else
                for (int c = 0; c < 8; c++) CellProc(n.Child(c & 1, (c >> 1) & 1, c >> 2), output);
            int half = 1 << (n.L - 1), i0 = n.I << n.L, j0 = n.J << n.L, k0 = n.K << n.L;
            Span<Node> around = stackalloc Node[4];
            Span<int> start = stackalloc int[3];
            for (int d = 0; d < 3; d++)
            {
                int p = P(d), q = Q(d);
                // The four faces between children across axis d.
                for (int u = 0; u < 2; u++)
                    for (int v = 0; v < 2; v++)
                        FaceProc(d, ChildAt(n, d, 0, p, u, q, v), ChildAt(n, d, 1, p, u, q, v), output);
                // The two edges along d through the centre.
                for (int s = 0; s < 2; s++)
                {
                    around[0] = ChildAt(n, d, s, p, 0, q, 0);
                    around[1] = ChildAt(n, d, s, p, 1, q, 0);
                    around[2] = ChildAt(n, d, s, p, 1, q, 1);
                    around[3] = ChildAt(n, d, s, p, 0, q, 1);
                    start[0] = i0; start[1] = j0; start[2] = k0;
                    start[p] += half; start[q] += half; start[d] += s * half;
                    EdgeProc(d, around, start[0], start[1], start[2], half, output);
                }
            }
        }

        /// <paramref name="a"/> and <paramref name="b"/> share a face across <paramref name="axis"/>, a on the low side.
        void FaceProc(int axis, Node a, Node b, List<(int A, int B, int C, int E, long Source)> output)
        {
            Kind ka = KindOf(a), kb = KindOf(b);
            if (ka == Kind.Empty || kb == Kind.Empty || (ka == Kind.Leaf && kb == Kind.Leaf)) return;
            int p = P(axis), q = Q(axis);
            for (int u = 0; u < 2; u++)
                for (int v = 0; v < 2; v++)
                    FaceProc(axis,
                        ka == Kind.Internal ? ChildAt(a, axis, 1, p, u, q, v) : a,
                        kb == Kind.Internal ? ChildAt(b, axis, 0, p, u, q, v) : b, output);

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
                    EdgeProc(d, around, start[0], start[1], start[2], half, output);
                }
            }
        }

        /// Cosine between a triangle's normal and the field's gradient at its centroid; 1 for a
        /// degenerate triangle, which cannot fold.
        double Facing(int a, int b, int c)
        {
            V3 pa = vertices[a], pb = vertices[b], pc = vertices[c];
            var n = V3.Cross(pb - pa, pc - pa);
            double area = n.Length;
            if (area < 1e-12 * g.H * g.H) return 1;
            var grad = CentroidGradient(a, b, c);
            double len = grad.Length;
            return len > 0 ? V3.Dot(n, grad) / (area * len) : 1;
        }

        // Which of a finest cell's 12 edges is the shared one, per quadrant around an edge along d.
        public static readonly int[][] EdgeInQuadrant = [[3, 2, 0, 1], [7, 5, 4, 6], [11, 10, 8, 9]];

        /// The four nodes around an edge segment along <paramref name="d"/>, counter-clockwise about +d.
        /// The segment starts at sample (i, j, k) and is <paramref name="length"/> cells long.
        void EdgeProc(int d, ReadOnlySpan<Node> around, int i, int j, int k, int length, List<(int A, int B, int C, int E, long Source)> output)
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
                    EdgeProc(d, next, start[0], start[1], start[2], half, output);
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
                if (cells.TryGetValue(n.Key, out var ce))
                {
                    // A cell's sheet for this edge; a cell larger than the edge must have one sheet (closure ensures it).
                    if (ce.Count > 1 && length != 1 << n.L)
                        throw new InvalidOperationException("DualContour: a multi-sheet cell borders a finer edge.");
                    v = ce.Base + ((ce.Packed >> (2 * EdgeInQuadrant[d][quadrant])) & 3);
                }
                else v = VertexOf(n);
                if (count > 0 && (poly[count - 1] == v || poly[0] == v)) continue; // a larger cell spans two quadrants
                poly[count++] = v;
            }
            if (count < 3) return;
            // Wind so the normal points from inside to outside.
            if (!inside) poly[..count].Reverse();
            // A leaf-level edge (deep or fine) is remembered for RepairTunnels, with its level in the low 3 bits.
            long source = length == 1 || length == g.StepB
                ? ((((long)k * g.Sy + j) * g.Sx + i) * 3 + d) * 8 + (length == 1 ? 0 : g.E)
                : -1;
            output.Add((poly[0], poly[1], poly[2], count == 4 ? poly[3] : -1, source));
        }
    }

    /// Fixes mesh edges shared by four triangles that come from the four sides of one grid square
    /// with alternating corner signs (see the class remarks). The sides pair up at the square's inside
    /// corners into two strips, and both strips run between the same two vertices u and w. The two
    /// triangles of one strip are split at a new vertex placed on the surface between them, so that
    /// strip uses u-m and m-w while the other keeps u-w.
    /// <paramref name="flat"/>: tunnels (by the edge they split) whose new vertex is not walked onto the surface;
    /// <paramref name="added"/> returns each new vertex with its edge.
    static void RepairTunnels(Grid g, List<V3> vertices, List<int> tris, List<long> sources, HashSet<(int, int)> flat, Dictionary<int, (int, int)> added)
    {
        added.Clear();
        // Mesh edges shared by exactly four triangles, in the order each edge first appears.
        int nt = sources.Count;
        var entries = new (long Key, int Pos)[3 * nt];
        Parallel.For(0, nt, t =>
        {
            for (int c = 0; c < 3; c++)
            {
                int a = tris[3 * t + c], b = tris[3 * t + (c + 1) % 3];
                entries[3 * t + c] = (a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a, 3 * t + c);
            }
        });
        Array.Sort(entries);
        var fours = new List<(int First, int U, int W, int[] Around)>();
        for (int run = 0; run < entries.Length;)
        {
            int end = run + 1;
            while (end < entries.Length && entries[end].Key == entries[run].Key) end++;
            if (end - run == 4)
            {
                long key = entries[run].Key;
                fours.Add((entries[run].Pos, (int)(key >> 32), (int)(uint)key,
                    [entries[run].Pos / 3, entries[run + 1].Pos / 3, entries[run + 2].Pos / 3, entries[run + 3].Pos / 3]));
            }
            run = end;
        }
        fours.Sort((x, y) => x.First.CompareTo(y.First));

        var at = new int[3];
        var lo = new int[3];
        foreach (var (_, u, w, around) in fours)
        {
            // The four source edges must be the sides of one grid square: two along each of two axes.
            int axisA = -1, axisB = -1, lv = -1;
            bool ok = true;
            lo[0] = lo[1] = lo[2] = int.MaxValue;
            foreach (int t in around)
            {
                long e = sources[t];
                if (e < 0) { ok = false; break; }
                int el = (int)(e & 7);
                if (lv < 0) lv = el; else if (el != lv) ok = false;
                e >>= 3;
                int axis = (int)(e % 3);
                if (axisA < 0 || axis == axisA) axisA = axis;
                else if (axisB < 0 || axis == axisB) axisB = axis;
                else ok = false;
                Decode(g, e / 3, at);
                for (int n = 0; n < 3; n++) lo[n] = Math.Min(lo[n], at[n]);
            }
            if (!ok || axisB < 0) continue;
            int len = 1 << lv; // the square's side, in finest cells
            int normal = 3 - axisA - axisB;

            // Triangle built around the side along `axis` at offset `far` (0 or 1) across the other axis.
            int Side(int axis, int far)
            {
                int other = axis == axisA ? axisB : axisA;
                foreach (int t in around)
                {
                    if (sources[t] < 0 || (sources[t] >> 3) % 3 != axis) continue;
                    Decode(g, (sources[t] >> 3) / 3, at);
                    if (at[other] == lo[other] + far * len && at[axis] == lo[axis] && at[normal] == lo[normal]) return t;
                }
                return -1;
            }
            bool Inside(int da, int db)
            {
                lo.CopyTo(at, 0);
                at[axisA] += da * len; at[axisB] += db * len;
                return g.In(at[0], at[1], at[2]);
            }
            bool c00 = Inside(0, 0), c10 = Inside(1, 0), c11 = Inside(1, 1), c01 = Inside(0, 1);
            if (c00 == c10 || c10 == c11 || c11 == c01) continue; // not an alternating square
            // The strip at a pairing corner (cornerA, 0): the side along A at B-offset 0 and the side along B at
            // A-offset cornerA. The square pairs around its inside corners, or its outside ones if its centre is inside.
            bool centreIn = FaceCentreInside(g, normal, lo[0], lo[1], lo[2], len);
            int cornerA = c00 != centreIn ? 0 : 1;
            int t1 = Side(axisA, 0), t2 = Side(axisB, cornerA);
            if (t1 < 0 || t2 < 0 || t1 == t2) continue;

            int Apex(int t)
            {
                for (int c = 0; c < 3; c++) { int v = tris[3 * t + c]; if (v != u && v != w) return v; }
                return -1;
            }
            int x = Apex(t1), y = Apex(t2);
            if (x < 0 || y < 0) continue;

            // Place the new vertex among the strip's four vertices, then walk it onto the surface.
            var m = (vertices[u] + vertices[w] + vertices[x] + vertices[y]) * 0.25;
            var start = m;
            for (int it = 0; it < 4 && !flat.Contains((u, w)); it++)
            {
                var grad = g.Field.Gradient(m, 1e-5 * g.H * len);
                double g2 = V3.Dot(grad, grad);
                if (g2 < 1e-12) break;
                m -= grad * (g.Field.Value(m) / g2);
            }
            if ((m - start).Length > g.H * len) m = start;
            int mi = vertices.Count;
            vertices.Add(m);
            added[mi] = (u, w);

            foreach (int t in new[] { t1, t2 })
            {
                // (a, b, apex), with a-b the shared edge in this triangle's winding, becomes (a, m, apex) and (m, b, apex).
                int c = 0;
                while (tris[3 * t + c] == u || tris[3 * t + c] == w) c++;
                int apex = tris[3 * t + c], a = tris[3 * t + (c + 1) % 3], b = tris[3 * t + (c + 2) % 3];
                tris[3 * t] = a; tris[3 * t + 1] = mi; tris[3 * t + 2] = apex;
                tris.Add(mi); tris.Add(b); tris.Add(apex);
                sources.Add(-1);
                sources[t] = -1;
            }
        }
    }

    /// Sample coordinates of a linear sample index.
    static void Decode(Grid g, long sample, int[] at)
    {
        at[0] = (int)(sample % g.Sx);
        at[1] = (int)(sample / g.Sx % g.Sy);
        at[2] = (int)(sample / ((long)g.Sx * g.Sy));
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

    /// Is the centre of the square face normal to <paramref name="axis"/> with minimum corner (i, j, k) and side
    /// <paramref name="size"/> (finest cells) inside? A face crossed four times is ambiguous from its corners: inside,
    /// the inside corners are joined across the face, so its crossings pair around the outside corners; outside (or
    /// without FaceCentres), around the inside corners. Every consumer asks this for the same face the same way.
    static bool FaceCentreInside(Grid g, int axis, int i, int j, int k, int size)
    {
        if (!FaceCentres) return false;
        int x2 = 2 * i + (axis != 0 ? size : 0), y2 = 2 * j + (axis != 1 ? size : 0), z2 = 2 * k + (axis != 2 ? size : 0);
        return g.InsideDoubled(x2, y2, z2);
    }

    /// Groups the cell's sign-changing edges into surface sheets: two crossings on the same face belong
    /// to the same sheet. On a face with four crossings the crossings pair around the inside corners, or around
    /// the outside corners if the face centre is inside (FaceCentreInside); the decision belongs to the face, so
    /// the neighbouring cell agrees. The cell is at finest sample (i, j, k) and <paramref name="size"/> wide.
    /// Writes a sheet number (or -1) per edge and returns the sheet count (at most 4).
    static int Sheets(ReadOnlySpan<bool> inside, Span<int> sheet, Grid g, int ci, int cj, int ck, int size)
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
            {
                int axis = f / 2, far = f % 2;
                bool around = !FaceCentreInside(g, axis, ci + (axis == 0 ? far * size : 0), cj + (axis == 1 ? far * size : 0),
                    ck + (axis == 2 ? far * size : 0), size);
                for (int i = 0; i < 4; i++)
                    if (inside[c[i]] == around) parent[Find(parent, fe[i])] = Find(parent, fe[(i + 3) % 4]);
            }
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

    /// The vertex for sheet number <paramref name="which"/> of the cell at finest sample (i, j, k) that is
    /// <paramref name="step"/> finest cells wide, and the quadric it minimises. <paramref name="unresolved"/>: the cell
    /// does not resolve the surface (placement off the surface, a fit residual over 0.05 cells, or two normals more
    /// than 90° apart); <paramref name="fellBack"/>: the placement went to the in-cell point.
    static V3 CellVertex(Grid g, int i, int j, int k, int step, double unit, ReadOnlySpan<bool> inside, ReadOnlySpan<int> sheet, int which,
        out Qef q, out V3? stable, out bool unresolved, out bool fellBack) =>
        CellVertex(g, i, j, k, step, unit, inside, sheet, which, out q, out stable, out unresolved, out fellBack, out _, out _);

    /// <paramref name="opposing"/>: the sheet holds two normals more than 90° apart. <paramref name="contradictory"/>:
    /// that, and its vertex does not fit them (fit residual over 0.05 cells, or the placement fell back to the in-cell point).
    static V3 CellVertex(Grid g, int i, int j, int k, int step, double unit, ReadOnlySpan<bool> inside, ReadOnlySpan<int> sheet, int which,
        out Qef q, out V3? stable, out bool unresolved, out bool fellBack, out bool opposing, out bool contradictory)
    {
        var cellMin = g.P(i, j, k);
        double h = unit;
        q = default;
        Span<V3> normals = stackalloc V3[12];
        int count = 0;
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
            var n = g.Field.Gradient(p, 1e-5 * h).Normalized();
            normals[count++] = n;
            q.Add(n, p - g.Origin);
        }
        var x = Place(g, q, cellMin - g.Origin, h, h, out stable, out fellBack);
        double rms = q.Rms(x);
        unresolved = fellBack || rms > 0.05 * h;
        // A resolved corner: the stable fit keeps all three plane directions, and the fit and the field both put the
        // vertex on them. Normals far apart are expected there, so they alone don't mark the cell unresolved.
        bool clean = !unresolved && stable == null && q.Rank(Cutoff) == 3 && rms <= 0.05 * h && Off(g, x, h) <= 0.02 * h;
        opposing = false;
        for (int a = 0; a < count && !opposing; a++)
            for (int b = a + 1; b < count; b++)
                if (V3.Dot(normals[a], normals[b]) < 0) { opposing = true; break; }
        if (opposing && !clean) unresolved = true;
        contradictory = opposing && (fellBack || rms > 0.05 * h);
        x += g.Origin;
        if (stable is { } s) stable = s + g.Origin;
        return x;
    }

    /// <see cref="CellVertex"/> without the diagnostics.
    static V3 CellVertex(Grid g, int i, int j, int k, int step, double unit, ReadOnlySpan<bool> inside, ReadOnlySpan<int> sheet, int which,
        out Qef q, out V3? stable, out bool unresolved) =>
        CellVertex(g, i, j, k, step, unit, inside, sheet, which, out q, out stable, out unresolved, out _);

    /// Vertex position (relative to the grid origin) for a cell at <paramref name="min"/> of side
    /// <paramref name="size"/>.
    ///
    /// Dropping small eigenvalues keeps the vertex stable on smooth patches, but it also flattens a
    /// shallow crease: two surfaces meeting nearly tangent give a second eigenvalue far below the first,
    /// and the vertex then sits between them instead of on the seam. So when the stable answer is
    /// noticeably off the surface, also solve keeping nearly all eigenvalues and take that answer if the
    /// field says it is at least twice as close. The field, not the fit, is the judge, so a sharp solve
    /// that slid away on a smooth patch is never preferred. When the sharp answer is taken,
    /// <paramref name="stable"/> returns the other one, to fall back to if the vertex folds a triangle.
    ///
    /// If neither answer is within <see cref="OffSurface"/> cells of the surface, the cell does not resolve the
    /// surface there and the QEF's answer cannot be trusted: the vertex goes to its <see cref="InCell"/> point, which
    /// is also the last step of the crossing repair in Mesh.
    /// <paramref name="unit"/> is the cell size the thresholds are in: the cell's own level for a finest or coarse
    /// cell, the base cell for a merged one. <paramref name="fellBack"/>: the in-cell point was used.
    static V3 Place(Grid g, in Qef q, V3 min, double size, double unit, out V3? stable, out bool fellBack)
    {
        fellBack = false;
        var x = Solve(g, q, min, size, unit, out stable);
        if (Off(g, x, unit) <= OffSurface * unit) return x;
        if (stable is { } s && Off(g, s, unit) <= OffSurface * unit) { stable = null; return s; }
        stable = null;
        fellBack = true;
        return InCell(g, q, min, size, unit);
    }

    /// Distance estimate from <paramref name="x"/> (relative to the grid origin) to the surface: |f| / |grad f|.
    /// A field may be scaled down by any factor and stay valid, so a small gradient is not a small distance:
    /// without a usable gradient the distance is unknown, which counts as far.
    static double Off(Grid g, V3 x, double unit)
    {
        var p = x + g.Origin;
        double f = Math.Abs(g.Field.Value(p));
        if (f == 0) return 0;
        double len = g.Field.Gradient(p, 1e-5 * unit).Length;
        double d = f / len;
        return len > 0 && double.IsFinite(d) ? d : double.PositiveInfinity;
    }

    /// The mass point of the crossings, walked onto the surface by Newton steps that never leave the cell.
    static V3 InCell(Grid g, in Qef q, V3 min, double size, double unit)
    {
        var x = ClampIn(q.Mass, min, size);
        for (int it = 0; it < 6; it++)
        {
            var p = x + g.Origin;
            double f = g.Field.Value(p);
            if (f == 0) break;
            var grad = g.Field.Gradient(p, 1e-5 * unit);
            double g2 = V3.Dot(grad, grad);
            // Stop on the surface by the distance estimate, not by |f|, which any valid scaling of the field shrinks.
            if (!(g2 > 0) || !double.IsFinite(f / g2) || Math.Abs(f) / Math.Sqrt(g2) < 1e-4 * unit) break;
            x = ClampIn(x - grad * (f / g2), min, size);
        }
        return x;
    }

    static V3 ClampIn(V3 x, V3 min, double size) => new(
        Math.Clamp(x.X, min.X, min.X + size), Math.Clamp(x.Y, min.Y, min.Y + size), Math.Clamp(x.Z, min.Z, min.Z + size));

    /// The QEF placement with its sharp second attempt (see Place).
    static V3 Solve(Grid g, in Qef q, V3 min, double size, double unit, out V3? stable)
    {
        stable = null;
        var safe = Clamp(q.Solve(Cutoff), min, size, unit);
        if (SharpCutoff <= 0) return safe;
        double off = Math.Abs(g.Field.Value(safe + g.Origin));
        if (off <= 0.02 * unit) return safe;
        var sharp = Clamp(q.Solve(SharpCutoff), min, size, unit);
        if (Math.Abs(g.Field.Value(sharp + g.Origin)) >= 0.5 * off) return safe;
        stable = safe;
        return sharp;
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
