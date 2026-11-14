import { Box, Circle, Shapes, SquaresIntersect, SquaresSubtract, SquaresUnite, type LucideIcon } from 'lucide-react'
import type { OperationKind, SceneNode } from '@/bridge/protocol'

interface PrimitiveInfo {
  label: string
  icon: LucideIcon
  /** display names for SDF params; unknown params show their raw key */
  params: Record<string, string>
}

/** Keyed by SDF.Type (IQ function name). Unknown types still show, with raw names. */
export const PRIMITIVES: Record<string, PrimitiveInfo> = {
  sdSphere: { label: 'Sphere', icon: Circle, params: { s: 'Radius' } },
  sdBox: { label: 'Box', icon: Box, params: { 'b.x': 'Half X', 'b.y': 'Half Y', 'b.z': 'Half Z' } },
}

export const OPERATIONS: Record<OperationKind, { label: string; icon: LucideIcon }> = {
  union: { label: 'Union', icon: SquaresUnite },
  subtract: { label: 'Subtract', icon: SquaresSubtract },
  intersect: { label: 'Intersect', icon: SquaresIntersect },
}

/** Prefer <NodeIcon> (components/node-icon.tsx) in JSX. */
export function nodeIcon(node: SceneNode): LucideIcon {
  if (node.kind === 'operation') return node.op ? OPERATIONS[node.op].icon : Shapes
  return (node.sdfType && PRIMITIVES[node.sdfType]?.icon) || Shapes
}

export function typeLabel(node: SceneNode): string {
  if (node.kind === 'operation') return node.op ? OPERATIONS[node.op].label : 'Operation'
  return (node.sdfType && PRIMITIVES[node.sdfType]?.label) || node.sdfType || 'Primitive'
}

export function paramLabel(sdfType: string | undefined, key: string): string {
  return (sdfType && PRIMITIVES[sdfType]?.params[key]) || key
}

/** Display labels for every node: the Rhino name if set, else "Sphere 1", "Union 2", ... in tree order. */
export function labelNodes(scene: SceneNode[]): Map<string, string> {
  const labels = new Map<string, string>()
  const counters = new Map<string, number>()
  const walk = (nodes: SceneNode[]) => {
    for (const node of nodes) {
      const type = typeLabel(node)
      const i = (counters.get(type) ?? 0) + 1
      counters.set(type, i)
      labels.set(node.id, node.name ?? `${type} ${i}`)
      if (node.children) walk(node.children)
    }
  }
  walk(scene)
  return labels
}

export function indexNodes(scene: SceneNode[]): Map<string, SceneNode> {
  const byId = new Map<string, SceneNode>()
  const walk = (nodes: SceneNode[]) => nodes.forEach((n) => (byId.set(n.id, n), n.children && walk(n.children)))
  walk(scene)
  return byId
}
