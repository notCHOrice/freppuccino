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
    };

    public static void Register(string sdfType, SdfTransformHandler handler) => Handlers[sdfType] = handler;

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
