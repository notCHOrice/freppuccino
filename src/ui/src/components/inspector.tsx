import { ArrowUpDown, Trash2 } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { BrandIcon, NodeIcon, type BrandIconName } from '@/components/brand-icon'
import { ParamSlider } from '@/components/param-slider'
import { send, useHost } from '@/bridge/host'
import type { AllowedCommand, DeformKind, OperationKind, SceneNode } from '@/bridge/protocol'
import { DEFORMS, OPERATIONS, PRIMITIVES, displayParams, isDeform, typeLabel } from '@/lib/scene'

/** Bottom half of the panel; what it shows depends on the selection (pick order). */
export function Inspector(props: { selected: SceneNode[]; labels: Map<string, string> }) {
  const { labels } = props
  const selected = props.selected.filter((n) => !n.missing)
  if (selected.length === 0) return <CreateSection />
  const one = selected[0]
  if (selected.length === 1)
    return (
      <>
        {one.kind === 'primitive' ? (
          <PrimitiveSection key={one.id} node={one} labels={labels} />
        ) : isDeform(one.op) ? (
          <DeformSection key={one.id} node={one} labels={labels} />
        ) : (
          <CombineSection key={one.id} operands={one.children ?? []} labels={labels} existing={one} />
        )}
        <DeformButtons />
      </>
    )
  return <CombineSection key={selected.map((s) => s.id).join()} operands={selected} labels={labels} />
}

function Section(props: { title: string; badge?: string; children: React.ReactNode; icon?: React.ReactNode }) {
  return (
    <section className="grid gap-3 px-4 py-4">
      <header className="flex min-w-0 items-center gap-2">
        {props.icon}
        <h2 className="type-heading truncate">{props.title}</h2>
        {props.badge && <Badge variant="secondary" className="ml-auto">{props.badge}</Badge>}
      </header>
      {props.children}
    </section>
  )
}

// ── Nothing selected: create ──

function CreateSection() {
  return (
    <>
      <Section title="Create">
        <div className="grid grid-cols-[repeat(auto-fill,minmax(72px,1fr))] gap-2">
          {Object.values(PRIMITIVES).map((p) => (
            <CommandTile key={p.command} label={p.label} icon={p.icon} command={p.command} />
          ))}
        </div>
        <p className="type-small text-muted-foreground">
          Runs the Rhino command, e.g. <span className="type-command text-caramel-ink">FTorus</span>.
        </p>
      </Section>
      <div className="mx-4 h-px bg-border" />
      <Section title="View">
        <p className="type-small text-muted-foreground">Select two or more objects or nodes to combine them; blend is per operation.</p>
        <Button variant="secondary" onClick={() => send({ type: 'runCommand', command: 'Freppuccino' })}>
          <BrandIcon name="preview" /> F-rep display mode
        </Button>
      </Section>
    </>
  )
}

// ── One primitive: its SDF parameters ──

function PrimitiveSection(props: { node: SceneNode; labels: Map<string, string> }) {
  const { node } = props
  const units = useHost((s) => s.settings.units)
  const params = displayParams(node)
  // Slider range is fixed per object (the section is keyed by id) so it doesn't move while dragging.
  const [max] = useState(() => Math.max(1, ...params.map((p) => p.value * 3)))

  return (
    <Section title={props.labels.get(node.id) ?? ''} badge={typeLabel(node)} icon={<NodeIcon node={node} className="size-6" />}>
      {params.length === 0 && <p className="text-muted-foreground">No parameters.</p>}
      {params.map((p) => (
        <ParamSlider
          key={p.key}
          label={p.label}
          units={units}
          value={p.value}
          min={0}
          max={max}
          onCommit={(v) => send({ type: 'setParam', id: node.id, name: p.key, value: v })}
        />
      ))}
    </Section>
  )
}

// ── ≥ 2 objects / nodes: new operation; or one operation node: edit it ──

