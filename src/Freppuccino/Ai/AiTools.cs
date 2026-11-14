using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Freppuccino.Sdf;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Ai;

/// <summary>
/// Tools the side panel's AI agent (src/ui/src/ai) calls through the "aiTool" message. Each takes a JSON object and
/// returns one; failures throw <see cref="AiToolException"/> with a message for the model.
///
/// Objects are referred to by "ref": a primitive's name (unique names are enforced on create), its GUID, or a CSG
/// node name ("N3"). Every edit is one undo step labelled "AI: …". Shapes use <see cref="AiShapes"/> specs.
/// Tool names and argument shapes must match src/ui/src/ai/tools.ts.
/// </summary>
public static partial class AiTools
{
    public static JsonNode Run(RhinoDoc doc, string tool, JsonObject args) => tool switch
    {
        "get_scene" => GetScene(doc),
        "create_primitive" => Create(doc, args),
        "update_primitive" => Update(doc, args),
        "transform" => TransformObjects(doc, args),
        "combine" => Combine(doc, args),
        "edit_operation" => EditOperation(doc, args),
        "delete" => Delete(doc, args),
        "set_visible" => SetVisible(doc, args),
        "clear_scene" => ClearScene(doc),
        "capture_views" => CaptureViews(doc, args),
        _ => throw new AiToolException($"unknown tool '{tool}'"),
    };

    // ───────────────────────── scene ─────────────────────────

    static JsonNode GetScene(RhinoDoc doc)
    {
        var tree = FrepTrees.Get(doc);
        var objects = SdfObjects(doc).Reverse().ToList(); // creation order
        var refs = RefNames(objects);

        var usedBy = new Dictionary<FrepOperand, List<string>>();
        foreach (var n in tree.Nodes)
            foreach (var o in n.Operands)
                (usedBy.TryGetValue(o, out var l) ? l : usedBy[o] = []).Add(n.Name);
        JsonArray UsedBy(FrepOperand o) => [.. (usedBy.GetValueOrDefault(o) ?? []).Select(s => (JsonNode)s)];

        var prims = new JsonArray();
        var bbox = BoundingBox.Empty;
        foreach (var obj in objects)
        {
            var o = FrepPrimitives.TryRead(obj.Attributes, out var p)
                ? AiShapes.ToSpec(p)
                : new JsonObject { ["type"] = SdfUserText.ReadType(obj.Attributes), ["invalid"] = true };
            o.Insert(0, "ref", refs[obj.Id]);
            if (obj.IsHidden) o["visible"] = false;
            var used = UsedBy(FrepOperand.Object(obj.Id));
            if (used.Count > 0) o["used_by"] = used;
            prims.Add(o);
            if (!obj.IsHidden) bbox.Union(obj.Geometry.GetBoundingBox(true));
        }

        var ops = new JsonArray();
        foreach (var n in tree.Nodes)
        {
            var o = new JsonObject
            {
                ["ref"] = n.Name,
                ["op"] = OpName(n.Op),
                ["k"] = AiShapes.Round(n.K),
                ["operands"] = new JsonArray([.. n.Operands.Select(x => (JsonNode)OperandRef(doc, x, refs))]),
            };
            if (n.Hidden) o["visible"] = false;
            var used = UsedBy(FrepOperand.Node(n.Id));
            if (used.Count > 0) o["used_by"] = used;
            ops.Add(o);
        }

        var roots = tree.Nodes.Where(n => !tree.IsReferenced(FrepOperand.Node(n.Id))).Select(n => (JsonNode)n.Name)
            .Concat(objects.Where(o => !tree.IsReferenced(FrepOperand.Object(o.Id))).Select(o => (JsonNode)refs[o.Id]));

        var result = new JsonObject
        {
            ["units"] = doc.ModelUnitSystem.ToString(),
            ["primitives"] = prims,
            ["operations"] = ops,
            ["roots"] = new JsonArray([.. roots]),
        };
        if (bbox.IsValid) result["bounding_box"] = new JsonObject { ["min"] = AiShapes.Json(bbox.Min), ["max"] = AiShapes.Json(bbox.Max) };
        if (FrepScene.Get(doc).Dropped is > 0 and var dropped)
            result["warning"] = $"{dropped} primitives are not drawn: the viewport shader is full. Use fewer primitives.";
        return result;
    }

