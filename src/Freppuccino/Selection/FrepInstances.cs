using System.Collections.Immutable;
using Freppuccino.Csg;
using Rhino.Geometry;

namespace Freppuccino.Selection;

/// One control instance: a leaf object reached from <see cref="Root"/> through the nodes in <see cref="Path"/>
/// (root node first, the leaf's parent last; empty when the leaf is itself a parentless root).
/// T: product of the Transform nodes on the path (identity until root transforms exist). The instance shows the
/// leaf's pre-warp shape: warps on the path don't deform it.
public sealed record FrepInstance(FrepOperand Root, ImmutableArray<int> Path, Guid Object, Transform T)
{
    public bool SamePath(FrepInstance o) => Root == o.Root && Object == o.Object && Path.SequenceEqual(o.Path);
}

/// Roots and control instances of a tree, in a stable order: parentless primitives (in <paramref name="leaves"/> order),
/// then parentless nodes in creation order; operands depth-first in order. An object's primary instance is its first.
public static class FrepInstances
{
    /// <paramref name="leaves"/>: the primitive objects that exist and are visible (others are missing leaves).
    /// Hidden nodes and their subtrees are skipped, like the shader. Warp nodes are passed through (their child is an instance).
    public static (List<FrepOperand> Roots, List<FrepInstance> Instances) Compute(FrepTree tree, IReadOnlyList<Guid> leaves)
    {
        var live = leaves.ToHashSet();
        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        var roots = new List<FrepOperand>();
        var instances = new List<FrepInstance>();

        foreach (var id in leaves)
        {
            var o = FrepOperand.Object(id);
            if (referenced.Contains(o)) continue;
            roots.Add(o);
            instances.Add(new FrepInstance(o, [], id, Transform.Identity));
        }

        foreach (var n in tree.Nodes)
        {
            var o = FrepOperand.Node(n.Id);
            if (referenced.Contains(o) || n.Hidden) continue;
            roots.Add(o);
            var path = ImmutableArray.CreateBuilder<int>();
            void Walk(FrepNode node)
            {
                path.Add(node.Id);
                foreach (var c in node.Operands)
                {
                    if (c.IsNode)
                    {
                        if (tree.Find(c.NodeId) is { Hidden: false } child) Walk(child);
                    }
                    else if (live.Contains(c.ObjectId))
                        instances.Add(new FrepInstance(o, path.ToImmutable(), c.ObjectId, Transform.Identity));
                }
                path.RemoveAt(path.Count - 1);
            }
            Walk(n);
        }
        return (roots, instances);
    }

    /// The root a node or object belongs to, if exactly one root reaches it (null if none or several).
    public static FrepOperand? OwningRoot(FrepTree tree, IReadOnlyList<Guid> leaves, Guid obj)
    {
        var roots = Compute(tree, leaves).Instances.Where(i => i.Object == obj).Select(i => i.Root).Distinct().ToList();
        return roots.Count == 1 ? roots[0] : null;
    }
}