function CombineSection(props: { operands: SceneNode[]; labels: Map<string, string>; existing?: SceneNode }) {
  const { labels, existing } = props
  const units = useHost((s) => s.settings.units)
  const [swapped, setSwapped] = useState(false)
  const existingOp = isDeform(existing?.op) ? undefined : existing?.op
  const [op, setOp] = useState<OperationKind>(existingOp ?? 'union')
  const [k, setK] = useState(existing?.k ?? 0)

  // Host values win when they change (undo, another edit); unrelated scene updates keep local edits.
  // Not mid-drag, and not until the host has caught up with our last edit (stale echoes would yank the slider back).
  const [dragging, setDragging] = useState(false)
  const [pending, setPending] = useState<{ op: OperationKind; k: number } | null>(null)
  const [synced, setSynced] = useState({ op: existing?.op, k: existing?.k })
  const caughtUp = !pending || (existing?.op === pending.op && existing?.k === pending.k)
  if (existing && !dragging && caughtUp && (pending || existing.op !== synced.op || existing.k !== synced.k)) {
    setPending(null)
    setSynced({ op: existing.op, k: existing.k })
    if (existingOp) setOp(existingOp)
    if (existing.k !== undefined) setK(existing.k)
  }

  // Live edits of an existing node. Blend previews are throttled; release sends the final value (= one undo step).
  const live = useRef({ timer: 0, k: 0, sent: null as { op: OperationKind; k: number } | null, previewed: false })
  const edit = (next: { op: OperationKind; k: number }, preview: boolean) => {
    if (!existing) return
    live.current.sent = next
    setPending(next)
    send({ type: 'setOperation', id: existing.id, ...next, preview })
  }
  const previewK = (v: number) => {
    const l = live.current
    l.k = v
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      if (l.k === (l.previewed ? l.sent?.k : existing?.k)) return
      l.previewed = true
      edit({ op, k: l.k }, true)
    }, PREVIEW_MS)
  }
  const commitK = (v: number) => {
    const l = live.current
    clearTimeout(l.timer)
    l.timer = 0
    setK(v)
    setDragging(false)
    // Final message closes the host's gesture, so send it even if the drag ended where it started.
    if (l.previewed || v !== existing?.k) edit({ op, k: v }, false)
    l.previewed = false
  }
  // Unmounted mid-drag (selection changed): close the gesture.
  const id = existing?.id
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (id && l.previewed && l.sent) send({ type: 'setOperation', id, ...l.sent, k: l.k })
    }
  }, [id])

  // Swapping only makes sense for two new operands; otherwise pick order decides the subtract target.
  const canSwap = !existing && props.operands.length === 2
  const operands = canSwap && swapped ? [...props.operands].reverse() : props.operands
  const ordered = op === 'subtract'
  const role = (i: number) => (ordered ? (i === 0 ? 'Target' : 'Tool') : `#${i + 1}`)

  return (
    <Section
      title={existing ? (labels.get(existing.id) ?? 'Operation') : 'Combine'}
      badge={existing ? 'Operation' : `${operands.length} operands`}
      icon={<BrandIcon name={OPERATIONS[op].icon} className="size-6" />}
    >
      <div className="flex items-center gap-1">
        <div className="grid min-w-0 flex-1 gap-1">
          {operands.map((o, i) => (
            <Operand key={`${i}:${o.id}`} node={o} label={labels.get(o.id)} role={role(i)} />
          ))}
        </div>
        {canSwap && (
          <Button variant="ghost" size="icon-xs" aria-label="Swap" onClick={() => setSwapped((s) => !s)}>
            <ArrowUpDown />
          </Button>
        )}
      </div>

      <ToggleGroup
        type="single"
        variant="outline"
        spacing={0}
        value={op}
        onValueChange={(v) => {
          if (!v || v === op) return
          setOp(v as OperationKind)
          edit({ op: v as OperationKind, k }, false)
        }}
        className="w-full"
      >
        {(Object.keys(OPERATIONS) as OperationKind[]).map((kind) => (
          <ToggleGroupItem key={kind} value={kind} className="flex-1 px-1" aria-label={OPERATIONS[kind].label}>
            <BrandIcon name={OPERATIONS[kind].icon} /> {OPERATIONS[kind].label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>

      <ParamSlider
        label="Blend radius"
        units={k === 0 ? 'sharp' : units}
        value={k}
        max={5}
        onChange={(v) => {
          setK(v)
          if (!existing) return
          setDragging(true)
          previewK(v)
        }}
        onCommit={existing ? commitK : setK}
      />

      {existing ? (
        <Button variant="outline" onClick={() => send({ type: 'deleteNodes', ids: [existing.id] })}>
          <Trash2 /> Delete
        </Button>
      ) : (
        <Button onClick={() => send({ type: 'applyOperation', operands: operands.map((o) => o.id), op, k })}>
          Apply {OPERATIONS[op].label.toLowerCase()}
        </Button>
      )}
    </Section>
  )
}

// ── One deform node: its params ──

function DeformSection(props: { node: SceneNode; labels: Map<string, string> }) {
  const { node, labels } = props
  const units = useHost((s) => s.settings.units)
  const params = displayParams(node)
  const isAngle = node.op !== 'repeat'
  const [max] = useState(() => (isAngle ? 360 : Math.max(1, ...params.map((p) => p.value * 3))))

  // Throttled previews while dragging; release sends the final value (= one undo step).
  const live = useRef({ timer: 0, name: '', value: 0, previewed: false })
  const preview = (name: string, value: number) => {
    const l = live.current
    Object.assign(l, { name, value })
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      l.previewed = true
      send({ type: 'setNodeParam', id: node.id, name: l.name, value: l.value, preview: true })
    }, PREVIEW_MS)
  }
  const commit = (name: string, value: number) => {
    const l = live.current
    clearTimeout(l.timer)
    Object.assign(l, { timer: 0, previewed: false })
    send({ type: 'setNodeParam', id: node.id, name, value })
  }
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (l.previewed) send({ type: 'setNodeParam', id: node.id, name: l.name, value: l.value })
    }
  }, [node.id])

  const child = node.children?.[0]
  return (
    <Section title={labels.get(node.id) ?? ''} badge={typeLabel(node)} icon={<NodeIcon node={node} className="size-6" />}>
      {child && <Operand node={child} label={labels.get(child.id)} role="Child" />}
      {node.refMissing && (
        <p className="type-small text-muted-foreground">{node.op === 'repeat' ? 'Box' : 'Axis line'} deleted; drawn unwarped.</p>
      )}
      {params.map((p) => (
        <ParamSlider
          key={p.key}
          label={p.label}
          units={isAngle ? '°' : units}
          value={p.value}
          min={isAngle ? -max : 0}
          max={max}
          step={isAngle ? 1 : 0.01}
          onChange={(v) => preview(p.key, v)}
          onCommit={(v) => commit(p.key, v)}
        />
      ))}
      <Button variant="outline" onClick={() => send({ type: 'deleteNodes', ids: [node.id] })}>
        <Trash2 /> Delete
      </Button>
    </Section>
  )
}