    // ───────────────────────── primitives ─────────────────────────

    static JsonNode Create(RhinoDoc doc, JsonObject args)
    {
        var type = Str(args, "type").ToLowerInvariant();
        var prim = AiShapes.FromSpec(type, args);
        var name = UniqueName(doc, args["name"]?.GetValue<string>() ?? type);
        Guid id = Guid.Empty;
        Mutate(doc, $"create {name}", () => id = FrepPrimitives.Add(doc, prim, name));
        return new JsonObject { ["ref"] = name, ["guid"] = id.ToString("D") };
    }

    static JsonNode Update(RhinoDoc doc, JsonObject args)
    {
        var obj = ResolveObject(doc, Str(args, "ref"));
        if (!FrepPrimitives.TryRead(obj.Attributes, out var current)) throw new AiToolException("object has unreadable SDF values");

        // Overlay the given fields on the current description, so partial updates keep everything else.
        var spec = AiShapes.ToSpec(current);
        var type = spec["type"]!.GetValue<string>();
        foreach (var (key, value) in args)
            if (key is not ("ref" or "name" or "type")) spec[key] = value?.DeepClone();
        var prim = AiShapes.FromSpec(type, spec);

        string? newName = null;
        if (args["name"]?.GetValue<string>() is { } requested && requested != obj.Attributes.Name)
            newName = UniqueName(doc, requested);
        var label = newName ?? RefOf(doc, obj);
        Mutate(doc, $"update {label}", () => FrepPrimitives.Replace(doc, obj, prim, newName));

        var after = AiShapes.ToSpec(prim);
        after.Insert(0, "ref", newName ?? RefOf(doc, obj));
        return after;
    }

    /// Moves / rotates / scales primitives, or every primitive under a node, with the same rules as the gumball.
    static JsonNode TransformObjects(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var leaves = Refs(args, "refs").SelectMany(r => Leaves(doc, tree, Resolve(doc, tree, r))).Distinct().ToList();
        if (leaves.Count == 0) throw new AiToolException("nothing to transform");

        var bbox = BoundingBox.Empty;
        foreach (var o in leaves) bbox.Union(o.Geometry.GetBoundingBox(true));
        var pivot = args.ContainsKey("pivot") ? new Point3d(AiShapes.Vec(args, "pivot")) : bbox.Center;

        var xf = Transform.Identity;
        if (args["scale"] is JsonArray)
        {
            var s = AiShapes.Vec(args, "scale"); // per world axis, about the pivot
            xf = Transform.Scale(new Plane(pivot, Vector3d.XAxis, Vector3d.YAxis), s.X, s.Y, s.Z);
        }
        else if (args.ContainsKey("scale"))
            xf = Transform.Scale(pivot, AiShapes.Number(args, "scale"));
        if (args["rotate"] is JsonObject rot)
        {
            var angle = RhinoMath.ToRadians(AiShapes.Number(rot, "angle_degrees"));
            var axis = rot.ContainsKey("axis") ? AiShapes.Vec(rot, "axis") : Vector3d.ZAxis;
            if (!axis.Unitize()) throw new AiToolException("rotate.axis must not be a zero vector");
            xf = Transform.Rotation(angle, axis, pivot) * xf;
        }
        if (args.ContainsKey("translate"))
            xf = Transform.Translation(AiShapes.Vec(args, "translate")) * xf;
        if (xf.IsIdentity) throw new AiToolException("give at least one of translate, rotate, scale");

        // Validate everything first so a transform the SDF can't represent changes nothing.
        var updates = new List<(RhinoObject Obj, SdPrimitive Prim)>();
        foreach (var obj in leaves)
        {
            var attributes = obj.Attributes.Duplicate();
            if (!SdfTransforms.TryApply(attributes, xf, out var reason))
                throw new AiToolException($"{RefOf(doc, obj)}: {reason}");
            if (!FrepPrimitives.TryRead(attributes, out var prim))
                throw new AiToolException($"{RefOf(doc, obj)}: transform produced invalid values");
            updates.Add((obj, prim));
        }
        Mutate(doc, "transform", () =>
        {
            foreach (var (obj, prim) in updates) FrepPrimitives.Replace(doc, obj, prim);
        });
        return new JsonObject { ["transformed"] = new JsonArray([.. updates.Select(u => (JsonNode)RefOf(doc, u.Obj))]) };
    }

