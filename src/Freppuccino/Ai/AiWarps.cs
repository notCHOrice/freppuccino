using System.Text.Json.Nodes;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Ai;

/// <summary>
/// AI tools for warp nodes (Twist / Bend / Repeat, see <see cref="FrepOps"/>). A warp wraps one child and is driven by a
/// referenced Rhino object, which these tools create and keep in sync:
///   twist   axis_start, axis_end, angle_degrees               → line; params [angle]
///   bend    axis_start, axis_end, angle_degrees, toward       → line; params [angle, direction°]
///   repeat  count [nx,ny,nz], spacing [sx,sy,sz], centered,   → box around the lattice; params = spacing along the box axes
///           x_axis?, y_axis?
/// The same fields come back from get_scene, so a description can be edited and sent back (edit_operation).
/// Reference objects the AI makes carry the user string <see cref="AiRefKey"/> and are deleted with their node.
/// </summary>
public static partial class AiTools
{
    const string AiRefKey = "Freppuccino.AiRef";

    static FrepOp ParseWarp(string op) => op.ToLowerInvariant() switch
    {
        "twist" => FrepOp.Twist,
        "bend" => FrepOp.Bend,
        "repeat" or "array" or "pattern" => FrepOp.Repeat,
        _ => throw new AiToolException($"unknown deform '{op}'; use twist, bend or repeat"),
    };

    static JsonNode Deform(RhinoDoc doc, JsonObject args)
    {
        var op = ParseWarp(Str(args, "op"));
        var tree = FrepTrees.Get(doc);
        var targetRef = Str(args, "target");
        var target = Resolve(doc, tree, targetRef);
        var child = FrepScene.WarpedBoundsOf(doc, target);
        if (!child.IsValid) throw new AiToolException($"'{targetRef}' has no geometry to deform");

        var spec = Defaults(op, child);
        foreach (var (key, value) in args)
            if (key is not ("op" or "target")) spec[key] = value?.DeepClone();
        if (args.ContainsKey("direction_degrees")) spec.Remove("toward");
        var (geometry, parameters) = BuildWarp(op, spec, child);

        FrepNode? node = null;
        var refName = $"{(target.IsNode ? FrepTree.NodeName(target.NodeId) : targetRef)}_{OpName(op)}_{(op == FrepOp.Repeat ? "box" : "axis")}";
        Mutate(doc, $"{OpName(op)} {targetRef}", () =>
        {
            var refId = AddRefObject(doc, geometry, refName);
            (var newTree, node) = tree.AddWarp(op, target, refId, parameters);
            FrepTrees.Set(doc, newTree, $"AI: {OpName(op)}");
        });

        var result = DescribeWarp(doc, FrepTrees.Get(doc), node!, null);
        var warnings = new List<string>();
        if (tree.IsReferenced(target))
            warnings.Add($"{targetRef} already belonged to another operation, so it is now used twice. Usually you want to deform " +
                         "the top node of a part, or deform first and combine the deformed node.");
        if (Steepness(op, geometry, parameters, child) is { } steep) warnings.Add(steep);
        if (warnings.Count > 0) result["warning"] = string.Join(" ", warnings);
        return result;
    }

    /// Max twist shear (rad of twist per unit length × distance from the axis) we encourage. Up to 10 rendered solid in
    /// tests; past that the shader's shrinking step bound risks holes and slows the viewport.
    const double MaxTwistShear = 12.0;

    /// A warning when the warp is too steep to render well, with what to change; null when fine.
    static string? Steepness(FrepOp op, GeometryBase geometry, double[] parameters, BoundingBox child)
    {
        if (op == FrepOp.Repeat || geometry is not LineCurve lc) return null;
        var line = lc.Line;
        double extent = child.GetCorners().Max(c => line.DistanceTo(c, false));
        double angle = Math.Abs(RhinoMath.ToRadians(parameters[0]));
        if (op == FrepOp.Twist)
        {
            double shear = angle / line.Length * extent;
            return shear <= MaxTwistShear ? null
                : $"This twist is too steep to render cleanly (shear {shear:0.#}, keep it ≤ {MaxTwistShear}: twist radians ÷ axis length × " +
                  $"distance from the axis {extent:0.##}). Expect holes and a slow viewport. Use a smaller angle, a longer axis, or keep the " +
                  "shape closer to the axis.";
        }
        double radius = angle > 1e-9 ? line.Length / angle : double.PositiveInfinity;
        // Measured from bounding-box corners, so it overstates thickness; tests pinched visibly from ~1.5× the radius.
        return extent < 1.25 * radius ? null
            : $"This bend is tighter than the shape is thick (bend radius {radius:0.##} = axis length ÷ angle in radians; the shape reaches " +
              $"{extent:0.##} from the axis). The inside of the bend may pinch. Use a smaller angle or a longer axis.";
    }

