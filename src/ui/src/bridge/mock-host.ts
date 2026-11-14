import type { Transport } from './host'
import type { AllowedCommand, HostMessage, SceneNode, UiMessage } from './protocol'

/**
 * Stand-in for Rhino when the page runs in a normal browser (`npm run dev`).
 * Behaves roughly like UiController.cs, including the stubs. Operation ids are "N<n>" like the real host.
 * Add ?theme=dark / ?theme=light to the URL to force a theme.
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
  const prim = (sdfType: string, params: Record<string, number>, name?: string): SceneNode => ({
    id: id(), kind: 'primitive', sdfType, name, visible: true, locked: false, params,
  })
  const make: Partial<Record<AllowedCommand, () => SceneNode>> = {
    FBox: () => prim('sdBox', { 'b.x': 1, 'b.y': 1, 'b.z': 1 }),
    FSphere: () => prim('sdSphere', { s: 1 }),
    FCylinder: () => prim('sdCappedCylinder', { h: 1, r: 0.5 }),
    FCone: () => prim('sdCappedCone', { h: 1, r1: 1, r2: 0 }),
    FPyramid: () => prim('sdPyramid', { h: 2, s: 2 }),
    FEllipsoid: () => prim('sdEllipsoid', { 'r.x': 2, 'r.y': 1, 'r.z': 1 }),
    FTorus: () => prim('sdTorus', { 't.x': 2, 't.y': 0.5 }),
  }

  const holes = opNode('union', 0.5, [
    prim('sdCappedCylinder', { h: 2, r: 0.8 }, 'Bore'),
    prim('sdEllipsoid', { 'r.x': 1.5, 'r.y': 0.6, 'r.z': 0.6 }),
  ])
  let scene: SceneNode[] = [
    prim('sdSphere', { s: 2 }),
    prim('sdTorus', { 't.x': 3, 't.y': 0.6 }),
    prim('sdCappedCone', { h: 1.5, r1: 1.2, r2: 0.3 }),
    opNode('subtract', 0.25, [prim('sdBox', { 'b.x': 3, 'b.y': 3, 'b.z': 1 }), holes]),
    prim('sdPyramid', { h: 2, s: 2.5 }),
    { ...prim('sdCappedCylinder', { h: 1, r: 0.5 }), custom: true },
  ]
  let selection: string[] = []

  const forced = new URLSearchParams(location.search).get('theme')
  const theme = forced === 'dark' || forced === 'light' ? forced : matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'

  const map = (nodes: SceneNode[], fn: (n: SceneNode) => SceneNode): SceneNode[] =>
    nodes.map((x) => fn({ ...x, children: x.children && map(x.children, fn) }))
  const flat = (nodes: SceneNode[]): SceneNode[] => nodes.flatMap((x) => [x, ...flat(x.children ?? [])])

  function handle(m: UiMessage) {
    switch (m.type) {
      case 'ready':
        emit({ type: 'state', scene, selection, settings: { units: 'Millimeters' }, theme })
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
      case 'runCommand': {
        const created = make[m.command]?.()
        if (created) {
          scene = [...scene, created]
          emit({ type: 'scene', scene })
        }
        emit({ type: 'notice', level: 'info', text: `Ran ${m.command} (mock).` })
        break
      }
      case 'setParam':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, params: { ...x.params, [m.name]: m.value } } : x))
        emit({ type: 'scene', scene })
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
      case 'setNodeParam':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, params: { ...x.params, [m.name]: m.value } } : x))
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