    // ───────────────────────── CSG ─────────────────────────

    static JsonNode Combine(RhinoDoc doc, JsonObject args)
    {
        var op = ParseOp(Str(args, "op"));
        var k = args.ContainsKey("k") ? Math.Max(0, AiShapes.Number(args, "k")) : 0;
        var tree = FrepTrees.Get(doc);
        var operands = Refs(args, "operands").Select(r => Resolve(doc, tree, r)).ToList();
        if (operands.Distinct().Count() != operands.Count) throw new AiToolException("operands must be distinct");
        if (operands.Count < 2) throw new AiToolException("combine needs at least 2 operands");

        var (newTree, node) = tree.Add(op, k, operands);
        Mutate(doc, $"{OpName(op)} {node.Name}", () => FrepTrees.Set(doc, newTree, $"AI: {OpName(op)}"));

        var result = new JsonObject { ["ref"] = node.Name };
        // Operands already used elsewhere stay drawn there too; usually a mistake, so say so.
        var shared = operands.Where(tree.IsReferenced).Select(o => OperandRef(doc, o, null)).ToList();
        if (shared.Count > 0)
            result["warning"] = $"{string.Join(", ", shared)} already belonged to another operation, so it is now used twice (DAG). " +
                                "If that wasn't intended, delete this node and combine the parent operation instead.";
        return result;
    }

    static JsonNode EditOperation(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var operand = Resolve(doc, tree, Str(args, "ref"));
        if (!operand.IsNode) throw new AiToolException("edit_operation takes an operation node (N…); use update_primitive for primitives");
        var node = tree.Find(operand.NodeId)!;
        var op = args.ContainsKey("op") ? ParseOp(Str(args, "op")) : node.Op;
        var k = args.ContainsKey("k") ? Math.Max(0, AiShapes.Number(args, "k")) : node.K;
        Mutate(doc, $"edit {node.Name}", () => FrepTrees.Set(doc, tree.WithOp(node.Id, op, k), $"AI: edit {node.Name}"));
        return new JsonObject { ["ref"] = node.Name, ["op"] = OpName(op), ["k"] = AiShapes.Round(k) };
    }

    /// Deletes nodes and primitives. recursive: also everything under the given nodes that nothing else uses.
    static JsonNode Delete(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var pending = Refs(args, "refs").Select(r => Resolve(doc, tree, r)).ToHashSet();
        if (args["recursive"]?.GetValue<bool>() == true)
            foreach (var o in pending.ToList()) AddDescendants(tree, o, pending);

        var deleted = new List<string>();
        Mutate(doc, "delete", () =>
        {
            var t = tree;
            bool progress;
            do
            {
                progress = false;
                foreach (var o in pending.Where(o => o.IsNode && !t.IsReferenced(o)).ToList())
                {
                    t = t.Remove(o.NodeId);
                    pending.Remove(o);
                    deleted.Add(FrepTree.NodeName(o.NodeId));
                    progress = true;
                }
            } while (progress);
            FrepTrees.Set(doc, t, "AI: delete");
            foreach (var o in pending.Where(o => !o.IsNode && !t.IsReferenced(o)).ToList())
            {
                var name = OperandRef(doc, o, null);
                if (doc.Objects.Delete(o.ObjectId, true)) deleted.Add(name);
                pending.Remove(o);
            }
        });

        var result = new JsonObject { ["deleted"] = new JsonArray([.. deleted.Select(d => (JsonNode)d)]) };
        if (pending.Count > 0)
            result["not_deleted"] = $"still used by other operations: {string.Join(", ", pending.Select(o => OperandRef(doc, o, null)))} " +
                                    "(delete the parent operation first, or pass recursive=true on the parent)";
        return result;
    }

