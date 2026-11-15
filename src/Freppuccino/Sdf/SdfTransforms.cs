using System;
using System.Collections.Generic;
using System.Linq;
using Freppuccino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Sdf;

/// <summary>
/// Rewrites a primitive's SDF.* values so they describe the primitive after <paramref name="xform"/>.
/// Return false (with a reason) if the transformed shape can no longer be expressed by this SDF type,
/// e.g. a box that has been sheared. <paramref name="attributes"/> is a copy; only commit it on success.
/// </summary>
public delegate bool SdfTransformHandler(ObjectAttributes attributes, Transform xform, out string? reason);

/// <summary>Per-SDF.Type transform rules. Each new primitive command registers its handler here.</summary>
public static class SdfTransforms
{
    private static readonly Dictionary<string, SdfTransformHandler> Handlers = new(StringComparer.Ordinal)
    {
        [FBoxCommand.SdfType] = FBoxCommand.TransformSdf,
        [FSphereCommand.SdfType] = FSphereCommand.TransformSdf,
        [FTorusCommand.SdfType] = FTorusCommand.TransformSdf,
        [FCylinderCommand.SdfType] = FCylinderCommand.TransformSdf,
        [FConeCommand.SdfType] = FConeCommand.TransformSdf,
        [FEllipsoidCommand.SdfType] = FEllipsoidCommand.TransformSdf,
        [FPyramidCommand.SdfType] = FPyramidCommand.TransformSdf,
    };

    public static void Register(string sdfType, SdfTransformHandler handler) => Handlers[sdfType] = handler;

    /// <summary>True if two scale factors match within a relative tolerance (e.g. a round cross-section stays round).</summary>
    public static bool NearlyEqual(double a, double b)
    {
        const double uniformTolerance = 1e-6;
        return Math.Abs(a - b) <= uniformTolerance * Math.Max(Math.Abs(a), Math.Abs(b));
    }

    /// <summary>
    /// Moves a local frame by <paramref name="xform"/>. <paramref name="scale"/> is how much each local axis
    /// was stretched. Fails if an axis collapses, or the axes stop being perpendicular (shear, or
    /// non-uniform scale along directions other than the frame's own axes).
    /// </summary>
    public static bool TryTransformFrame(Plane frame, Transform xform, out Plane result, out Vector3d scale, out string? reason)
    {
        result = Plane.Unset;
        scale = Vector3d.Unset;

        var origin = frame.Origin;
        origin.Transform(xform);

        // Vector3d.Transform applies only the linear part, so translation doesn't affect the axes.
        var x = frame.XAxis; x.Transform(xform);
        var y = frame.YAxis; y.Transform(xform);
        var z = frame.ZAxis; z.Transform(xform);

        double sx = x.Length, sy = y.Length, sz = z.Length;
        const double degenerate = 1e-12;
        if (sx < degenerate || sy < degenerate || sz < degenerate)
        {
            reason = "transform flattens the primitive";
            return false;
        }

        x /= sx; y /= sy; z /= sz;

        const double perpendicularTolerance = 1e-6;
        if (Math.Abs(x * y) > perpendicularTolerance ||
            Math.Abs(y * z) > perpendicularTolerance ||
            Math.Abs(z * x) > perpendicularTolerance)
        {
            reason = "transform skews the primitive (shear, or non-uniform scale off its axes)";
            return false;
        }

        // Mirroring can flip X × Y relative to the transformed Z; fine for the symmetric primitives we support.
        result = new Plane(origin, x, y);
        scale = new Vector3d(sx, sy, sz);
        reason = null;
        return true;
    }

