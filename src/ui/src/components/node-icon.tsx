import { createElement } from 'react'
import type { SceneNode } from '@/bridge/protocol'
import { nodeIcon } from '@/lib/scene'

export function NodeIcon(props: { node: SceneNode; className?: string }) {
  return createElement(nodeIcon(props.node), { className: props.className })
}