/** Runs FTwist / FBend / FRepeat on the selection (they then ask for the line / box). */
function DeformButtons() {
  return (
    <Section title="Deform">
      <div className="grid grid-cols-[repeat(auto-fill,minmax(72px,1fr))] gap-2">
        {(Object.keys(DEFORMS) as DeformKind[]).map((kind) => (
          <CommandTile key={kind} {...DEFORMS[kind]} />
        ))}
      </div>
    </Section>
  )
}

/** Icon + label button that runs a Rhino command (Create / Deform grids). */
function CommandTile(props: { label: string; icon: BrandIconName; command: AllowedCommand }) {
  return (
    <button
      type="button"
      title={props.command}
      onClick={() => send({ type: 'runCommand', command: props.command })}
      className="group flex flex-col items-center gap-1 rounded-md border border-input bg-card px-1 pt-2 pb-1.5 transition-[background-color,translate,box-shadow] outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background active:translate-y-px"
    >
      <BrandIcon name={props.icon} className="size-7 transition-transform group-hover:-translate-y-0.5" />
      <span className="type-small">{props.label}</span>
    </button>
  )
}

/** Live blend previews: at most one per this many ms (~30 Hz); the final value always follows on release. */
const PREVIEW_MS = 33

function Operand(props: { node: SceneNode; label?: string; role: string }) {
  return (
    <div className="flex min-w-0 flex-1 items-center gap-2 rounded-md border border-input bg-card px-2 py-1.5">
      <NodeIcon node={props.node} className="size-5" />
      <div className="grid min-w-0">
        <span className="type-label text-muted-foreground">{props.role}</span>
        <span className="truncate font-bold">{props.label}</span>
      </div>
    </div>
  )
}
