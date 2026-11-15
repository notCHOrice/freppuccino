using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.DocObjects.Custom;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace Freppuccino.Selection;

/// The gumball carrier of a node root: an invisible mesh whose geometry is the root's world bounding box, so Rhino's
/// native gumball sits on the blend and moves/rotates/scales it (plan section 4). Selecting a node root = selecting its
/// carrier, so Rhino's own deselection (Esc, SelNone, empty click, commands) applies to roots too.
/// Never drawn, never picked by Rhino (the picker selects it), not an F-rep leaf, not baked.
[Guid("6b2f9d40-1c7e-4a58-9e3b-5d0a8c21f7e4")]
public sealed class FrepCarrier : CustomMeshObject
{
    public FrepCarrier() { }
    public FrepCarrier(Mesh m) : base(m) { }

    protected override void OnDraw(DrawEventArgs e) { }
    protected override IEnumerable<ObjRef> OnPick(PickContext context) => [];
}

/// Keeps one carrier per visible node root (created/updated/deleted on Idle, outside any undo record), and turns a
/// transform of carriers into the roots' matrices (one undo step with the command that moved them).
/// Carriers are transient: removed before a save and on open (a plain mesh tagged <see cref="Key"/> is a leftover), so
/// they never reach files or other plugins.
public static class FrepCarriers
{
    public const string Key = "FREP.Carrier";
    static bool dirty = true, saving;

    public static void Install()
    {
        RhinoApp.Idle += (_, _) =>
        {
            if (!dirty || saving || RhinoDoc.ActiveDoc is not { } doc || Rhino.Commands.Command.InCommand()) return;
            dirty = false;
            Sync(doc);
        };
        FrepTrees.Changed += _ => dirty = true;
        RhinoDoc.AddRhinoObject += (_, _) => dirty = true;
        RhinoDoc.DeleteRhinoObject += (_, _) => dirty = true;
        RhinoDoc.UndeleteRhinoObject += (_, _) => dirty = true;
        RhinoDoc.ReplaceRhinoObject += (_, _) => dirty = true;
        RhinoDoc.ModifyObjectAttributes += (_, _) => dirty = true;
        RhinoDoc.EndOpenDocument += (_, _) => dirty = true;
        RhinoDoc.BeginSaveDocument += (_, e) =>
        {
            saving = true;
            RemoveAll(e.Document);
        };
        RhinoDoc.EndSaveDocument += (_, _) =>
        {
            saving = false;
            dirty = true;
        };
    }

    public static int? NodeOf(RhinoObject? o) =>
        o?.Attributes.GetUserString(Key) is { } s && int.TryParse(s, out var id) ? id : null;

    public static bool IsCarrier(RhinoObject? o) => NodeOf(o) != null;

    /// The carrier of a node root (null if none yet).
    public static RhinoObject? Find(RhinoDoc doc, int nodeId) =>
        doc.Objects.GetObjectList(ObjectType.Mesh).FirstOrDefault(o => o is FrepCarrier && NodeOf(o) == nodeId);

    /// Creates the carrier now if missing (for a selection made before the next Idle).
    public static RhinoObject? Ensure(RhinoDoc doc, int nodeId)
    {
        if (Find(doc, nodeId) is { } c) return c;
        Sync(doc);
        return Find(doc, nodeId);
    }

    /// One carrier per visible node root, its geometry the root's box; leftovers (plain tagged meshes, duplicates,
    /// carriers of roots that are gone) removed. Selected carriers keep their geometry (the gumball shouldn't jump).
    static void Sync(RhinoDoc doc)
    {
        // Carriers are display helpers: adding or replacing them must not mark the document as changed.
        bool modified = doc.Modified;
        try { SyncCore(doc); }
        finally { doc.Modified = modified; }
    }

