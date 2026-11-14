using System.Text.Json;
using Freppuccino.Sdf;
using Rhino;
using Rhino.DocObjects;

namespace Freppuccino.Ui;

/// <summary>
/// Glue between Rhino and the side panel page: pushes scene / selection / settings snapshots when they change
/// (coalesced on Idle) and handles requests from the page.
///
/// Wired: selection sync both ways, show/hide, a whitelist of commands.
/// Stubbed (UI exists, no implementation yet): <see cref="SetParamRequest"/>, <see cref="ApplyOperationRequest"/>,
/// <see cref="SetOperationRequest"/>.
/// </summary>
public sealed class UiController : IDisposable
{
    /// Commands the page may run. Keep this a whitelist: the page must not be able to run arbitrary scripts.
    static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "FBox", "FSphere", "FCylinder", "FCone", "FPyramid", "FEllipsoid", "FTorus", "Freppuccino",
    };

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
        RhinoApp.Idle -= OnIdle;
    }

    // ─────────────── Rhino → page ───────────────

    void OnSelectionChanged(object? sender, RhinoObjectSelectionEventArgs e) => selectionDirty = true;
    void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e) => selectionDirty = true;
    void OnObjectChanged(object? sender, RhinoObjectEventArgs e) => sceneDirty = selectionDirty = true;
    void OnReplaceObject(object? sender, RhinoReplaceObjectEventArgs e) => sceneDirty = true;
    void OnModifyAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e) => sceneDirty = true;

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
        web.Send(new StateMessage(BuildScene(doc), SelectedIds(doc), Settings(doc), theme));
    }

    static UiSettings Settings(RhinoDoc doc) => new(doc.ModelUnitSystem.ToString());

    /// Today: a flat list of primitives in document order. Once operations exist (docs/side-panel.md) this is
    /// where the graph gets walked and operands get nested under their operation node.
    static List<SceneNode> BuildScene(RhinoDoc doc)
    {
        var nodes = new List<SceneNode>();
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
            var attributes = obj.Attributes;
            if (SdfUserText.ReadType(attributes) is not { } type) continue;
            nodes.Add(new SceneNode(
                Id: obj.Id,
                Kind: SceneNodeKind.Primitive,
                Name: string.IsNullOrEmpty(attributes.Name) ? null : attributes.Name,
                Visible: !obj.IsHidden,
                Locked: obj.IsLocked,
                SdfType: type,
                Params: ReadParams(attributes)));
        }
        return nodes;
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

    static List<Guid> SelectedIds(RhinoDoc doc) =>
        doc.Objects.GetSelectedObjects(false, false)
            .Where(o => SdfUserText.ReadType(o.Attributes) != null)
            .Select(o => o.Id)
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
                    // TODO: create an operation node over a, b (docs/side-panel.md "Operations"), then send the new scene.
                    var op = Parse<ApplyOperationRequest>(message);
                    NotImplemented($"{op.Op} (k = {op.K:0.###})");
                    break;
                case "setOperation":
                    // TODO: update the operation node's op / k (undoable), then send the new scene.
                    var edit = Parse<SetOperationRequest>(message);
                    NotImplemented($"Editing operation ({edit.Op}, k = {edit.K:0.###})");
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
        if (request.Mode == "replace") doc.Objects.UnselectAll();
        foreach (var id in request.Ids)
        {
            var obj = doc.Objects.FindId(id);
            if (obj == null) continue;
            // IsSelected: 0 = not selected, 1 = selected, 2 = selected persistently
            bool select = request.Mode != "toggle" || obj.IsSelected(false) == 0;
            obj.Select(select);
        }
        doc.Views.Redraw();
    }

    static void SetVisible(RhinoDoc doc, SetVisibleRequest request)
    {
        if (request.Visible) doc.Objects.Show(request.Id, true);
        else doc.Objects.Hide(request.Id, true);
        doc.Views.Redraw();
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
