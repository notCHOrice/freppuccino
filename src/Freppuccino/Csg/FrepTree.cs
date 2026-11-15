using System.Collections.Immutable;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.FileIO;

namespace Freppuccino.Csg;

/// Union/Intersect/Difference: CSG over ≥ 2 operands, blend K. Twist/Bend/Repeat: domain warps of one operand,
/// shaped by a referenced Rhino object (<see cref="FrepNode.Ref"/>: line for Twist/Bend, box for Repeat) + <see cref="FrepNode.Params"/>.
public enum FrepOp { Union = 0, Intersect = 1, Difference = 2, Twist = 3, Bend = 4, Repeat = 5 }

public static class FrepOps
{
    public static bool IsWarp(this FrepOp op) => op >= FrepOp.Twist;

    /// Param names per warp op (index = slot in FrepNode.Params). Angles in degrees.
    ///   Twist  angle: total twist from line start to end (linear, continues past the ends)
    ///   Bend   angle: total bend over the line's length (straight past the ends); direction: bend direction around the line
    ///   Repeat x/y/z: spacing along the box axes (≤ 0 = no repetition on that axis)
    public static IReadOnlyList<string> ParamNames(this FrepOp op) => op switch
    {
        FrepOp.Twist => ["angle"],
        FrepOp.Bend => ["angle", "direction"],
        FrepOp.Repeat => ["x", "y", "z"],
        _ => [],
    };
}

/// Leaf (Rhino object id of an SDF primitive) or a reference to another node.
public readonly record struct FrepOperand(int NodeId, Guid ObjectId)
{
    public bool IsNode => ObjectId == Guid.Empty;
    public static FrepOperand Node(int id) => new(id, Guid.Empty);
    public static FrepOperand Object(Guid id) => new(0, id);
}

/// Difference: Operands[0] − Operands[1..]. K = smooth blend distance (model units, 0 = hard).
/// Hidden: dropped like a hidden primitive (a missing leaf for its parents; not drawn as a root).
/// Warps: Operands = [child], Ref = line/box object, Params per <see cref="FrepOps.ParamNames"/>; K unused.
/// Matrix: optional affine transform M of the node's whole result (12 numbers, top three rows of the 4x4, row-major;
/// default = identity): field = result(M⁻¹ p) · s(M), s = smallest singular value. A root's gumball transform composes
/// into it ("transform the blend as one object"); it stays with the node if the node later becomes an operand.
public sealed record FrepNode(int Id, FrepOp Op, double K, ImmutableArray<FrepOperand> Operands, bool Hidden = false,
    Guid Ref = default, ImmutableArray<double> Params = default, ImmutableArray<double> Matrix = default)
{
    public string Name => FrepTree.NodeName(Id);
    public double Param(int i) => !Params.IsDefault && i < Params.Length ? Params[i] : 0;

    public bool HasMatrix => !Matrix.IsDefault && Matrix.Length == 12;

    /// M (identity if none).
    public Rhino.Geometry.Transform M
    {
        get
        {
            var t = Rhino.Geometry.Transform.Identity;
            if (!HasMatrix) return t;
            for (int i = 0; i < 12; i++) t[i / 4, i % 4] = Matrix[i];
            return t;
        }
    }

    /// Identity clears the matrix.
    public FrepNode WithM(Rhino.Geometry.Transform m)
    {
        if (Scene.Placement.IsIdentity(m)) return this with { Matrix = default };
        var a = new double[12];
        for (int i = 0; i < 12; i++) a[i] = m[i / 4, i % 4];
        return this with { Matrix = [.. a] };
    }
}

/// Immutable CSG DAG. Nodes in creation order, which is topological: operands always predate their node, and
/// operands are never edited, so no cycles. Parentless prims/nodes are hard-min'd at the root.
public sealed record FrepTree(ImmutableList<FrepNode> Nodes, int NextId)
{
    public static readonly FrepTree Empty = new(ImmutableList<FrepNode>.Empty, 1);

    public static string NodeName(int id) => "N" + id;

    /// "N3", "n3" or "3".
    public static bool TryParseName(string? text, out int id)
    {
        id = 0;
        var s = text?.Trim() ?? "";
        if (s.StartsWith('N') || s.StartsWith('n')) s = s[1..];
        return int.TryParse(s, out id);
    }

    public FrepNode? Find(int id) => Nodes.Find(n => n.Id == id);