    /// <summary>
    /// Ellipsoid (frame + semi-axes r) after <paramref name="xform"/>. Any non-degenerate affine map takes an ellipsoid to an
    /// ellipsoid, including shear and non-uniform scale off its axes, so this only fails if the result is flat.
    /// Keeps the existing axes when they stay perpendicular; otherwise reads the new principal axes off A·Aᵀ,
    /// where A = linear part × frame axes × diag(r) maps the unit ball onto the ellipsoid.
    /// </summary>
    public static bool TryTransformEllipsoid(Plane frame, Vector3d r, Transform xform, out Plane result, out Vector3d radii, out string? reason)
    {
        if (TryTransformFrame(frame, xform, out result, out var scale, out reason))
        {
            radii = new Vector3d(r.X * scale.X, r.Y * scale.Y, r.Z * scale.Z);
            return true;
        }

        radii = Vector3d.Unset;
        var origin = frame.Origin;
        origin.Transform(xform);
        var cols = new[] { frame.XAxis * r.X, frame.YAxis * r.Y, frame.ZAxis * r.Z };
        for (int i = 0; i < 3; i++) cols[i].Transform(xform);

        var m = new double[3, 3]; // A·Aᵀ = sum of outer products of A's columns
        foreach (var c in cols)
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] += c[i] * c[j];

        SymmetricEigen(m, out var w, out var v);
        double max = Math.Max(w[0], Math.Max(w[1], w[2]));
        if (!(max > 0) || Math.Min(w[0], Math.Min(w[1], w[2])) <= 1e-18 * max)
        {
            reason = "transform flattens the primitive";
            return false;
        }

        var x = new Vector3d(v[0, 0], v[1, 0], v[2, 0]);
        var y = new Vector3d(v[0, 1], v[1, 1], v[2, 1]);
        result = new Plane(origin, x, y);
        radii = new Vector3d(Math.Sqrt(w[0]), Math.Sqrt(w[1]), Math.Sqrt(w[2]));
        reason = null;
        return result.IsValid;
    }

    /// <summary>Cyclic Jacobi on a symmetric 3×3 (destroys <paramref name="a"/>). Eigenvalues w, eigenvectors in v's columns.</summary>
    internal static void SymmetricEigen(double[,] a, out double[] w, out double[,] v)
    {
        v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        double scale = Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]);
        for (int sweep = 0; sweep < 32; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off <= 1e-15 * scale) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (a[p, q] == 0) continue;
                    // Rotation in the (p, q) plane that zeroes a[p, q] (Numerical Recipes 11.1).
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        w = [a[0, 0], a[1, 1], a[2, 2]];
    }

    public static bool TryApply(ObjectAttributes attributes, Transform xform, out string? reason)
    {
        var type = SdfUserText.ReadType(attributes);
        if (type == null)
        {
            reason = "object has no " + SdfUserText.TypeKey;
            return false;
        }

        if (!Handlers.TryGetValue(type, out var handler))
        {
            reason = "no transform rule for " + type;
            return false;
        }

        if (!xform.IsValid || !xform.IsAffine)
        {
            reason = "transform is not affine";
            return false;
        }

        // Leaf placement P (Scene.Placement): once P is set, the transform composes into it and the SDF values stay put
        // (feeding it to the handler would compose in the wrong space). Otherwise the handler first; whatever it can't
        // represent (shear, non-uniform scale off the frame axes) becomes P instead of being rejected.
        switch (Scene.Placement.ReadLeaf(attributes, out var place))
        {
            case Scene.Placement.Stored.Valid:
                return TryPlace(attributes, xform * place, out reason);
            case Scene.Placement.Stored.Invalid:
                reason = "unreadable or degenerate " + Scene.Placement.LeafKey;
                return false;
        }
        // As before placements existed: the handler edits the attributes in place, and only on success.
        if (handler(attributes, xform, out reason)) return true;
        return TryPlace(attributes, xform, out reason);
    }

    static bool TryPlace(ObjectAttributes attributes, Transform place, out string? reason)
    {
        if (!Scene.Placement.TryRows(place, out _))
        {
            reason = "transform flattens the primitive";
            return false;
        }
        Scene.Placement.WriteLeaf(attributes, place);
        reason = null;
        return true;
    }

}