    static JsonNode SetVisible(RhinoDoc doc, JsonObject args)
    {
        var visible = args["visible"]?.GetValue<bool>() ?? throw new AiToolException("missing 'visible' (true/false)");
        var tree = FrepTrees.Get(doc);
        var operands = Refs(args, "refs").Select(r => Resolve(doc, tree, r)).ToList();
        Mutate(doc, visible ? "show" : "hide", () =>
        {
            var t = tree;
            foreach (var o in operands)
            {
                if (o.IsNode) t = t.WithHidden(o.NodeId, !visible);
                else if (visible) doc.Objects.Show(o.ObjectId, true);
                else doc.Objects.Hide(o.ObjectId, true);
            }
            FrepTrees.Set(doc, t, "AI: visibility");
        });
        return new JsonObject { ["ok"] = true };
    }

    static JsonNode ClearScene(RhinoDoc doc)
    {
        var objects = SdfObjects(doc).Reverse().ToList(); // creation order
        var tree = FrepTrees.Get(doc);
        Mutate(doc, "clear scene", () =>
        {
            FrepTrees.Set(doc, tree with { Nodes = [] }, "AI: clear scene");
            foreach (var o in objects) doc.Objects.Delete(o.Id, true);
        });
        return new JsonObject { ["deleted_primitives"] = objects.Count, ["deleted_operations"] = tree.Nodes.Count };
    }

    // ───────────────────────── screenshots ─────────────────────────

    const int DefaultSize = 512, MaxSize = 1024;
    static readonly string[] DefaultViews = ["perspective", "front", "right", "top"];

    static JsonNode CaptureViews(RhinoDoc doc, JsonObject args)
    {
        var views = args["views"] is JsonArray a && a.Count > 0 ? a.Select(v => v!.GetValue<string>()).ToList() : [.. DefaultViews];
        foreach (var v in views)
            if (!AiCapture.IsView(v)) throw new AiToolException($"unknown view '{v}'; use: {string.Join(", ", AiCapture.ViewNames)}");
        if (views.Count > 6) throw new AiToolException("at most 6 views per call");
        var size = args.ContainsKey("size") ? (int)Math.Clamp(AiShapes.Number(args, "size"), 128, MaxSize) : DefaultSize;

        // Frame the given refs, else every visible primitive.
        var tree = FrepTrees.Get(doc);
        var targets = args["focus"] is JsonArray
            ? Refs(args, "focus").SelectMany(r => Leaves(doc, tree, Resolve(doc, tree, r))).ToList()
            : SdfObjects(doc).Where(o => !o.IsHidden).ToList();
        var boxes = targets.Select(o => o.Geometry.GetBoundingBox(true)).Where(b => b.IsValid).ToList();
        var images = AiCapture.Capture(doc, views, size, size, boxes);
        return new JsonObject
        {
            ["views"] = new JsonArray([.. views.Select(v => (JsonNode)v)]),
            ["images"] = new JsonArray([.. images.Select(i => (JsonNode)i)]),
        };
    }

    // ───────────────────────── helpers ─────────────────────────

    /// One undo record per tool call.
    static void Mutate(RhinoDoc doc, string description, Action action)
    {
        var record = doc.BeginUndoRecord("AI: " + description);
        try { action(); }
        finally
        {
            if (record != 0) doc.EndUndoRecord(record);
            doc.Views.Redraw();
        }
    }

    static IEnumerable<RhinoObject> SdfObjects(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings
        {
            HiddenObjects = true,
            LockedObjects = true,
            NormalObjects = true,
            IncludeLights = false,
            IncludeGrips = false,
        };
        return doc.Objects.GetObjectList(settings).Where(o => SdfUserText.ReadType(o.Attributes) != null);
    }

