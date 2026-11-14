// Messages between this page and Rhino.
// Mirror of src/Freppuccino/Ui/UiProtocol.cs — change both together. Docs: docs/side-panel.md.

export type Guid = string

export type OperationKind = 'union' | 'subtract' | 'intersect'

/**
 * One row in the browser tree. Primitives (Rhino objects with SDF.* user text) are leaves;
 * operations nest their operands as children (children[0] is the target for subtract).
 */
export interface SceneNode {
  id: Guid
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
}

export interface UiSettings {
  /** global smooth-union blend k, model units */
  blend: number
  units: string
}

export type Theme = 'light' | 'dark'

// ── Host → UI ──
export type HostMessage =
  | { type: 'state'; scene: SceneNode[]; selection: Guid[]; settings: UiSettings; theme: Theme }
  | { type: 'scene'; scene: SceneNode[] }
  | { type: 'selection'; ids: Guid[] }
  | { type: 'settings'; settings: UiSettings }
  | { type: 'notice'; level: 'info' | 'warning' | 'error'; text: string }

// ── UI → Host ──
export type SelectMode = 'replace' | 'add' | 'toggle'
export type AllowedCommand = 'FBox' | 'FSphere' | 'Freppuccino'

export type UiMessage =
  | { type: 'ready' }
  | { type: 'select'; ids: Guid[]; mode: SelectMode }
  | { type: 'setVisible'; id: Guid; visible: boolean }
  | { type: 'runCommand'; command: AllowedCommand }
  | { type: 'setBlend'; k: number }
  /** STUB on the host */
  | { type: 'setParam'; id: Guid; name: string; value: number }
  /** STUB on the host. For subtract, a is the target and b the tool. */
  | { type: 'applyOperation'; a: Guid; b: Guid; op: OperationKind; k: number }
  /** STUB on the host: edit an existing operation node */
  | { type: 'setOperation'; id: Guid; op: OperationKind; k: number }
