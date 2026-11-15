using Freppuccino.Csg;
using Freppuccino.Meshing;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// The scene as a CPU field, following FrepShaderGen.Compile: nodes in creation order, missing leaves (hidden or
/// deleted primitives, hidden nodes) dropped the same way, parentless primitives and nodes as roots.
/// Warps: hidden = missing, missing child = empty, ref gone = child unwarped. Shared by FrepMesh and picking.
public static class FrepFieldBuilder
{
    /// <paramref name="only"/>: restrict the field's roots to these operands (each must be a root of the tree; others are
    /// ignored). Null = every root. <paramref name="roots"/>: the operands that ended up as roots (a root whose result is
    /// empty is left out).
    public static FrepField Build(FrepSceneData scene, IReadOnlyCollection<FrepOperand>? only, out List<FrepOperand> roots)
    {
        var tree = scene.Tree;
        var field = new FrepField();
        var index = new Dictionary<Guid, int>();
        for (int i = 0; i < scene.Primitives.Count; i++) index[scene.Ids[i]] = Add(field, scene.Primitives[i]);

        var warps = new Dictionary<int, SdWarp>();
        for (int i = 0; i < scene.Warps.Count; i++) warps[scene.WarpIds[i]] = scene.Warps[i];

        var nodeRef = new Dictionary<int, FieldRef?>();
        FieldRef? Ref(FrepOperand o) => o.IsNode
            ? nodeRef.GetValueOrDefault(o.NodeId)
            : index.TryGetValue(o.ObjectId, out var i) ? FieldRef.Primitive(i) : null;
        foreach (var n in tree.Nodes)
        {
            nodeRef[n.Id] = null;
            if (n.Hidden) continue;
            if (n.Op.IsWarp())
            {
                if (n.Operands.Length == 0 || Ref(n.Operands[0]) is not { } child) continue;
                nodeRef[n.Id] = warps.TryGetValue(n.Id, out var w)
                    ? FieldRef.Node(field.AddWarp(n.Op switch { FrepOp.Twist => FieldOp.Twist, FrepOp.Bend => FieldOp.Bend, _ => FieldOp.Repeat },
                        FrepWarpMath.Rows(w), child))
                    : child;
                continue;
            }
            var terms = n.Operands.Select(Ref).ToList();
            // Union skips missing operands; intersect with one missing is empty; difference needs its base.
            bool empty = n.Op switch
            {
                FrepOp.Union => false,
                FrepOp.Intersect => terms.Contains(null),
                _ => terms.Count == 0 || terms[0] == null,
            };
            var live = terms.OfType<FieldRef>().ToList();
            if (empty || live.Count == 0) continue;
            var op = n.Op switch { FrepOp.Union => FieldOp.Union, FrepOp.Intersect => FieldOp.Intersect, _ => FieldOp.Difference };
            nodeRef[n.Id] = FieldRef.Node(field.AddNode(op, n.K, live));
        }

        roots = [];
        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        for (int i = 0; i < scene.Primitives.Count; i++)
        {
            var o = FrepOperand.Object(scene.Ids[i]);
            if (referenced.Contains(o) || (only != null && !only.Contains(o))) continue;
            field.AddRoot(FieldRef.Primitive(i));
            roots.Add(o);
        }
        foreach (var n in tree.Nodes)
        {
            var o = FrepOperand.Node(n.Id);
            if (nodeRef[n.Id] is not { } r || referenced.Contains(o) || (only != null && !only.Contains(o))) continue;
            field.AddRoot(r);
            roots.Add(o);
        }
        return field;
    }

    /// A field of one primitive (a control instance's shape, for picking).
    public static FrepField Single(SdPrimitive prim)
    {
        var field = new FrepField();
        field.AddRoot(FieldRef.Primitive(Add(field, prim)));
        return field;
    }

    static int Add(FrepField field, SdPrimitive prim)
    {
        var (kind, frame, p) = prim;
        V4[]? place = null;
        if (prim.Place is { } pl && Placement.TryRows(pl, out var rows)) place = rows;
        var fieldKind = kind switch
        {
            SdKind.Sphere => FieldKind.Sphere,
            SdKind.Box => FieldKind.Box,
            SdKind.Torus => FieldKind.Torus,
            SdKind.Cylinder => FieldKind.Cylinder,
            SdKind.Cone => FieldKind.Cone,
            SdKind.Ellipsoid => FieldKind.Ellipsoid,
            SdKind.Pyramid => FieldKind.Pyramid,
            _ => throw new NotSupportedException($"no CPU field for {kind} yet"),
        };
        return field.AddPrimitive(fieldKind, V(frame.Origin), V(frame.XAxis), V(frame.YAxis), V(p), place);
    }

    static V3 V(Point3d p) => new(p.X, p.Y, p.Z);
    static V3 V(Vector3d v) => new(v.X, v.Y, v.Z);
}
