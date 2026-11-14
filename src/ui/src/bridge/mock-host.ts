import type { Transport } from './host'
import type { AllowedCommand, HostMessage, SceneNode, UiMessage } from './protocol'

/**
 * Stand-in for Rhino when the page runs in a normal browser (`npm run dev`).
 * Behaves like UiController.cs, including the stubs. The scene includes operation nodes
 * (which the real host doesn't send yet) so the nested browser tree can be developed now.
 * Add ?theme=dark / ?theme=light to the URL to force a theme.
 */
export function createMockHost(): Transport {
  let listener: ((m: HostMessage) => void) | null = null
  const emit = (m: HostMessage) => setTimeout(() => listener?.(structuredClone(m)), 0)

  let n = 0
  const id = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`
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

  let scene: SceneNode[] = [
    prim('sdSphere', { s: 2 }),
    prim('sdTorus', { 't.x': 3, 't.y': 0.6 }),
    prim('sdCappedCone', { h: 1.5, r1: 1.2, r2: 0.3 }),
    {
      id: id(), kind: 'operation', op: 'subtract', k: 0.25, visible: true, locked: false,
      children: [
        prim('sdBox', { 'b.x': 3, 'b.y': 3, 'b.z': 1 }),
        {
          id: id(), kind: 'operation', op: 'union', k: 0.5, visible: true, locked: false,
          children: [prim('sdCappedCylinder', { h: 2, r: 0.8 }, 'Bore'), prim('sdEllipsoid', { 'r.x': 1.5, 'r.y': 0.6, 'r.z': 0.6 })],
        },
      ],
    },
    prim('sdPyramid', { h: 2, s: 2.5 }),
  ]
  let selection: string[] = []

  const forced = new URLSearchParams(location.search).get('theme')
  const theme = forced === 'dark' || forced === 'light' ? forced : matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'

  const map = (nodes: SceneNode[], fn: (n: SceneNode) => SceneNode): SceneNode[] =>
    nodes.map((x) => fn({ ...x, children: x.children && map(x.children, fn) }))
  const notImplemented = (what: string) => emit({ type: 'notice', level: 'info', text: `${what}: not implemented yet (mock).` })

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