    static JsonNode EditWarp(RhinoDoc doc, FrepTree tree, FrepNode node, JsonObject args)
    {
        if (args.ContainsKey("op") && !string.Equals(Str(args, "op"), OpName(node.Op), StringComparison.OrdinalIgnoreCase))
            throw new AiToolException($"{node.Name} is a {OpName(node.Op)}; a deform can't change type (delete it and deform again)");
        var child = FrepScene.WarpedBoundsOf(doc, node.Operands[0]);
        if (!child.IsValid) throw new AiToolException($"{node.Name}'s target has no geometry");

        var spec = DescribeWarp(doc, tree, node, null);
        foreach (var (key, value) in args)
            if (key is not ("ref" or "op" or "k" or "target")) spec[key] = value?.DeepClone();
        if (args.ContainsKey("direction_degrees")) spec.Remove("toward");
        var (geometry, parameters) = BuildWarp(node.Op, spec, child);

        Mutate(doc, $"edit {node.Name}", () =>
        {
            var t = tree;
            var refId = node.Ref;
            bool replaced = doc.Objects.FindId(refId) is { IsDeleted: false } && geometry switch
            {
                Curve c => doc.Objects.Replace(refId, c),
                Brep b => doc.Objects.Replace(refId, b),
                _ => false,
            };
            if (!replaced)
            {
                refId = AddRefObject(doc, geometry, $"{node.Name}_{OpName(node.Op)}_{(node.Op == FrepOp.Repeat ? "box" : "axis")}");
                var i = t.Nodes.FindIndex(n => n.Id == node.Id);
                t = t with { Nodes = t.Nodes.SetItem(i, t.Nodes[i] with { Ref = refId }) };
            }
            for (int p = 0; p < parameters.Length; p++) t = t.WithParam(node.Id, p, parameters[p]);
            FrepTrees.Set(doc, t, $"AI: edit {node.Name}");
        });
        var tree2 = FrepTrees.Get(doc);
        var result = DescribeWarp(doc, tree2, tree2.Find(node.Id)!, null);
        if (Steepness(node.Op, geometry, parameters, child) is { } steep) result["warning"] = steep;
        return result;
    }

    /// Defaults that fit the target: axes along its bounding box's Z (bottom → top center), one copy per axis.
    static JsonObject Defaults(FrepOp op, BoundingBox child)
    {
        var c = child.Center;
        return op == FrepOp.Repeat
            ? new JsonObject { ["count"] = new JsonArray(1, 1, 1), ["spacing"] = new JsonArray(0, 0, 0), ["centered"] = false }
            : new JsonObject
            {
                ["axis_start"] = AiShapes.Json(new Point3d(c.X, c.Y, child.Min.Z)),
                ["axis_end"] = AiShapes.Json(new Point3d(c.X, c.Y, child.Max.Z)),
            };
    }

    /// Spec → reference geometry + node params.
    static (GeometryBase Geometry, double[] Params) BuildWarp(FrepOp op, JsonObject s, BoundingBox child)
    {
        if (op == FrepOp.Repeat) return BuildRepeat(s, child);

        var a = new Point3d(AiShapes.Vec(s, "axis_start"));
        var b = new Point3d(AiShapes.Vec(s, "axis_end"));
        var d = b - a;
        if (d.Length < 1e-9) throw new AiToolException("axis_start and axis_end must be different points");
        var angle = s.ContainsKey("angle_degrees") ? AiShapes.Number(s, "angle_degrees") : throw new AiToolException("missing 'angle_degrees'");
        var line = new LineCurve(a, b);
        if (op == FrepOp.Twist) return (line, [angle]);

        // Bend direction: 'toward' (a vector) wins; else direction_degrees around the axis from FrepWarpMath.BendReference.
        d.Unitize();
        var n0 = FrepWarpMath.BendReference(d);
        double direction;
        if (s["toward"] is JsonArray)
        {
            var t = AiShapes.Vec(s, "toward");
            t -= d * (t * d);
            if (!t.Unitize()) throw new AiToolException("'toward' must not be parallel to the axis");
            direction = RhinoMath.ToDegrees(Math.Atan2(t * Vector3d.CrossProduct(d, n0), t * n0));
        }
        else direction = s.ContainsKey("direction_degrees") ? AiShapes.Number(s, "direction_degrees") : 0;
        return (line, [angle, AiShapes.Round(direction)]);
    }