    public bool IsReferenced(FrepOperand o) => Nodes.Any(n => n.Operands.Contains(o));

    public (FrepTree Tree, FrepNode Node) Add(FrepOp op, double k, IEnumerable<FrepOperand> operands)
    {
        var node = new FrepNode(NextId, op, k, [.. operands]);
        return (new FrepTree(Nodes.Add(node), NextId + 1), node);
    }

    public (FrepTree Tree, FrepNode Node) AddWarp(FrepOp op, FrepOperand child, Guid reference, IEnumerable<double> parameters)
    {
        var node = new FrepNode(NextId, op, 0, [child], Ref: reference, Params: [.. parameters]);
        return (new FrepTree(Nodes.Add(node), NextId + 1), node);
    }

    public FrepTree WithParam(int id, int index, double value) => Edit(id, n =>
    {
        var p = (n.Params.IsDefault ? [] : n.Params).ToBuilder();
        while (p.Count <= index) p.Add(0);
        p[index] = value;
        return n with { Params = p.ToImmutable() };
    });

    public FrepTree WithK(int id, double k) => Edit(id, n => n with { K = k });

    public FrepTree WithOp(int id, FrepOp op, double k) => Edit(id, n => n with { Op = op, K = k });

    public FrepTree WithHidden(int id, bool hidden) => Edit(id, n => n with { Hidden = hidden });

    public FrepTree WithM(int id, Rhino.Geometry.Transform m) => Edit(id, n => n.WithM(m));

    FrepTree Edit(int id, Func<FrepNode, FrepNode> f)
    {
        var i = Nodes.FindIndex(n => n.Id == id);
        return i < 0 ? this : this with { Nodes = Nodes.SetItem(i, f(Nodes[i])) };
    }

    /// Caller ensures nothing references it.
    public FrepTree Remove(int id) => this with { Nodes = Nodes.RemoveAll(n => n.Id == id) };

    const int Major = 1, Minor = 3; // 1.1: per-node Hidden; 1.2: Ref + Params; 1.3: Matrix

    public void Write(BinaryArchiveWriter w)
    {
        w.Write3dmChunkVersion(Major, Minor);
        w.WriteInt(NextId);
        w.WriteInt(Nodes.Count);
        foreach (var n in Nodes)
        {
            w.WriteInt(n.Id);
            w.WriteInt((int)n.Op);
            w.WriteDouble(n.K);
            w.WriteInt(n.Operands.Length);
            foreach (var o in n.Operands)
            {
                w.WriteInt(o.NodeId);
                w.WriteGuid(o.ObjectId);
            }
        }
        foreach (var n in Nodes) w.WriteBool(n.Hidden);
        foreach (var n in Nodes)
        {
            w.WriteGuid(n.Ref);
            var p = n.Params.IsDefault ? [] : n.Params;
            w.WriteInt(p.Length);
            foreach (var v in p) w.WriteDouble(v);
        }
        foreach (var n in Nodes)
        {
            var m = n.HasMatrix ? n.Matrix : [];
            w.WriteInt(m.Length);
            foreach (var v in m) w.WriteDouble(v);
        }
    }

    public static FrepTree Read(BinaryArchiveReader r)
    {
        r.Read3dmChunkVersion(out var major, out var minor);
        if (major != Major) throw new InvalidDataException($"F-rep tree version {major} unsupported");
        int nextId = r.ReadInt();
        int count = r.ReadInt();
        var nodes = ImmutableList.CreateBuilder<FrepNode>();
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt();
            var op = (FrepOp)r.ReadInt();
            double k = r.ReadDouble();
            int nOps = r.ReadInt();
            var ops = ImmutableArray.CreateBuilder<FrepOperand>(nOps);
            for (int j = 0; j < nOps; j++)
                ops.Add(new FrepOperand(r.ReadInt(), r.ReadGuid()));
            nodes.Add(new FrepNode(id, op, k, ops.MoveToImmutable()));
        }
        if (minor >= 1)
            for (int i = 0; i < count; i++)
                nodes[i] = nodes[i] with { Hidden = r.ReadBool() };
        if (minor >= 2)
            for (int i = 0; i < count; i++)
            {
                var reference = r.ReadGuid();
                var p = new double[r.ReadInt()];
                for (int j = 0; j < p.Length; j++) p[j] = r.ReadDouble();
                nodes[i] = nodes[i] with { Ref = reference, Params = [.. p] };
            }
        if (minor >= 3)
            for (int i = 0; i < count; i++)
            {
                var m = new double[r.ReadInt()];
                for (int j = 0; j < m.Length; j++) m[j] = r.ReadDouble();
                if (m.Length == 12) nodes[i] = nodes[i] with { Matrix = [.. m] };
            }
        return new FrepTree(nodes.ToImmutable(), nextId);
    }
}

