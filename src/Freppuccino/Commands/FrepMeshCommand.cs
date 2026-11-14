using System.Diagnostics;
using System.Runtime.InteropServices;
using Freppuccino.Display;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

namespace Freppuccino.Commands;

/// <summary>
/// Bakes every visible F-rep in the document into one mesh: the same smooth union the Freppuccino
/// display mode shows (current FrepBlend distance), meshed with <see cref="DualContour"/>. Resolution sets
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
            RhinoApp.WriteLine("FrepMesh: no F-rep objects in the document (create some with FSphere / FBox).");
            return Result.Nothing;
        }

        var rc = RhinoGet.GetInteger("Mesh resolution (cells along the longest side)", true, ref resolution, MinResolution, MaxResolution);
        if (rc != Result.Success) return rc;
        rc = RhinoGet.GetNumber("Simplification tolerance in cells (0 = uniform density)", true, ref simplify, 0, 1);
        if (rc != Result.Success) return rc;

        var field = new FrepField { Blend = FrepSettings.Blend };
        foreach (var (frame, radius) in scene.Spheres) field.AddSphere(V(frame.Origin), radius);
        foreach (var (frame, half) in scene.Boxes) field.AddBox(V(frame.Origin), V(frame.XAxis), V(frame.YAxis), V(half));

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
        bool manifold = mesh.IsManifold(true, out _, out _);
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
            $"{(closed ? "closed" : "OPEN")}, {(manifold ? "manifold" : "NON-MANIFOLD")}, {watch.ElapsedMilliseconds} ms.");
        return Result.Success;
    }

    static V3 V(Point3d p) => new(p.X, p.Y, p.Z);
    static V3 V(Vector3d v) => new(v.X, v.Y, v.Z);
}
