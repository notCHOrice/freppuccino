using System.Text.Json;
using System.Text.Json.Serialization;

namespace Freppuccino.Ui;

/// <summary>
/// JSON messages between the side panel (src/ui, React) and Rhino.
/// Mirror of src/ui/src/bridge/protocol.ts — change both together. Every message has a "type".
/// Docs: docs/side-panel.md.
/// </summary>
public static class UiProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ───────────────────────────── Scene tree ─────────────────────────────

public static class SceneNodeKind
{
    /// A Rhino object carrying SDF.* user text (FBox, FSphere, ...). Id = Rhino object id ("D" format).
    public const string Primitive = "primitive";
    /// A CSG node (Csg/FrepTree). Id = node name, e.g. "N3".
    public const string Operation = "operation";
}

/// <summary>Combine operations. Each takes a blend k (model units, 0 = sharp).</summary>
public static class OperationKind
{
    public const string Union = "union";
    /// children[0] minus children[1..]
    public const string Subtract = "subtract";
    public const string Intersect = "intersect";
}

/// <summary>
/// One row in the browser tree. Primitives are leaves; operations nest their operands as children.
/// The CSG graph is a DAG, so a shared operand appears under each parent.
/// </summary>
public sealed record SceneNode(
    string Id,
    string Kind,
    string? Name,
    bool Visible,
    bool Locked,
    string? SdfType = null,
    IReadOnlyDictionary<string, double>? Params = null,
    string? Op = null,
    double? K = null,
    IReadOnlyList<SceneNode>? Children = null,
    bool? Missing = null);

public sealed record UiSettings(string Units);

// ───────────────────────────── Host → UI ─────────────────────────────

public abstract record HostMessage
{
    public abstract string Type { get; }
}

/// Full snapshot; sent in reply to "ready" and when the active document changes.
public sealed record StateMessage(IReadOnlyList<SceneNode> Scene, IReadOnlyList<string> Selection, UiSettings Settings, string Theme) : HostMessage
{
    public override string Type => "state";
}

public sealed record SceneMessage(IReadOnlyList<SceneNode> Scene) : HostMessage
{
    public override string Type => "scene";
}

/// Ids of the selected primitives (Rhino selection) and operation nodes (panel selection).
public sealed record SelectionMessage(IReadOnlyList<string> Ids) : HostMessage
{
    public override string Type => "selection";
}

/// Transient feedback shown in the panel ("info" | "warning" | "error").
public sealed record NoticeMessage(string Level, string Text) : HostMessage
{
    public override string Type => "notice";
}

// ───────────────────────────── UI → Host ─────────────────────────────
// Incoming messages are dispatched on "type" in UiController.Handle.

/// mode: "replace" | "add" | "toggle"
public sealed record SelectRequest(string[] Ids, string Mode = "replace");

/// Primitive: Rhino show/hide. Operation node: FrepNode.Hidden (undoable).
public sealed record SetVisibleRequest(string Id, bool Visible);

/// Only commands in UiController.AllowedCommands run.
public sealed record RunCommandRequest(string Command);

/// STUB: edit one SDF parameter of a primitive, e.g. { id, name: "s", value: 2 }.
public sealed record SetParamRequest(string Id, string Name, double Value);

/// New CSG node over operands (primitive or node ids, ≥ 2). For subtract, operands[0] is the target.
public sealed record ApplyOperationRequest(string[] Operands, string Op, double K);

/// Edit an existing operation node. Preview = live drag tick (no undo); the next non-preview edit ends the gesture as one undo step.
public sealed record SetOperationRequest(string Id, string Op, double K, bool Preview = false);

/// Delete operation nodes nothing else uses.
public sealed record DeleteNodesRequest(string[] Ids);
