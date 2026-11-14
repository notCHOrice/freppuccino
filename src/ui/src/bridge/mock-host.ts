import type { Transport } from './host'
import type { HostMessage, SceneNode, UiMessage } from './protocol'

/**
 * Stand-in for Rhino when the page runs in a normal browser (`npm run dev`).
 * Behaves like UiController.cs, including the stubs. The scene includes an operation node
 * (which the real host can't produce yet) so the nested browser tree can be developed now.
 */
export function createMockHost(): Transport {
  let listener: ((m: HostMessage) => void) | null = null
  const emit = (m: HostMessage) => setTimeout(() => listener?.(structuredClone(m)), 0)

  let n = 0
  const id = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`
  const sphere = (s: number, name?: string): SceneNode => ({
    id: id(), kind: 'primitive', sdfType: 'sdSphere', name, visible: true, locked: false, params: { s },
  })
  const box = (x: number, y: number, z: number): SceneNode => ({
    id: id(), kind: 'primitive', sdfType: 'sdBox', visible: true, locked: false, params: { 'b.x': x, 'b.y': y, 'b.z': z },
  })

  let scene: SceneNode[] = [
    sphere(2),
    box(1, 1.5, 0.5),
    {
      id: id(), kind: 'operation', op: 'subtract', k: 0.25, visible: true, locked: false,
      children: [
        box(3, 3, 1),
        { id: id(), kind: 'operation', op: 'union', k: 0.5, visible: true, locked: false, children: [sphere(1.2, 'Hole A'), sphere(0.8)] },
      ],
    },
  ]
  let selection: string[] = []
  let blend = 0.5

  const map = (nodes: SceneNode[], fn: (n: SceneNode) => SceneNode): SceneNode[] =>
    nodes.map((x) => fn({ ...x, children: x.children && map(x.children, fn) }))
  const notImplemented = (what: string) => emit({ type: 'notice', level: 'info', text: `${what}: not implemented yet (mock).` })

  function handle(m: UiMessage) {
    switch (m.type) {
      case 'ready':
        emit({ type: 'state', scene, selection, settings: { blend, units: 'Millimeters' }, theme: matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light' })
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
      case 'setBlend':
        blend = m.k
        break
      case 'setParam':
        notImplemented('Editing primitive parameters')
        break
      case 'applyOperation':
        notImplemented(`${m.op} (k = ${m.k})`)
        break
      case 'setOperation':
        notImplemented(`Editing operation (${m.op}, k = ${m.k})`)
        break
    }
  }

  return {
    kind: 'mock',
    post: (m) => setTimeout(() => handle(m), 0),
    listen: (cb) => (listener = cb),
  }
}