    /// Name if it identifies the object uniquely, else its GUID.
    static Dictionary<Guid, string> RefNames(List<RhinoObject> objects)
    {
        var counts = objects.Where(o => !string.IsNullOrEmpty(o.Attributes.Name))
            .GroupBy(o => o.Attributes.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return objects.ToDictionary(o => o.Id, o =>
            !string.IsNullOrEmpty(o.Attributes.Name) && counts[o.Attributes.Name] == 1 && !NodeLike().IsMatch(o.Attributes.Name)
                ? o.Attributes.Name
                : o.Id.ToString("D"));
    }

    static string RefOf(RhinoDoc doc, RhinoObject obj) => RefNames(SdfObjects(doc).ToList()).GetValueOrDefault(obj.Id) ?? obj.Id.ToString("D");

    static string OperandRef(RhinoDoc doc, FrepOperand o, Dictionary<Guid, string>? refs)
    {
        if (o.IsNode) return FrepTree.NodeName(o.NodeId);
        refs ??= RefNames(SdfObjects(doc).ToList());
        return refs.GetValueOrDefault(o.ObjectId) ?? $"(deleted {o.ObjectId:D})";
    }

    [GeneratedRegex(@"^[Nn]\d+$")]
    private static partial Regex NodeLike();

    /// Unique (case-insensitive) among F-rep objects; spaces → underscores; never looks like a node name.
    static string UniqueName(RhinoDoc doc, string requested)
    {
        var name = Regex.Replace(requested.Trim(), @"\s+", "_");
        if (name.Length == 0) name = "primitive";
        if (NodeLike().IsMatch(name) || Guid.TryParse(name, out _)) name = "obj_" + name;
        var taken = SdfObjects(doc).Select(o => o.Attributes.Name).Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{name}_{i}")) return $"{name}_{i}";
    }

    static FrepOperand Resolve(RhinoDoc doc, FrepTree tree, string r)
    {
        r = r.Trim();
        if (NodeLike().IsMatch(r))
        {
            var id = int.Parse(r.AsSpan(1));
            if (tree.Find(id) == null) throw new AiToolException($"no operation node '{r}'; call get_scene for current refs");
            return FrepOperand.Node(id);
        }
        return FrepOperand.Object(ResolveObject(doc, r).Id);
    }

    static RhinoObject ResolveObject(RhinoDoc doc, string r)
    {
        r = r.Trim();
        if (Guid.TryParse(r, out var g))
            return doc.Objects.FindId(g) is { IsDeleted: false } o && SdfUserText.ReadType(o.Attributes) != null
                ? o
                : throw new AiToolException($"no F-rep primitive with id {r}");
        var matches = SdfObjects(doc).Where(o => string.Equals(o.Attributes.Name, r, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AiToolException($"no primitive named '{r}'; call get_scene for current refs"),
            _ => throw new AiToolException($"{matches.Count} primitives are named '{r}'; use the GUID from get_scene"),
        };
    }

    /// The primitives an operand stands for (itself, or every leaf under a node).
    static IEnumerable<RhinoObject> Leaves(RhinoDoc doc, FrepTree tree, FrepOperand o)
    {
        var all = new HashSet<FrepOperand> { o };
        AddDescendants(tree, o, all);
        foreach (var x in all)
            if (!x.IsNode && doc.Objects.FindId(x.ObjectId) is { IsDeleted: false } obj)
                yield return obj;
    }

    static void AddDescendants(FrepTree tree, FrepOperand o, HashSet<FrepOperand> into)
    {
        if (!o.IsNode || tree.Find(o.NodeId) is not { } n) return;
        foreach (var c in n.Operands)
            if (into.Add(c)) AddDescendants(tree, c, into);
    }

    static FrepOp ParseOp(string op) => op.ToLowerInvariant() switch
    {
        "union" => FrepOp.Union,
        "subtract" or "difference" => FrepOp.Difference,
        "intersect" or "intersection" => FrepOp.Intersect,
        _ => throw new AiToolException($"unknown op '{op}'; use union, subtract or intersect"),
    };

    static string OpName(FrepOp op) => op switch
    {
        FrepOp.Union => "union",
        FrepOp.Difference => "subtract",
        FrepOp.Intersect => "intersect",
        _ => op.ToString(),
    };

    static string Str(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : throw new AiToolException($"missing '{key}' (string)");

    static List<string> Refs(JsonObject args, string key)
    {
        if (args[key] is JsonValue single && single.TryGetValue<string>(out var s)) return [s];
        if (args[key] is not JsonArray a || a.Count == 0) throw new AiToolException($"'{key}' must be a non-empty array of refs");
        return [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var r) ? r : throw new AiToolException($"'{key}' must contain strings"))];
    }

    /// Parses a tool's JSON arguments (the model sometimes sends "" for no arguments).
    public static JsonObject ParseArgs(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } e ? JsonNode.Parse(e.GetRawText())!.AsObject() : [];
}
