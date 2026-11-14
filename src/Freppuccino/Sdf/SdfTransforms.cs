using System;
using System.Collections.Generic;
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
    };

    public static void Register(string sdfType, SdfTransformHandler handler) => Handlers[sdfType] = handler;

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

        return handler(attributes, xform, out reason);
    }
}
