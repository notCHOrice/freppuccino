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
    id: id(), kind: 'primitive', sdfType, name, visible: true, locked: false, params, color: '#FCFCFC',
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
      case 'setColor':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, color: m.color.toUpperCase() } : x))
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
      case 'aiTool': {
        let reply: HostMessage
        try {
          reply = { type: 'aiToolResult', callId: m.callId, ok: true, result: aiTool(m.tool, m.args) }
        } catch (e) {
          reply = { type: 'aiToolResult', callId: m.callId, ok: false, error: (e as Error).message }
        }
        emit(reply)
        emit({ type: 'scene', scene })
        break
      }
      case 'aiSaveRun':
        console.info('[mock] AI run (not saved in the browser):', m.title, m.transcript)
        break
    }
  }

  // ── AI tools: just enough of Ai/AiTools.cs to exercise the agent loop in a browser (no geometry, no screenshots) ──
  const specs = new Map<string, Record<string, unknown>>() // primitive id → spec
  const SDF: Record<string, string> = {
    sphere: 'sdSphere', box: 'sdBox', cylinder: 'sdCappedCylinder', cone: 'sdCappedCone',
    pyramid: 'sdPyramid', ellipsoid: 'sdEllipsoid', torus: 'sdTorus',
  }
  const find = (ref: string) => {
    const node = flat(scene).find((x) => x.id === ref || x.name === ref)
    if (!node) throw new Error(`no object or node '${ref}'; call get_scene for current refs`)
    return node
  }
  function aiTool(tool: string, args: Record<string, unknown>): unknown {
    switch (tool) {
      case 'get_scene': {
        const all = flat(scene)
        return {
          units: 'Millimeters',
          primitives: all.filter((x) => x.kind === 'primitive').map((x) => ({ ref: x.name ?? x.id, ...specs.get(x.id) })),
          operations: all.filter((x) => x.kind === 'operation').map((x) => ({
            ref: x.id, op: x.op, k: x.k, operands: x.children?.map((c) => c.name ?? c.id),
          })),
          roots: scene.map((x) => x.name ?? x.id),
        }
      }
      case 'create_primitive': {
        const type = String(args.type)
        if (!SDF[type]) throw new Error(`unknown type '${type}'`)
        let name = String(args.name ?? type).replace(/\s+/g, '_')
        while (flat(scene).some((x) => x.name === name)) name += '_2'
        const p = prim(SDF[type], {}, name)
        const { name: _, ...spec } = args
        specs.set(p.id, spec)
        scene = [...scene, p]
        return { ref: name, guid: p.id }
      }
      case 'update_primitive': {
        const p = find(String(args.ref))
        const { ref: _, ...patch } = args
        specs.set(p.id, { ...specs.get(p.id), ...patch })
        return { ref: p.name, ...specs.get(p.id) }
      }
      case 'combine': {
        const operands = (args.operands as string[]).map(find)
        const node = opNode(args.op as SceneNode['op'], Number(args.k ?? 0), operands)
        scene = [...scene.filter((x) => !operands.includes(x)), node]
        return { ref: node.id }
      }
      case 'deform': {
        const target = find(String(args.target))
        const node = { ...opNode(args.op as SceneNode['op'], 0, [target]), params: {} }
        scene = [...scene.filter((x) => x !== target), node]
        const { op, target: _, ...fields } = args
        return { ref: node.id, op, target: target.name ?? target.id, ...fields }
      }
      case 'edit_operation': {
        const n = find(String(args.ref))
        scene = map(scene, (x) => (x.id === n.id ? { ...x, op: (args.op as SceneNode['op']) ?? x.op, k: (args.k as number) ?? x.k } : x))
        return { ref: n.id }
      }
      case 'delete': {
        const ids = new Set((args.refs as string[]).map((r) => find(r).id))
        scene = scene.filter((x) => !ids.has(x.id))
        return { deleted: [...ids] }
      }
      case 'clear_scene':
        scene = []
        return { ok: true }
      case 'transform':
      case 'set_visible':
        return { ok: true, note: 'mock host: no geometry' }
      case 'capture_views':
        throw new Error('Screenshots need Rhino (the mock host has no viewport). Skip visual checks.')
      default:
        throw new Error(`unknown tool '${tool}'`)
    }
  }

  return {
    kind: 'mock',
    post: (m) => setTimeout(() => handle(m), 0),
    listen: (cb) => (listener = cb),
  }
}