    static void SyncCore(RhinoDoc doc)
    {
        var scene = FrepScene.Get(doc);
        var roots = scene.Roots.Where(r => r.IsNode).Select(r => r.NodeId).ToHashSet();
        var seen = new HashSet<int>();
        var settings = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, ObjectTypeFilter = ObjectType.Mesh };
        foreach (var o in doc.Objects.GetObjectList(settings).ToList())
        {
            if (NodeOf(o) is not { } id) continue;
            if (o is not FrepCarrier || !roots.Contains(id) || !seen.Add(id)) { Delete(doc, o); continue; }
            if (o.IsSelected(false) > 0) continue;
            if (RootBox(scene, id) is { } box && !Near(o.Geometry.GetBoundingBox(true), box))
                Replace(doc, o, BoxMesh(box));
        }
        foreach (var id in roots)
        {
            if (seen.Contains(id) || RootBox(scene, id) is not { } box) continue;
            var a = new ObjectAttributes { Name = $"N{id} (blend)" };
            a.SetUserString(Key, id.ToString());
            var mesh = BoxMesh(box);
            var carrier = new FrepCarrier(mesh);
            doc.Objects.AddRhinoObject(carrier, mesh);
            doc.Objects.ModifyAttributes(carrier, a, true);
        }
    }

    static void RemoveAll(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, ObjectTypeFilter = ObjectType.Mesh };
        foreach (var o in doc.Objects.GetObjectList(settings).ToList())
            if (IsCarrier(o)) Delete(doc, o);
    }

    static void Delete(RhinoDoc doc, RhinoObject o)
    {
        // Outside an undo record this leaves no undo step.
        doc.Objects.Unlock(o.Id, true);
        doc.Objects.Delete(o.Id, true);
    }

    static void Replace(RhinoDoc doc, RhinoObject o, Mesh mesh)
    {
        var a = o.Attributes.Duplicate();
        doc.Objects.Delete(o.Id, true);
        var carrier = new FrepCarrier(mesh);
        doc.Objects.AddRhinoObject(carrier, mesh);
        doc.Objects.ModifyAttributes(carrier, a, true);
    }

    static bool Near(BoundingBox a, BoundingBox b)
    {
        double tol = 1e-6 * Math.Max(1, b.Diagonal.Length);
        return a.Min.DistanceTo(b.Min) <= tol && a.Max.DistanceTo(b.Max) <= tol;
    }

    /// World box of a node root's surface (the field's sublevel bounds), or null if it's empty.
    public static BoundingBox? RootBox(FrepSceneData scene, int nodeId)
    {
        var f = FrepFieldBuilder.Build(scene, [FrepOperand.Node(nodeId)], out var got);
        if (got.Count == 0) return null;
        var (lo, hi) = f.Bounds();
        if (!(lo.X <= hi.X && lo.Y <= hi.Y && lo.Z <= hi.Z)) return null;
        return new BoundingBox(lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z);
    }

    static Mesh BoxMesh(BoundingBox b)
    {
        // A degenerate (flat) box still needs volume for the gumball frame.
        double pad = 1e-6 * Math.Max(1, b.Diagonal.Length);
        b.Inflate(pad);
        return Mesh.CreateFromBox(b, 1, 1, 1);
    }

    // ─────────────── transforms ───────────────

    /// Called first from SdfTransformWatcher's BeforeTransformObjects. For each carrier in the transform: its root's
    /// matrix becomes X × M (tree edit, recorded in the running command's undo record), and the root's real objects
    /// (leaves whose primary instance is in this root, warp reference objects) follow as derived display: their
    /// geometry gets X, a reference's applied transforms A become X × A; SDF values and leaf placements don't change.
    /// Returns the leaf objects the caller must not rewrite (Rhino is moving them as part of this same transform).
    public static HashSet<Guid> OnBeforeTransform(RhinoDoc doc, RhinoObject[] objects, Transform x, bool copy)
    {
        var skip = new HashSet<Guid>();
        var carriers = objects.Where(o => o is FrepCarrier && NodeOf(o) != null).ToList();
        if (carriers.Count == 0) return skip;
        if (copy)
        {
            RhinoApp.WriteLine("Freppuccino: copying a blend isn't supported yet; the copy is discarded.");
            return skip;
        }
        var tree = FrepTrees.Get(doc);
        var moving = objects.Select(o => o.Id).ToHashSet();
        var scene = FrepScene.Get(doc);
        var (_, instances) = FrepInstances.Compute(tree, scene.Ids);
        var primary = new Dictionary<Guid, FrepInstance>();
        foreach (var i in instances) primary.TryAdd(i.Object, i);

        var roots = new List<int>();
        foreach (var c in carriers)
        {
            int id = NodeOf(c)!.Value;
            if (tree.Find(id) is not { } n || !Placement.IsUsable(x * n.M)) continue;
            tree = tree.WithM(id, x * n.M);
            roots.Add(id);
        }
        if (roots.Count == 0) return skip;
        FrepTrees.Set(doc, tree, "Transform blend");

        foreach (var id in roots)
        {
            var root = FrepOperand.Node(id);
            // Leaves whose real object stands for this root (first path).
            foreach (var (obj, inst) in primary)
            {
                if (inst.Root != root) continue;
                if (moving.Contains(obj)) { skip.Add(obj); continue; }
                if (doc.Objects.FindId(obj) is not { } o) continue;
                var g = o.Geometry.Duplicate();
                if (g.Transform(x)) doc.Objects.Replace(obj, g, true);
            }
            // Warp references in this root's subtree.
            foreach (var n in Subtree(tree, id))
            {
                if (!n.Op.IsWarp() || doc.Objects.FindId(n.Ref) is not { } r) continue;
                var a = r.Attributes.Duplicate();
                Placement.ReadApplied(a, out var applied);
                Placement.WriteApplied(a, x * applied);
                if (!moving.Contains(r.Id))
                {
                    var g = r.Geometry.Duplicate();
                    if (g.Transform(x)) doc.Objects.Replace(r.Id, g, true);
                }
                doc.Objects.ModifyAttributes(r.Id, a, true);
            }
        }
        return skip;
    }

    static IEnumerable<FrepNode> Subtree(FrepTree tree, int id)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>([id]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur) || tree.Find(cur) is not { } n) continue;
            yield return n;
            foreach (var o in n.Operands) if (o.IsNode) stack.Push(o.NodeId);
        }
    }
}