    /// Box around count × spacing lattice points; the child (bounding box center) is lattice point 0.
    static (GeometryBase Geometry, double[] Params) BuildRepeat(JsonObject s, BoundingBox child)
    {
        var count = AiShapes.Vec(s, "count");
        var spacing = AiShapes.Vec(s, "spacing");
        bool centered = s["centered"]?.GetValue<bool>() ?? false;
        var x = s["x_axis"] is JsonArray ? AiShapes.Vec(s, "x_axis") : Vector3d.XAxis;
        var y = s["y_axis"] is JsonArray ? AiShapes.Vec(s, "y_axis") : (x.IsParallelTo(Vector3d.YAxis, 1e-3) != 0 ? -Vector3d.XAxis : Vector3d.YAxis);
        var frame = new Plane(child.Center, x, y);
        if (!frame.IsValid) throw new AiToolException("x_axis and y_axis must not be parallel");
        Vector3d[] axes = [frame.XAxis, frame.YAxis, frame.ZAxis];

        var effective = new double[3];
        var intervals = new Interval[3];
        for (int i = 0; i < 3; i++)
        {
            int n = (int)Math.Round(count[i]);
            if (n < 1 || n > 200) throw new AiToolException("count values must be 1 … 200");
            // Child's extent along this axis, for axes without copies (any nonzero box size works there).
            double half = 0;
            foreach (var c in child.GetCorners()) half = Math.Max(half, Math.Abs((c - child.Center) * axes[i]));
            half = Math.Max(half, 1e-3);
            if (n == 1)
            {
                intervals[i] = new Interval(-half, half);
                continue;
            }
            if (!(spacing[i] > 0)) throw new AiToolException($"spacing[{i}] must be > 0 when count[{i}] > 1");
            int lo = centered ? -(n - 1) / 2 : 0, hi = lo + n - 1;
            intervals[i] = new Interval((lo - 0.5) * spacing[i], (hi + 0.5) * spacing[i]);
            effective[i] = spacing[i];
        }
        if (effective.All(v => v == 0)) throw new AiToolException("repeat needs a count > 1 on at least one axis");
        var brep = new Box(frame, intervals[0], intervals[1], intervals[2]).ToBrep()
                   ?? throw new AiToolException("couldn't build the repeat box");

        // FrepRefs reads the box back in its own axis order: give each of its axes the spacing of the matching lattice axis.
        if (!FrepRefs.TryBox(brep, out var read)) throw new AiToolException("couldn't read back the repeat box");
        Vector3d[] readAxes = [read.Plane.XAxis, read.Plane.YAxis, read.Plane.ZAxis];
        var parameters = readAxes.Select(r => effective[BestAxis(r, axes)]).ToArray();
        return (brep, parameters);
    }

    static int BestAxis(Vector3d v, Vector3d[] axes)
    {
        int best = 0;
        for (int i = 1; i < axes.Length; i++)
            if (Math.Abs(v * axes[i]) > Math.Abs(v * axes[best])) best = i;
        return best;
    }

    static Guid AddRefObject(RhinoDoc doc, GeometryBase geometry, string name)
    {
        var attributes = doc.CreateDefaultAttributes();
        attributes.Name = name;
        attributes.SetUserString(AiRefKey, "1");
        var id = geometry switch
        {
            Curve c => doc.Objects.AddCurve(c, attributes),
            Brep b => doc.Objects.AddBrep(b, attributes),
            _ => Guid.Empty,
        };
        return id != Guid.Empty ? id : throw new AiToolException("couldn't add the deform's reference object");
    }

    /// Deletes AI-made reference objects no node uses any more.
    static void DeleteOrphanRefs(RhinoDoc doc, FrepTree tree, IEnumerable<Guid> candidates)
    {
        foreach (var id in candidates.Distinct())
            if (id != Guid.Empty && !tree.Nodes.Any(n => n.Ref == id) &&
                doc.Objects.FindId(id) is { IsDeleted: false } o && o.Attributes.GetUserString(AiRefKey) == "1")
                doc.Objects.Delete(id, true);
    }

