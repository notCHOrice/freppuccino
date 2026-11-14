using System;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Sdf;

/// <summary>
/// Writes F-rep primitive metadata as Attribute User Text using the parameter names from
/// Inigo Quilez's distance functions (https://iquilezles.org/articles/distfunctions/).
///
/// Schema (shared by every primitive):
///   SDF.Version       schema version
///   SDF.Type          IQ function name, e.g. "sdBox", "sdSphere", "sdCappedCylinder"
///   SDF.Units         model unit system the values are expressed in
///   SDF.Frame.Origin  "x,y,z"  world position of the primitive's local origin
///   SDF.Frame.XAxis   "x,y,z"  local X axis (unit vector, world space)
///   SDF.Frame.YAxis   "x,y,z"  local Y axis (unit vector, world space); Z = X × Y
///   SDF.&lt;param&gt;       IQ parameters, vectors split per component (SDF.b.x, SDF.b.y, ...)
///
/// IQ functions are defined in local space with the primitive centered at the origin,
/// so evaluate as sdXxx(worldToLocal(p), params...).
/// </summary>
public static class SdfUserText
{
    public const string Prefix = "SDF.";
    public const string SchemaVersion = "1";

    public const string VersionKey = Prefix + "Version";
    public const string TypeKey = Prefix + "Type";
    public const string UnitsKey = Prefix + "Units";
    public const string OriginKey = Prefix + "Frame.Origin";
    public const string XAxisKey = Prefix + "Frame.XAxis";
    public const string YAxisKey = Prefix + "Frame.YAxis";

    /// <summary>Writes the common header and local frame. Call once per primitive, then add parameters.</summary>
    public static void WriteHeader(ObjectAttributes attributes, string sdfType, Plane frame, UnitSystem units)
    {
        attributes.SetUserString(VersionKey, SchemaVersion);
        attributes.SetUserString(TypeKey, sdfType);
        attributes.SetUserString(UnitsKey, units.ToString());
        attributes.SetUserString(OriginKey, Format(frame.Origin.X, frame.Origin.Y, frame.Origin.Z));
        attributes.SetUserString(XAxisKey, Format(frame.XAxis.X, frame.XAxis.Y, frame.XAxis.Z));
        attributes.SetUserString(YAxisKey, Format(frame.YAxis.X, frame.YAxis.Y, frame.YAxis.Z));
    }

    /// <summary>Scalar parameter, e.g. WriteFloat(a, "r", radius) -> SDF.r</summary>
    public static void WriteFloat(ObjectAttributes attributes, string name, double value) =>
        attributes.SetUserString(Prefix + name, Format(value));

    /// <summary>vec2 parameter, e.g. WriteVec2(a, "t", R, r) -> SDF.t.x, SDF.t.y</summary>
    public static void WriteVec2(ObjectAttributes attributes, string name, double x, double y)
    {
        WriteFloat(attributes, name + ".x", x);
        WriteFloat(attributes, name + ".y", y);
    }

    /// <summary>vec3 parameter, e.g. WriteVec3(a, "b", hx, hy, hz) -> SDF.b.x, SDF.b.y, SDF.b.z</summary>
    public static void WriteVec3(ObjectAttributes attributes, string name, double x, double y, double z)
    {
        WriteFloat(attributes, name + ".x", x);
        WriteFloat(attributes, name + ".y", y);
        WriteFloat(attributes, name + ".z", z);
    }

    private static string Format(double x, double y, double z) =>
        Format(x) + "," + Format(y) + "," + Format(z);

    private static string Format(double value)
    {
        // Avoid "-0" and float noise; invariant culture so values parse identically on any locale.
        if (Math.Abs(value) < 1e-12)
            value = 0.0;
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
