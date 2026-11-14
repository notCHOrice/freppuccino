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
    /// A Rhino object carrying SDF.* user text (FBox, FSphere, ...). Id = Rhino object id.
    public const string Primitive = "primitive";
    /// A CSG operation over its children (future, see docs/side-panel.md). Id = graph node id.
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
/// One row in the browser tree. Primitives are leaves; operations nest their operands as children,
/// so the same shape covers today's flat list and the future CSG tree.
/// </summary>
public sealed record SceneNode(
    Guid Id,
    string Kind,
    string? Name,
    bool Visible,
    bool Locked,
    string? SdfType = null,
    IReadOnlyDictionary<string, double>? Params = null,
    string? Op = null,
    double? K = null,
    IReadOnlyList<SceneNode>? Children = null);

public sealed record UiSettings(double Blend, string Units);

// ───────────────────────────── Host → UI ─────────────────────────────

public abstract record HostMessage
{
    public abstract string Type { get; }
}

/// Full snapshot; sent in reply to "ready" and when the active document changes.
public sealed record StateMessage(IReadOnlyList<SceneNode> Scene, IReadOnlyList<Guid> Selection, UiSettings Settings, string Theme) : HostMessage
{
    public override string Type => "state";
}

public sealed record SceneMessage(IReadOnlyList<SceneNode> Scene) : HostMessage
{
    public override string Type => "scene";
}

/// Ids of the selected F-rep objects (primitives now, operation nodes later).
public sealed record SelectionMessage(IReadOnlyList<Guid> Ids) : HostMessage
{
    public override string Type => "selection";
}

public sealed record SettingsMessage(UiSettings Settings) : HostMessage
{
    public override string Type => "settings";
}

/// Transient feedback shown in the panel ("info" | "warning" | "error").
public sealed record NoticeMessage(string Level, string Text) : HostMessage
{
    public override string Type => "notice";
}

// ───────────────────────────── UI → Host ─────────────────────────────
// Incoming messages are dispatched on "type" in UiController.Handle.

/// mode: "replace" | "add" | "toggle"
public sealed record SelectRequest(Guid[] Ids, string Mode = "replace");

public sealed record SetVisibleRequest(Guid Id, bool Visible);

/// Only commands in UiController.AllowedCommands run.
public sealed record RunCommandRequest(string Command);

public sealed record SetBlendRequest(double K);

/// STUB: edit one SDF parameter of a primitive, e.g. { id, name: "s", value: 2 }.
public sealed record SetParamRequest(Guid Id, string Name, double Value);

/// STUB: combine A and B. For subtract, A is the target and B the tool.
public sealed record ApplyOperationRequest(Guid A, Guid B, string Op, double K);

/// STUB: edit an existing operation node (selected in the tree).
public sealed record SetOperationRequest(Guid Id, string Op, double K);