    static IEnumerable<RhinoObject> AiRefObjects(RhinoDoc doc) =>
        doc.Objects.GetObjectList(new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, NormalObjects = true })
            .Where(o => o.Attributes.GetUserString(AiRefKey) == "1");

    /// Warp node → the spec fields above (+ ref, op, target, ref_object).
    static JsonObject DescribeWarp(RhinoDoc doc, FrepTree tree, FrepNode n, Dictionary<Guid, string>? refs)
    {
        var o = new JsonObject
        {
            ["ref"] = n.Name,
            ["op"] = OpName(n.Op),
            ["target"] = n.Operands.Length > 0 ? OperandRef(doc, n.Operands[0], refs) : null,
        };
        var obj = doc.Objects.FindId(n.Ref) is { IsDeleted: false } r ? r : null;
        if (obj != null && !string.IsNullOrEmpty(obj.Attributes.Name)) o["ref_object"] = obj.Attributes.Name;

        if (n.Op == FrepOp.Repeat)
        {
            if (obj == null || !FrepRefs.TryBox(obj.Geometry, out var box))
            {
                o["ref_missing"] = true;
                return o;
            }
            var anchor = n.Operands.Length > 0 ? FrepScene.WarpedBoundsOf(doc, n.Operands[0]).Center : box.Center;
            box.Plane.RemapToPlaneSpace(anchor, out var local);
            Interval[] iv = [box.X, box.Y, box.Z];
            double[] at = [local.X, local.Y, local.Z];
            Vector3d[] boxAxes = [box.Plane.XAxis, box.Plane.YAxis, box.Plane.ZAxis];

            // Report in the box axes closest to world X, Y, Z (pointing the positive way), whatever order the box came in.
            Vector3d[] world = [Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis];
            var count = new double[3];
            var spacing = new double[3];
            var outAxes = new Vector3d[3];
            bool centered = false;
            var used = new HashSet<int>();
            for (int w = 0; w < 3; w++)
            {
                int j = Enumerable.Range(0, 3).Where(k => !used.Contains(k)).MaxBy(k => Math.Abs(boxAxes[k] * world[w]));
                used.Add(j);
                double sign = boxAxes[j] * world[w] < 0 ? -1 : 1;
                double sp = n.Param(j);
                double lo = 0, hi = 0;
                if (sp > 0)
                {
                    lo = Math.Min(0, Math.Ceiling((iv[j].Min - at[j]) / sp - 1e-9));
                    hi = Math.Max(0, Math.Floor((iv[j].Max - at[j]) / sp + 1e-9));
                }
                count[w] = hi - lo + 1;
                spacing[w] = sp > 0 ? sp : 0;
                centered |= sign > 0 ? lo < 0 : hi > 0;
                outAxes[w] = boxAxes[j] * sign;
            }
            o["count"] = new JsonArray([.. count.Select(c => (JsonNode)(int)c)]);
            o["spacing"] = new JsonArray([.. spacing.Select(v => (JsonNode)AiShapes.Round(v))]);
            o["centered"] = centered;
            if (outAxes[0].IsParallelTo(Vector3d.XAxis, 1e-6) != 1 || outAxes[1].IsParallelTo(Vector3d.YAxis, 1e-6) != 1)
            {
                o["x_axis"] = AiShapes.Json(outAxes[0]);
                o["y_axis"] = AiShapes.Json(outAxes[1]);
            }
            return o;
        }

        if (obj == null || !FrepRefs.TryLine(obj.Geometry, out var line))
        {
            o["ref_missing"] = true;
            o["angle_degrees"] = AiShapes.Round(n.Param(0));
            return o;
        }
        o["axis_start"] = AiShapes.Json(line.From);
        o["axis_end"] = AiShapes.Json(line.To);
        o["angle_degrees"] = AiShapes.Round(n.Param(0));
        if (n.Op == FrepOp.Bend)
        {
            var d = line.Direction;
            d.Unitize();
            var n0 = FrepWarpMath.BendReference(d);
            var phi = RhinoMath.ToRadians(n.Param(1));
            o["toward"] = AiShapes.Json(n0 * Math.Cos(phi) + Vector3d.CrossProduct(d, n0) * Math.Sin(phi));
            o["bend_radius"] = Math.Abs(n.Param(0)) > 1e-6 ? AiShapes.Round(line.Length / Math.Abs(RhinoMath.ToRadians(n.Param(0)))) : null;
        }
        return o;
    }
}
