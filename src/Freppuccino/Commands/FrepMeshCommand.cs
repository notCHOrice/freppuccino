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
/// curved, so triangle density adapts to the shape. Optionally the result is passed through Rhino's
/// QuadRemesh for an even quad layout.
/// The F-rep objects are left in place.
/// </summary>
[Guid("3f6b1c82-9a4e-4d57-b1c3-7e2a5d9f0c46")]
public class FrepMeshCommand : Command
{
    public const int MinResolution = 16, MaxResolution = 384;

    // Remembered between runs, like Rhino's own commands.
    static int resolution = 128;
    static double simplify = 0.03;
    static bool quads;

    public override string EnglishName => "FrepMesh";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Count == 0)
        {
            RhinoApp.WriteLine("FrepMesh: no F-rep objects in the document (create some with FSphere, FBox, ...).");
            return Result.Nothing;
        }
        var field = FrepFieldBuilder.Build(scene, null, out _);
        if (!field.HasRoots)
        {
            RhinoApp.WriteLine("FrepMesh: nothing to mesh (every F-rep is hidden or cancelled out by the CSG tree).");
            return Result.Nothing;
        }

        var rc = RhinoGet.GetInteger("Mesh resolution (cells along the longest side)", true, ref resolution, MinResolution, MaxResolution);
        if (rc != Result.Success) return rc;
        rc = RhinoGet.GetNumber("Simplification tolerance in cells (0 = uniform density)", true, ref simplify, 0, 1);
        if (rc != Result.Success) return rc;
        rc = RhinoGet.GetBool("Remesh to quads with Rhino's QuadRemesh (slower, and it reshapes the surface slightly)", true, "No", "Yes", ref quads);
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
        mesh.Normals.ComputeNormals();
        // Split vertex normals across sharp edges so creases shade crisp, as Rhino's own meshes do.
        mesh.Unweld(RhinoMath.ToRadians(30), true);

        // Optional second pass: Rhino's QuadRemesh lays out even quads over the triangles, keeping the
        // creases we just unwelded as hard edges. It works from the triangle mesh, so it cannot be more
        // accurate than it (volumes moved by up to 1.3% in testing), and it decides its own topology.
        string faces = $"{mesh.Faces.Count} triangles";
        if (quads)
        {
            var parameters = new QuadRemeshParameters
            {
                TargetQuadCount = Math.Max(200, mesh.Faces.Count / 2),
                AdaptiveQuadCount = true,
                AdaptiveSize = 50,
                DetectHardEdges = true,
            };
            var remeshed = mesh.QuadRemesh(parameters);
            if (remeshed != null && remeshed.Faces.Count > 0)
            {
                faces += $" -> {remeshed.Faces.Count} faces ({remeshed.Faces.QuadCount} quads) after QuadRemesh";
                mesh = remeshed;
            }
            else RhinoApp.WriteLine("FrepMesh: QuadRemesh produced nothing; keeping the triangle mesh.");
        }

        // Our own check on the mesh being added, whichever pass produced it: it also catches pinched
        // vertices, which an edge-based test such as Mesh.IsManifold passes.
        bool closed = mesh.IsClosed;
        var (badEdges, badVertices) = Defects(mesh);
        string quality = $"{(closed ? "closed" : "OPEN")}, " +
            (badEdges + badVertices == 0 ? "manifold" : $"NON-MANIFOLD ({badEdges} bad edges, {badVertices} pinched vertices; try a higher resolution)");

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
            $"FrepMesh: {scene.Count} F-rep(s) -> {faces}, cell size {result.CellSize:0.####} {doc.ModelUnitSystem}, " +
            $"{quality}, {watch.ElapsedMilliseconds} ms.");
        return Result.Success;
    }

    /// <see cref="DualContour.Defects"/> for a Rhino mesh: faces are read through the mesh's topology, so
    /// vertices that were unwelded for shading count as one, and quads are split in two.
    static (int Edges, int Vertices) Defects(Mesh mesh)
    {
        var topology = mesh.TopologyVertices;
        var vertices = new List<V3>(topology.Count);
        for (int i = 0; i < topology.Count; i++) vertices.Add(new V3(topology[i].X, topology[i].Y, topology[i].Z));
        var triangles = new List<int>(mesh.Faces.Count * 3);
        foreach (var face in mesh.Faces)
        {
            int a = topology.TopologyVertexIndex(face.A), b = topology.TopologyVertexIndex(face.B), c = topology.TopologyVertexIndex(face.C);
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            if (!face.IsQuad) continue;
            triangles.Add(a); triangles.Add(c); triangles.Add(topology.TopologyVertexIndex(face.D));
        }
        return DualContour.Defects(new DualContour.Result(vertices, triangles, 0, 0, 0, 0));
    }

    static V3 V(Point3d p) => new(p.X, p.Y, p.Z);
    static V3 V(Vector3d v) => new(v.X, v.Y, v.Z);
}
