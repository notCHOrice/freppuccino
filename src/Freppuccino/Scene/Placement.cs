using System.Globalization;
using Freppuccino.Meshing;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// General affine placements, stored as user text (12 numbers, the top three rows of the 4x4, row-major).
///
/// Leaf placement P (SDF.Place): the leaf's field is sd(P⁻¹ p) · s(P), s = smallest singular value of P's linear part,
/// so it still changes by at most 1 per unit distance. Absent = identity. The per-type transform handlers keep simple
/// edits in the readable SDF.* values; whatever they can't represent (shear, off-axis non-uniform scale) goes into P.
///
/// Applied root transforms A (FREP.Applied) on a warp reference object (axis line / repeat box): the accumulated root
/// transforms Rhino has applied to that object's real geometry. Its canonical geometry is A⁻¹ × geometry. Absent = identity.
public static class Placement
{
    public const string LeafKey = "SDF.Place";
    public const string AppliedKey = "FREP.Applied";

    /// Rows a placed primitive adds to the shader data (and FrepField takes): 3 rows of P⁻¹ (xyz linear row, w translation),
    /// then (s(P), 0, 0, 0).
    public const int ShaderRows = 4;

    public static bool TryReadLeaf(ObjectAttributes a, out Transform place) => TryRead(a.GetUserString(LeafKey), out place);
    public static bool TryReadApplied(ObjectAttributes a, out Transform applied) => TryRead(a.GetUserString(AppliedKey), out applied);

    /// Identity (or null) removes the key.
    public static void WriteLeaf(ObjectAttributes a, Transform? place) => Write(a, LeafKey, place);
    public static void WriteApplied(ObjectAttributes a, Transform? applied) => Write(a, AppliedKey, applied);

    public static bool IsIdentity(Transform t, double tol = 1e-12)
    {
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(t[r, c] - (r == c ? 1 : 0)) > tol) return false;
        return true;
    }

    /// Smallest singular value of the linear part (sqrt of AᵀA's smallest eigenvalue).
    public static double MinScale(Transform t) => Singular(t).Min;

    /// Largest / smallest singular value of the linear part (1 for a similarity); infinity if singular.
    public static double Condition(Transform t)
    {
        var (lo, hi) = Singular(t);
        return lo > 0 ? hi / lo : double.PositiveInfinity;
    }

    static (double Min, double Max) Singular(Transform t)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    m[i, j] += t[k, i] * t[k, j];
        Sdf.SdfTransforms.SymmetricEigen(m, out var w, out _);
        double lo = Math.Min(w[0], Math.Min(w[1], w[2])), hi = Math.Max(w[0], Math.Max(w[1], w[2]));
        return (Math.Sqrt(Math.Max(0, lo)), Math.Sqrt(Math.Max(0, hi)));
    }

    /// Shader / FrepField rows for a leaf placement; false if P is singular.
    public static bool TryRows(Transform place, out V4[] rows)
    {
        rows = [];
        if (!place.TryGetInverse(out var inv)) return false;
        double s = MinScale(place);
        if (!(s > 0)) return false;
        rows = [
            new(inv.M00, inv.M01, inv.M02, inv.M03),
            new(inv.M10, inv.M11, inv.M12, inv.M13),
            new(inv.M20, inv.M21, inv.M22, inv.M23),
            new(s, 0, 0, 0),
        ];
        return true;
    }

    static bool TryRead(string? text, out Transform t)
    {
        t = Transform.Identity;
        var parts = text?.Split(',');
        if (parts is not { Length: 12 }) return false;
        for (int i = 0; i < 12; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { t = Transform.Identity; return false; }
            t[i / 4, i % 4] = v;
        }
        return t.IsValid && Math.Abs(t.Determinant) > 1e-18;
    }

    static void Write(ObjectAttributes a, string key, Transform? t)
    {
        if (t is not { } v || IsIdentity(v)) { a.DeleteUserString(key); return; }
        var s = new string[12];
        for (int i = 0; i < 12; i++) s[i] = v[i / 4, i % 4].ToString("R", CultureInfo.InvariantCulture);
        a.SetUserString(key, string.Join(",", s));
    }
}
