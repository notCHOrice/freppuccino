import { Box, Circle, Cone, Cylinder, Egg, Pyramid, Shapes, SquaresIntersect, SquaresSubtract, SquaresUnite, Torus, type LucideIcon } from 'lucide-react'
import type { AllowedCommand, OperationKind, SceneNode } from '@/bridge/protocol'

interface PrimitiveInfo {
  label: string
  icon: LucideIcon
  /** Rhino command that creates it */
  command: AllowedCommand
  /** display names for SDF params; unknown params show their raw key */
  params: Record<string, string>
}

/** Keyed by SDF.Type (IQ function name). Unknown types still show, with raw names. */
export const PRIMITIVES: Record<string, PrimitiveInfo> = {
  sdBox: { label: 'Box', icon: Box, command: 'FBox', params: { 'b.x': 'Half X', 'b.y': 'Half Y', 'b.z': 'Half Z' } },
  sdSphere: { label: 'Sphere', icon: Circle, command: 'FSphere', params: { s: 'Radius' } },
  sdCappedCylinder: { label: 'Cylinder', icon: Cylinder, command: 'FCylinder', params: { h: 'Half height', r: 'Radius' } },
  sdCappedCone: { label: 'Cone', icon: Cone, command: 'FCone', params: { h: 'Half height', r1: 'Base radius', r2: 'Top radius' } },
  sdTorus: { label: 'Torus', icon: Torus, command: 'FTorus', params: { 't.x': 'Major radius', 't.y': 'Minor radius' } },
  sdEllipsoid: { label: 'Ellipsoid', icon: Egg, command: 'FEllipsoid', params: { 'r.x': 'Radius X', 'r.y': 'Radius Y', 'r.z': 'Radius Z' } },
  sdPyramid: { label: 'Pyramid', icon: Pyramid, command: 'FPyramid', params: { h: 'Height', s: 'Base side' } },
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
