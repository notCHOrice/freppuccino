using System.Text.Json;
using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Sdf;
using Rhino;
using Rhino.DocObjects;

namespace Freppuccino.Ui;

/// <summary>
/// Glue between Rhino and the side panel page: pushes scene / selection snapshots when they change
/// (coalesced on Idle) and handles requests from the page.
///
/// Scene = SDF primitives (Rhino objects) + the doc's CSG tree (<see cref="FrepTrees"/>). Operation nodes aren't
/// Rhino objects, so their selection lives here (<see cref="TakeSelectedNodes"/>).
/// Stubbed: <see cref="SetParamRequest"/>.
/// </summary>
public sealed class UiController : IDisposable
{
    /// Commands the page may run. Keep this a whitelist: the page must not be able to run arbitrary scripts.
    static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase) { "FBox", "FSphere", "Freppuccino" };

    /// Selected operation nodes per doc (runtime serial), in pick order.
    static readonly Dictionary<uint, List<int>> selectedNodes = [];

    /// Selected node ids; clears them (consumed like Rhino preselection by FUnion/FIntersect).
    public static IReadOnlyList<int> TakeSelectedNodes(RhinoDoc doc)
    {
        if (!selectedNodes.Remove(doc.RuntimeSerialNumber, out var ids)) return [];
        NodeSelectionChanged?.Invoke();
        return ids;
    }

    static event Action? NodeSelectionChanged;

    static List<int> NodesOf(RhinoDoc doc) =>
        selectedNodes.TryGetValue(doc.RuntimeSerialNumber, out var l) ? l : selectedNodes[doc.RuntimeSerialNumber] = [];

    readonly WebViewHost web;
    bool sceneDirty, selectionDirty;
    uint sentDocSerial;

    public UiController(WebViewHost web)
    {
        this.web = web;
        web.MessageReceived += Handle;

        RhinoDoc.SelectObjects += OnSelectionChanged;
        RhinoDoc.DeselectObjects += OnSelectionChanged;
        RhinoDoc.DeselectAllObjects += OnDeselectAll;
        RhinoDoc.AddRhinoObject += OnObjectChanged;
        RhinoDoc.DeleteRhinoObject += OnObjectChanged;
        RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
        RhinoDoc.ReplaceRhinoObject += OnReplaceObject;
        RhinoDoc.ModifyObjectAttributes += OnModifyAttributes;
        FrepTrees.Changed += OnTreeChanged;
        NodeSelectionChanged += OnNodeSelectionChanged;
        RhinoApp.Idle += OnIdle;
    }

    public void Dispose()
    {
        web.MessageReceived -= Handle;
        RhinoDoc.SelectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectAllObjects -= OnDeselectAll;
        RhinoDoc.AddRhinoObject -= OnObjectChanged;
        RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
        RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
        RhinoDoc.ReplaceRhinoObject -= OnReplaceObject;
        RhinoDoc.ModifyObjectAttributes -= OnModifyAttributes;
        FrepTrees.Changed -= OnTreeChanged;
        NodeSelectionChanged -= OnNodeSelectionChanged;
        RhinoApp.Idle -= OnIdle;
    }

    // ─────────────── Rhino → page ───────────────

    void OnSelectionChanged(object? sender, RhinoObjectSelectionEventArgs e) => selectionDirty = true;
    void OnObjectChanged(object? sender, RhinoObjectEventArgs e) => sceneDirty = selectionDirty = true;
    void OnReplaceObject(object? sender, RhinoReplaceObjectEventArgs e) => sceneDirty = true;
    void OnModifyAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e) => sceneDirty = true;
    void OnNodeSelectionChanged() => selectionDirty = true;

    // Deselect-all (Esc, click on empty space, end of an F-CSG command) also clears node selection.
    void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e)
    {
        selectedNodes.Remove(e.Document.RuntimeSerialNumber);
        selectionDirty = true;
    }

    void OnTreeChanged(RhinoDoc doc)
    {
        // Drop selected nodes that no longer exist (undo, delete).
        if (selectedNodes.TryGetValue(doc.RuntimeSerialNumber, out var ids))
            ids.RemoveAll(id => FrepTrees.Get(doc).Find(id) == null);
        sceneDirty = selectionDirty = true;
    }

    void OnIdle(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;

        if (doc.RuntimeSerialNumber != sentDocSerial)
        {
            SendState(doc);
            return;
        }
        if (sceneDirty)
        {
            sceneDirty = false;
            web.Send(new SceneMessage(BuildScene(doc)));
        }
        if (selectionDirty)
        {
            selectionDirty = false;
            web.Send(new SelectionMessage(SelectedIds(doc)));
        }
    }

    void SendState(RhinoDoc doc)
    {
        sentDocSerial = doc.RuntimeSerialNumber;
        sceneDirty = selectionDirty = false;
        var theme = Rhino.Runtime.HostUtils.RunningInDarkMode ? "dark" : "light";
        web.Send(new StateMessage(BuildScene(doc), SelectedIds(doc), new UiSettings(doc.ModelUnitSystem.ToString()), theme));
    }

    static string NodeKey(int id) => FrepTree.NodeName(id);
    static string ObjectKey(Guid id) => id.ToString("D");

    /// "N3" → 3 if that node exists.
    static bool TryNode(FrepTree tree, string key, out int id)
    {
        if (key.StartsWith('N') && int.TryParse(key.AsSpan(1), out id) && tree.Find(id) != null) return true;
        id = 0;
        return false;
    }

    static IEnumerable<int> Nodes(FrepTree tree, IEnumerable<string> keys) =>
        keys.Select(k => TryNode(tree, k, out var n) ? n : 0).Where(n => n != 0);

    static FrepOperand? ParseOperand(FrepTree tree, string key) =>
        Guid.TryParse(key, out var g) ? FrepOperand.Object(g)
        : TryNode(tree, key, out var n) ? FrepOperand.Node(n)
        : null;

    static string ToKind(FrepOp op) => op switch
    {
        FrepOp.Union => OperationKind.Union,
        FrepOp.Intersect => OperationKind.Intersect,
        FrepOp.Difference => OperationKind.Subtract,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    static FrepOp? ParseKind(string kind) => kind switch
    {
        OperationKind.Union => FrepOp.Union,
        OperationKind.Intersect => FrepOp.Intersect,
        OperationKind.Subtract => FrepOp.Difference,
        _ => null,
    };

    /// Roots: nodes nothing references (creation order), then unreferenced primitives (document order).
    /// Operands nest under their node; shared operands repeat under each parent.
    static List<SceneNode> BuildScene(RhinoDoc doc)
    {
        var tree = FrepTrees.Get(doc);
        var built = new Dictionary<int, SceneNode>();

        SceneNode Node(FrepNode n)
        {
            if (built.TryGetValue(n.Id, out var s)) return s;
            var children = n.Operands.Select(o => o.IsNode
                ? (tree.Find(o.NodeId) is { } child ? Node(child) : Missing(NodeKey(o.NodeId)))
                : Primitive(doc, o.ObjectId)).ToList();
            return built[n.Id] = new SceneNode(
                Id: NodeKey(n.Id), Kind: SceneNodeKind.Operation, Name: n.Name, Visible: true, Locked: false,
                Op: ToKind(n.Op), K: n.K, Children: children);
        }

        var roots = tree.Nodes.Where(n => !tree.IsReferenced(FrepOperand.Node(n.Id))).Select(Node).ToList();

        var settings = new ObjectEnumeratorSettings
        {
            HiddenObjects = true,
            LockedObjects = true,
            NormalObjects = true,
            IncludeLights = false,
            IncludeGrips = false,
        };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (SdfUserText.ReadType(obj.Attributes) == null || tree.IsReferenced(FrepOperand.Object(obj.Id))) continue;
            roots.Add(Primitive(doc, obj));
        }
        return roots;
    }

    static SceneNode Missing(string id) =>
        new(id, SceneNodeKind.Primitive, "(deleted)", Visible: false, Locked: false, Missing: true);

    static SceneNode Primitive(RhinoDoc doc, Guid id) =>
        doc.Objects.FindId(id) is { IsDeleted: false } obj ? Primitive(doc, obj) : Missing(ObjectKey(id));

    static SceneNode Primitive(RhinoDoc doc, RhinoObject obj)
    {
        var attributes = obj.Attributes;
        return new SceneNode(
            Id: ObjectKey(obj.Id),
            Kind: SceneNodeKind.Primitive,
            Name: string.IsNullOrEmpty(attributes.Name) ? null : attributes.Name,
            Visible: !obj.IsHidden,
            Locked: obj.IsLocked,
            SdfType: SdfUserText.ReadType(attributes),
            Params: ReadParams(attributes));
    }

    static readonly HashSet<string> HeaderKeys = new(StringComparer.Ordinal)
    {
        SdfUserText.VersionKey, SdfUserText.TypeKey, SdfUserText.UnitsKey,
        SdfUserText.OriginKey, SdfUserText.XAxisKey, SdfUserText.YAxisKey,
    };

    /// Every numeric SDF.&lt;param&gt; (SDF.s, SDF.b.x, ...), keyed without the prefix. Generic, so new primitives just show up.
    static Dictionary<string, double> ReadParams(ObjectAttributes attributes)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var strings = attributes.GetUserStrings();
        foreach (string? key in strings.AllKeys)
        {
            if (key == null || !key.StartsWith(SdfUserText.Prefix, StringComparison.Ordinal) || HeaderKeys.Contains(key)) continue;
            var name = key[SdfUserText.Prefix.Length..];
            if (SdfUserText.TryReadFloat(attributes, name, out var v)) result[name] = v;
        }
        return result;
    }

    static List<string> SelectedIds(RhinoDoc doc) =>
        doc.Objects.GetSelectedObjects(false, false)
            .Where(o => SdfUserText.ReadType(o.Attributes) != null)
            .Select(o => ObjectKey(o.Id))
            .Concat(selectedNodes.GetValueOrDefault(doc.RuntimeSerialNumber, []).Select(NodeKey))
            .ToList();

    // ─────────────── page → Rhino ───────────────

    void Handle(string type, JsonElement message)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null) return;
        try
        {
            switch (type)
            {
                case "ready":
                    SendState(doc);
                    break;
                case "select":
                    Select(doc, Parse<SelectRequest>(message));
                    break;
                case "setVisible":
                    SetVisible(doc, Parse<SetVisibleRequest>(message));
                    break;
                case "runCommand":
                    RunCommand(Parse<RunCommandRequest>(message));
                    break;
                case "setParam":
                    // TODO: write SDF.<name> (SdfUserText.WriteFloat) on a copy of the attributes and
                    // doc.Objects.ModifyAttributes(...) inside an undo record; regenerate the Rhino proxy geometry.
                    NotImplemented("Editing primitive parameters");
                    break;
                case "applyOperation":
                    ApplyOperation(doc, Parse<ApplyOperationRequest>(message));
                    break;
                case "setOperation":
                    SetOperation(doc, Parse<SetOperationRequest>(message));
                    break;
                case "deleteNodes":
                    DeleteNodes(doc, Parse<DeleteNodesRequest>(message));
                    break;
                default:
                    RhinoApp.WriteLine($"Freppuccino UI: unknown message '{type}'");
                    break;
            }
        }
        catch (JsonException ex)
        {
            RhinoApp.WriteLine($"Freppuccino UI: bad '{type}' message: {ex.Message}");
        }
    }

    static T Parse<T>(JsonElement message) =>
        message.Deserialize<T>(UiProtocol.Json) ?? throw new JsonException("empty payload");

    static void Select(RhinoDoc doc, SelectRequest request)
    {
        var tree = FrepTrees.Get(doc);
        var nodeIds = Nodes(tree, request.Ids).ToList();

        // While FUnion/FIntersect/FDifference wait for operands, a node click feeds the command (like typing "N3").
        if (FCsgCommand.Picking)
        {
            foreach (var n in nodeIds) RhinoApp.SendKeystrokes(FrepTree.NodeName(n), true);
            return;
        }

        if (request.Mode == "replace") doc.Objects.UnselectAll(); // also clears node selection (OnDeselectAll)
        var nodes = NodesOf(doc);
        foreach (var n in nodeIds)
        {
            bool select = request.Mode != "toggle" || !nodes.Contains(n);
            nodes.Remove(n);
            if (select) nodes.Add(n);
        }
        foreach (var key in request.Ids)
        {
            if (!Guid.TryParse(key, out var id) || doc.Objects.FindId(id) is not { } obj) continue;
            // IsSelected: 0 = not selected, 1 = selected, 2 = selected persistently
            bool select = request.Mode != "toggle" || obj.IsSelected(false) == 0;
            obj.Select(select);
        }
        NodeSelectionChanged?.Invoke();
        doc.Views.Redraw();
    }

    static void SetVisible(RhinoDoc doc, SetVisibleRequest request)
    {
        if (!Guid.TryParse(request.Id, out var id)) return;
        if (request.Visible) doc.Objects.Show(id, true);
        else doc.Objects.Hide(id, true);
        doc.Views.Redraw();
    }

    void ApplyOperation(RhinoDoc doc, ApplyOperationRequest request)
    {
        if (ParseKind(request.Op) is not { } op) throw new JsonException($"unknown op '{request.Op}'");
        var tree0 = FrepTrees.Get(doc);
        var operands = request.Operands.Select(k => ParseOperand(tree0, k)).OfType<FrepOperand>().Distinct().ToList();
        if (operands.Count < 2)
        {
            web.Send(new NoticeMessage("warning", "Need at least 2 operands."));
            return;
        }
        var (tree, node) = tree0.Add(op, Math.Max(0, request.K), operands);
        FrepTrees.Set(doc, tree, $"F-rep {request.Op}");
        // Select the new node, like the page expects after "Apply".
        doc.Objects.UnselectAll();
        NodesOf(doc).Add(node.Id);
        NodeSelectionChanged?.Invoke();
        doc.Views.Redraw();
    }

    static void SetOperation(RhinoDoc doc, SetOperationRequest request)
    {
        if (ParseKind(request.Op) is not { } op) throw new JsonException($"unknown op '{request.Op}'");
        var tree = FrepTrees.Get(doc);
        if (!TryNode(tree, request.Id, out var id) || tree.Find(id) is not { } node) return;
        var k = Math.Max(0, request.K);
        if (node.Op == op && node.K == k) return;
        FrepTrees.Set(doc, tree.WithOp(id, op, k), $"Edit {node.Name}");
    }

    /// Deletes nodes that nothing else references (nodes being deleted together count as going away).
    void DeleteNodes(RhinoDoc doc, DeleteNodesRequest request)
    {
        var tree = FrepTrees.Get(doc);
        var pending = Nodes(tree, request.Ids).ToHashSet();
        bool removed;
        do
        {
            removed = false;
            foreach (var id in pending.ToList())
            {
                if (tree.IsReferenced(FrepOperand.Node(id))) continue;
                tree = tree.Remove(id);
                pending.Remove(id);
                removed = true;
            }
        } while (removed);

        if (pending.Count > 0)
            web.Send(new NoticeMessage("warning", $"Not deleted (still used by other nodes): {string.Join(", ", pending.Select(FrepTree.NodeName))}"));
        FrepTrees.Set(doc, tree, "Delete F-rep node");
    }

    void RunCommand(RunCommandRequest request)
    {
        if (!AllowedCommands.Contains(request.Command))
        {
            web.Send(new NoticeMessage("error", $"Command '{request.Command}' is not allowed from the panel."));
            return;
        }
        // Fails (returns false) if another command is already running.
        if (!RhinoApp.RunScript("_" + request.Command, false))
            web.Send(new NoticeMessage("warning", $"Couldn't start {request.Command}; finish the current command first."));
    }

    void NotImplemented(string what)
    {
        RhinoApp.WriteLine($"Freppuccino UI: {what} is not implemented yet.");
        web.Send(new NoticeMessage("info", $"{what}: not implemented yet."));
    }
}
