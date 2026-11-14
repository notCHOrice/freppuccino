// Messages between this page and Rhino.
// Mirror of src/Freppuccino/Ui/UiProtocol.cs — change both together. Docs: docs/side-panel.md.

/** Primitive: Rhino object id (GUID). Operation: CSG node name, e.g. "N3". */
export type Id = string

export type OperationKind = 'union' | 'subtract' | 'intersect'

/**
 * One row in the browser tree. Primitives (Rhino objects with SDF.* user text) are leaves;
 * operations (CSG nodes) nest their operands as children (children[0] is the target for subtract).
 * The CSG graph is a DAG: a shared operand appears under each parent.
 */
export interface SceneNode {
  id: Id
  kind: 'primitive' | 'operation'
  name?: string
  visible: boolean
  locked: boolean
  /** primitive: IQ function name, e.g. "sdSphere", "sdBox" */
  sdfType?: string
  /** primitive: SDF.<param> values without the prefix, e.g. { s: 1 } or { "b.x": 1, "b.y": 2, "b.z": 3 } */
  params?: Record<string, number>
  /** operation */
  op?: OperationKind
  /** operation: blend distance in model units, 0 = sharp */
  k?: number
  children?: SceneNode[]
  /** operand whose object / node was deleted */
  missing?: boolean
}

export interface UiSettings {
  units: string
}

export type Theme = 'light' | 'dark'

// ── Host → UI ──
export type HostMessage =
  | { type: 'state'; scene: SceneNode[]; selection: Id[]; settings: UiSettings; theme: Theme }
  | { type: 'scene'; scene: SceneNode[] }
  /** selected primitives (Rhino selection) + operation nodes (kept by the host) */
  | { type: 'selection'; ids: Id[] }
  | { type: 'notice'; level: 'info' | 'warning' | 'error'; text: string }

// ── UI → Host ──
export type SelectMode = 'replace' | 'add' | 'toggle'
/** Must match UiController.AllowedCommands */
export type AllowedCommand =
  | 'FBox' | 'FSphere' | 'FCylinder' | 'FCone' | 'FPyramid' | 'FEllipsoid' | 'FTorus'
  | 'Freppuccino'

export type UiMessage =
  | { type: 'ready' }
  /** While an F-CSG command waits for operands, the host feeds clicked nodes to it instead of selecting. */
  | { type: 'select'; ids: Id[]; mode: SelectMode }
  /** primitive: Rhino show/hide; operation: hides the node's result (undoable) */
  | { type: 'setVisible'; id: Id; visible: boolean }
  | { type: 'runCommand'; command: AllowedCommand }
  /** preview: live drag tick, no undo; the next non-preview setParam ends the gesture as one undo step. */
  | { type: 'setParam'; id: Id; name: string; value: number; preview?: boolean }
  /** New CSG node over ≥ 2 primitives / nodes. For subtract, operands[0] is the target. */
  | { type: 'applyOperation'; operands: Id[]; op: OperationKind; k: number }
  /** preview: live drag tick, no undo; the next non-preview setOperation ends the gesture as one undo step. */
  | { type: 'setOperation'; id: Id; op: OperationKind; k: number; preview?: boolean }
  /** Nodes still used by other nodes are kept (host sends a notice). */
  | { type: 'deleteNodes'; ids: Id[] }
