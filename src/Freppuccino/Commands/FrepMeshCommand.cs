using System.Diagnostics;
using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

namespace Freppuccino.Commands;

/// <summary>
/// Bakes every visible F-rep in the document into one mesh: the same primitives and CSG tree the
/// Freppuccino display mode shows, meshed with <see cref="DualContour"/>. Resolution sets
/// the finest cell size; the simplification tolerance merges cells where the surface is flat or gently
/// curved, so triangle density adapts to the shape.
/// The F-rep objects are left in place.
/// </summary>
[Guid("3f6b1c82-9a4e-4d57-b1c3-7e2a5d9f0c46")]
public class FrepMeshCommand : Command
{
    public const int MinResolution = 16, MaxResolution = 384;

    // Remembered between runs, like Rhino's own commands.
    static int resolution = 128;
    static double simplify = 0.03;

    public override string EnglishName => "FrepMesh";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Count == 0)
        {
            RhinoApp.WriteLine("FrepMesh: no F-rep objects in the document (create some with FSphere, FBox, ...).");
            return Result.Nothing;
        }
        var field = BuildField(scene, FrepTrees.Get(doc));
        if (!field.HasRoots)
        {
            RhinoApp.WriteLine("FrepMesh: nothing to mesh (every F-rep is hidden or cancelled out by the CSG tree).");
            return Result.Nothing;
        }

        var rc = RhinoGet.GetInteger("Mesh resolution (cells along the longest side)", true, ref resolution, MinResolution, MaxResolution);
        if (rc != Result.Success) return rc;
        rc = RhinoGet.GetNumber("Simplification tolerance in cells (0 = uniform density)", true, ref simplify, 0, 1);
        if (rc != Result.Success) return rc;

        var watch = Stopwatch.StartNew();
        var result = DualContour.Mesh(field, resolution, simplify);
        if (result.Triangles.Count == 0)
        {
            RhinoApp.WriteLine("FrepMesh: the F-reps produced no surface at this resolution.");
            return Result.Failure;
        }

        var mesh = new Mesh();
        mesh.Vertices.UseDoublePrecisionVertices = true;
        foreach (var p in result.Vertices) mesh.Vertices.Add(p.X, p.Y, p.Z);
        var t = result.Triangles;
        for (int i = 0; i < t.Count; i += 3) mesh.Faces.AddFace(t[i], t[i + 1], t[i + 2]);
        mesh.Faces.CullDegenerateFaces();
        mesh.Compact();
        bool closed = mesh.IsClosed;
        // Our own check, on the mesh as generated: it also catches pinched vertices, which an
        // edge-based test such as Mesh.IsManifold passes.
        var (badEdges, badVertices) = DualContour.Defects(result);
        mesh.Normals.ComputeNormals();
        // Split vertex normals across sharp edges so creases shade crisp, as Rhino's own meshes do.
        mesh.Unweld(RhinoMath.ToRadians(30), true);

        var attributes = doc.CreateDefaultAttributes();
        attributes.Name = "FrepMesh";
        var id = doc.Objects.AddMesh(mesh, attributes);
        if (id == Guid.Empty)
        {
            RhinoApp.WriteLine("FrepMesh: unable to add the mesh to the document.");
            return Result.Failure;
        }

        // The Freppuccino display mode draws the F-reps over unselected objects; selecting the mesh shows it.
        doc.Objects.UnselectAll();
        doc.Objects.Select(id);
        doc.Views.Redraw();
        RhinoApp.WriteLine(
            $"FrepMesh: {scene.Count} F-rep(s) -> {mesh.Faces.Count} triangles, cell size {result.CellSize:0.####} {doc.ModelUnitSystem}, " +
            $"{(closed ? "closed" : "OPEN")}, " +
            (badEdges + badVertices == 0 ? "manifold" : $"NON-MANIFOLD ({badEdges} bad edges, {badVertices} pinched vertices; try a higher resolution)") +
            $", {watch.ElapsedMilliseconds} ms.");
        return Result.Success;
    }

    /// The scene as a CPU field, following FrepShaderGen.Compile: nodes in creation order, missing
    /// leaves (hidden or deleted primitives, hidden nodes) dropped the same way, parentless primitives
    /// and nodes as roots.
    static FrepField BuildField(FrepSceneData scene, FrepTree tree)
    {
        var field = new FrepField();
        var index = new Dictionary<Guid, int>();
        for (int i = 0; i < scene.Primitives.Count; i++)
        {
            var (kind, frame, p) = scene.Primitives[i];
            var fieldKind = kind switch
            {
                SdKind.Sphere => FieldKind.Sphere,
                SdKind.Box => FieldKind.Box,
                SdKind.Torus => FieldKind.Torus,
                SdKind.Cylinder => FieldKind.Cylinder,
                SdKind.Cone => FieldKind.Cone,
                SdKind.Ellipsoid => FieldKind.Ellipsoid,
                SdKind.Pyramid => FieldKind.Pyramid,
                _ => throw new NotSupportedException($"FrepMesh cannot mesh {kind} yet"),
            };
            index[scene.Ids[i]] = field.AddPrimitive(fieldKind, V(frame.Origin), V(frame.XAxis), V(frame.YAxis), V(p));
        }

        var nodeRef = new Dictionary<int, FieldRef?>();
        FieldRef? Ref(FrepOperand o) => o.IsNode
            ? nodeRef.GetValueOrDefault(o.NodeId)
            : index.TryGetValue(o.ObjectId, out var i) ? FieldRef.Primitive(i) : null;
        foreach (var n in tree.Nodes)
        {
            nodeRef[n.Id] = null;
            if (n.Hidden) continue;
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

        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        for (int i = 0; i < scene.Primitives.Count; i++)
            if (!referenced.Contains(FrepOperand.Object(scene.Ids[i]))) field.AddRoot(FieldRef.Primitive(i));
        foreach (var n in tree.Nodes)
            if (nodeRef[n.Id] is { } r && !referenced.Contains(FrepOperand.Node(n.Id))) field.AddRoot(r);
        return field;
    }

    static V3 V(Point3d p) => new(p.X, p.Y, p.Z);
    static V3 V(Vector3d v) => new(v.X, v.Y, v.Z);
}
