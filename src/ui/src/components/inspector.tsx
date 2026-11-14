import { ArrowLeftRight } from 'lucide-react'
import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { BrandIcon, NodeIcon } from '@/components/brand-icon'
import { ParamSlider } from '@/components/param-slider'
import { send, useHost } from '@/bridge/host'
import type { OperationKind, SceneNode } from '@/bridge/protocol'
import { OPERATIONS, PRIMITIVES, displayParams, typeLabel } from '@/lib/scene'

/** Bottom half of the panel; what it shows depends on the selection. */
export function Inspector(props: { selected: SceneNode[]; labels: Map<string, string> }) {
  const { selected, labels } = props
  if (selected.length === 0) return <CreateSection />
  if (selected.length === 1 && selected[0].kind === 'primitive')
    return <PrimitiveSection key={selected[0].id} node={selected[0]} labels={labels} />
  if (selected.length === 1 && selected[0].kind === 'operation') {
    const op = selected[0]
    const [a, b] = op.children ?? []
    return a && b ? <CombineSection key={op.id} a={a} b={b} labels={labels} existing={op} /> : null
  }
  if (selected.length === 2)
    return <CombineSection key={selected.map((s) => s.id).join()} a={selected[0]} b={selected[1]} labels={labels} />
  return (
    <Section title={`${selected.length} selected`}>
      <p className="text-muted-foreground">Select two objects to combine them.</p>
    </Section>
  )
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
            <button
              key={p.command}
              type="button"
              title={p.command}
              onClick={() => send({ type: 'runCommand', command: p.command })}
              className="group flex flex-col items-center gap-1 rounded-md border border-input bg-card px-1 pt-2 pb-1.5 transition-[background-color,translate,box-shadow] outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background active:translate-y-px"
            >
              <BrandIcon name={p.icon} className="size-7 transition-transform group-hover:-translate-y-0.5" />
              <span className="type-small">{p.label}</span>
            </button>
          ))}
        </div>
        <p className="type-small text-muted-foreground">
          Runs the Rhino command, e.g. <span className="type-command text-caramel-ink">FTorus</span>.
        </p>
      </Section>
      <div className="mx-4 h-px bg-border" />
      <Section title="View">
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

// ── Two objects (or an existing operation): combine ──

function CombineSection(props: { a: SceneNode; b: SceneNode; labels: Map<string, string>; existing?: SceneNode }) {
  const { labels, existing } = props
  const units = useHost((s) => s.settings.units)
  const [swapped, setSwapped] = useState(false)
  const [op, setOp] = useState<OperationKind>(existing?.op ?? 'union')
  const [k, setK] = useState(existing?.k ?? 0)
  // Host values win when they change (undo, another edit); unrelated scene updates keep local edits.
  const [synced, setSynced] = useState({ op: existing?.op, k: existing?.k })
  if (existing && (existing.op !== synced.op || existing.k !== synced.k)) {
    setSynced({ op: existing.op, k: existing.k })
    if (existing.op) setOp(existing.op)
    if (existing.k !== undefined) setK(existing.k)
  }

  const [a, b] = swapped ? [props.b, props.a] : [props.a, props.b]
  const ordered = op === 'subtract'

  const apply = () =>
    existing
      ? send({ type: 'setOperation', id: existing.id, op, k })
      : send({ type: 'applyOperation', a: a.id, b: b.id, op, k })

  return (
    <Section
      title={existing ? (labels.get(existing.id) ?? 'Operation') : 'Combine'}
      badge={existing ? 'Operation' : undefined}
      icon={<BrandIcon name={OPERATIONS[op].icon} className="size-6" />}
    >
      <div className="flex items-center gap-1">
        <Operand node={a} label={labels.get(a.id)} role={ordered ? 'Target' : 'A'} />
        <Button variant="ghost" size="icon-xs" aria-label="Swap" disabled={!!existing} onClick={() => setSwapped((s) => !s)}>
          <ArrowLeftRight />
        </Button>
        <Operand node={b} label={labels.get(b.id)} role={ordered ? 'Tool' : 'B'} />
      </div>

      <ToggleGroup type="single" variant="outline" spacing={0} value={op} onValueChange={(v) => v && setOp(v as OperationKind)} className="w-full">
        {(Object.keys(OPERATIONS) as OperationKind[]).map((kind) => (
          <ToggleGroupItem key={kind} value={kind} className="flex-1 px-1" aria-label={OPERATIONS[kind].label}>
            <BrandIcon name={OPERATIONS[kind].icon} /> {OPERATIONS[kind].label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>

      <ParamSlider label="Blend radius" units={k === 0 ? 'sharp' : units} value={k} max={5} onChange={setK} />

      <Button onClick={apply}>{existing ? 'Update' : `Apply ${OPERATIONS[op].label.toLowerCase()}`}</Button>
    </Section>
  )
}

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