/// Per-document trees (persisted via plugin Write/ReadDocument). All edits go through <see cref="Set"/> (or
/// <see cref="Preview"/> + <see cref="Commit"/>) so they're undoable.
public static class FrepTrees
{
    static readonly Dictionary<uint, FrepTree> trees = [];
    /// Open live-edit gesture per doc: the tree to undo back to.
    static readonly Dictionary<uint, (FrepTree Before, string Description)> previews = [];

    /// Raised on the UI thread after any change (edit, undo/redo, file open).
    public static event Action<RhinoDoc>? Changed;

    public static void Install()
    {
        RhinoDoc.CloseDocument += (_, e) =>
        {
            trees.Remove(e.Document.RuntimeSerialNumber);
            previews.Remove(e.Document.RuntimeSerialNumber);
        };
        // Open (not merge/import) replaces the doc's contents; ReadDocument then fills the tree back in.
        RhinoDoc.BeginOpenDocument += (_, e) =>
        {
            if (e.Merge || e.Reference) return;
            previews.Remove(e.Document.RuntimeSerialNumber);
            Apply(e.Document, FrepTree.Empty);
        };
        // Any command (incl. Undo/Redo) ends a live gesture whose commit never came.
        Command.BeginCommand += (_, _) => CommitAll();
    }

    public static FrepTree Get(RhinoDoc doc) => trees.GetValueOrDefault(doc.RuntimeSerialNumber, FrepTree.Empty);

    /// Replaces the doc's tree with an undo step. Opens its own undo record if none is active (e.g. panel edits).
    public static void Set(RhinoDoc doc, FrepTree tree, string description)
    {
        Commit(doc);
        var old = Get(doc);
        if (ReferenceEquals(old, tree)) return;
        Record(doc, old, description);
        Apply(doc, tree);
    }

    /// Live edit (slider drag): applies without undo; the first call of a gesture remembers the tree to undo to.
    /// A different description starts a new gesture (commits the open one). End with <see cref="Commit"/>.
    public static void Preview(RhinoDoc doc, FrepTree tree, string description)
    {
        var key = doc.RuntimeSerialNumber;
        if (previews.TryGetValue(key, out var p) && p.Description != description) Commit(doc);
        if (!previews.ContainsKey(key)) previews[key] = (Get(doc), description);
        if (!ReferenceEquals(Get(doc), tree)) Apply(doc, tree);
    }

    /// Ends the doc's open gesture as one undo step (none if it ended where it started).
    public static void Commit(RhinoDoc doc)
    {
        if (!previews.Remove(doc.RuntimeSerialNumber, out var p)) return;
        var now = Get(doc);
        if (p.Before.NextId == now.NextId && p.Before.Nodes.SequenceEqual(now.Nodes)) return;
        Record(doc, p.Before, p.Description);
    }

    public static void CommitAll()
    {
        foreach (var serial in previews.Keys.ToList())
            if (RhinoDoc.FromRuntimeSerialNumber(serial) is { } doc) Commit(doc);
            else previews.Remove(serial);
    }

    /// Undo step back to <paramref name="old"/>; the current tree is the redo state.
    static void Record(RhinoDoc doc, FrepTree old, string description)
    {
        uint record = doc.UndoRecordingIsActive ? 0 : doc.BeginUndoRecord(description);
        doc.AddCustomUndoEvent(description, OnUndo, old);
        if (record != 0) doc.EndUndoRecord(record);
    }

    /// Swap in the tagged tree and register the inverse (Rhino's custom undo pattern: redo is registered from the undo handler).
    static void OnUndo(object? sender, CustomUndoEventArgs e)
    {
        var doc = e.Document;
        var current = Get(doc);
        doc.AddCustomUndoEvent(e.ActionDescription, OnUndo, current);
        Apply(doc, (FrepTree)e.Tag);
    }

    /// No undo; for file load and previews.
    public static void Apply(RhinoDoc doc, FrepTree tree)
    {
        trees[doc.RuntimeSerialNumber] = tree;
        FrepScene.MarkDirty();
        Changed?.Invoke(doc);
        doc.Views.Redraw();
    }
}
