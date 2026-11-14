import type { Transport } from './host'
import type { HostMessage, SceneNode, UiMessage } from './protocol'

/**
 * Stand-in for Rhino when the page runs in a normal browser (`npm run dev`).
 * Behaves roughly like UiController.cs, including the stubs. Operation ids are "N<n>" like the real host.
 */
export function createMockHost(): Transport {
  let listener: ((m: HostMessage) => void) | null = null
  const emit = (m: HostMessage) => setTimeout(() => listener?.(structuredClone(m)), 0)

  let n = 0
  const id = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`
  let nextNode = 1
  const opNode = (op: SceneNode['op'], k: number, children: SceneNode[]): SceneNode => ({
    id: `N${nextNode++}`, kind: 'operation', name: `N${nextNode - 1}`, op, k, visible: true, locked: false, children,
  })
  const sphere = (s: number, name?: string): SceneNode => ({
    id: id(), kind: 'primitive', sdfType: 'sdSphere', name, visible: true, locked: false, params: { s },
  })
  const box = (x: number, y: number, z: number): SceneNode => ({
    id: id(), kind: 'primitive', sdfType: 'sdBox', visible: true, locked: false, params: { 'b.x': x, 'b.y': y, 'b.z': z },
  })

  let scene: SceneNode[] = [
    sphere(2),
    box(1, 1.5, 0.5),
  ]
  const holes = opNode('union', 0.5, [sphere(1.2, 'Hole A'), sphere(0.8)])
  scene.push(opNode('subtract', 0.25, [box(3, 3, 1), holes]))
  let selection: string[] = []

  const map = (nodes: SceneNode[], fn: (n: SceneNode) => SceneNode): SceneNode[] =>
    nodes.map((x) => fn({ ...x, children: x.children && map(x.children, fn) }))
  const flat = (nodes: SceneNode[]): SceneNode[] => nodes.flatMap((x) => [x, ...flat(x.children ?? [])])
  const notImplemented = (what: string) => emit({ type: 'notice', level: 'info', text: `${what}: not implemented yet (mock).` })

  function handle(m: UiMessage) {
    switch (m.type) {
      case 'ready':
        emit({ type: 'state', scene, selection, settings: { units: 'Millimeters' }, theme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light' })
        break
      case 'select':
        if (m.mode === 'replace') selection = [...m.ids]
        else if (m.mode === 'add') selection = [...new Set([...selection, ...m.ids])]
        else for (const x of m.ids) selection = selection.includes(x) ? selection.filter((s) => s !== x) : [...selection, x]
        emit({ type: 'selection', ids: selection })
        break
      case 'setVisible':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, visible: m.visible } : x))
        emit({ type: 'scene', scene })
        break
      case 'runCommand':
        if (m.command === 'FSphere') scene = [...scene, sphere(1)]
        else if (m.command === 'FBox') scene = [...scene, box(1, 1, 1)]
        emit({ type: 'scene', scene })
        emit({ type: 'notice', level: 'info', text: `Ran ${m.command} (mock).` })
        break
      case 'setParam':
        notImplemented('Editing primitive parameters')
        break
      case 'applyOperation': {
        const all = flat(scene)
        const operands = m.operands.map((x) => all.find((y) => y.id === x)).filter((x) => !!x)
        const node = opNode(m.op, m.k, operands)
        scene = [...scene.filter((x) => !m.operands.includes(x.id)), node]
        selection = [node.id]
        emit({ type: 'scene', scene })
        emit({ type: 'selection', ids: selection })
        break
      }
      case 'setOperation':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, op: m.op, k: m.k } : x))
        emit({ type: 'scene', scene })
        break
      case 'deleteNodes': {
        // Only roots can go (nothing references them); their operands become roots again.
        const roots = scene.filter((x) => m.ids.includes(x.id) && x.kind === 'operation')
        if (roots.length < m.ids.length) emit({ type: 'notice', level: 'warning', text: 'Not deleted: still used by other nodes (mock).' })
        scene = [...scene.filter((x) => !roots.includes(x)), ...roots.flatMap((x) => x.children ?? [])]
        selection = selection.filter((x) => !roots.some((r) => r.id === x))
        emit({ type: 'scene', scene })
        emit({ type: 'selection', ids: selection })
        break
      }
    }
  }

  return {
    kind: 'mock',
    post: (m) => setTimeout(() => handle(m), 0),
    listen: (cb) => (listener = cb),
  }
}
