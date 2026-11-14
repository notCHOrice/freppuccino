import { ArrowUpDown, Box, Circle, Sparkles, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { NodeIcon } from '@/components/node-icon'
import { ParamSlider } from '@/components/param-slider'
import { send, useHost } from '@/bridge/host'
import type { OperationKind, SceneNode } from '@/bridge/protocol'
import { OPERATIONS, paramLabel, typeLabel } from '@/lib/scene'

/** Bottom half of the panel; what it shows depends on the selection (pick order). */
export function Inspector(props: { selected: SceneNode[]; labels: Map<string, string> }) {
  const { labels } = props
  const selected = props.selected.filter((n) => !n.missing)
  if (selected.length === 0) return <CreateSection />
  if (selected.length === 1 && selected[0].kind === 'primitive') return <PrimitiveSection key={selected[0].id} node={selected[0]} labels={labels} />
  if (selected.length === 1) return <CombineSection key={selected[0].id} operands={selected[0].children ?? []} labels={labels} existing={selected[0]} />
  return <CombineSection key={selected.map((s) => s.id).join()} operands={selected} labels={labels} />
}

function Section(props: { title: string; badge?: string; children: React.ReactNode; icon?: React.ReactNode }) {
  return (
    <section className="grid gap-3 px-3 py-3">
      <header className="flex items-center gap-1.5">
        {props.icon}
        <h2 className="truncate font-medium">{props.title}</h2>
        {props.badge && <Badge variant="secondary" className="ml-auto">{props.badge}</Badge>}
      </header>
      {props.children}
    </section>
  )
}

// ── Nothing selected: create + scene settings ──

function CreateSection() {
  return (
    <>
      <Section title="Create">
        <div className="grid grid-cols-2 gap-2">
          <Button variant="outline" onClick={() => send({ type: 'runCommand', command: 'FBox' })}>
            <Box /> Box
          </Button>
          <Button variant="outline" onClick={() => send({ type: 'runCommand', command: 'FSphere' })}>
            <Circle /> Sphere
          </Button>
        </div>
      </Section>
      <Section title="Scene">
        <p className="text-muted-foreground">Select two or more objects or nodes to combine them; blend is per operation.</p>
        <Button variant="secondary" onClick={() => send({ type: 'runCommand', command: 'Freppuccino' })}>
          <Sparkles /> F-rep display mode
        </Button>
      </Section>
    </>
  )
}

// ── One primitive: its SDF parameters ──

function PrimitiveSection(props: { node: SceneNode; labels: Map<string, string> }) {
  const { node } = props
  const units = useHost((s) => s.settings.units)
  const params = Object.entries(node.params ?? {})
  // Slider range is fixed per object (the section is keyed by id) so it doesn't move while dragging.
  const [max] = useState(() => Math.max(1, ...params.map(([, v]) => v * 3)))

  return (
    <Section title={props.labels.get(node.id) ?? ''} badge={typeLabel(node)} icon={<NodeIcon node={node} className="size-3.5 text-muted-foreground" />}>
      {params.length === 0 && <p className="text-muted-foreground">No parameters.</p>}
      {params.map(([key, value]) => (
        <ParamSlider
          key={key}
          label={paramLabel(node.sdfType, key)}
          hint={units}
          value={value}
          min={0.001}
          max={max}
          onCommit={(v) => send({ type: 'setParam', id: node.id, name: key, value: v })}
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
  const [op, setOp] = useState<OperationKind>(existing?.op ?? 'union')
  const [k, setK] = useState(existing?.k ?? 0)
  // Host values win when they change (undo, another edit); unrelated scene updates keep local edits.
  const [synced, setSynced] = useState({ op: existing?.op, k: existing?.k })
  if (existing && (existing.op !== synced.op || existing.k !== synced.k)) {
    setSynced({ op: existing.op, k: existing.k })
    if (existing.op) setOp(existing.op)
    if (existing.k !== undefined) setK(existing.k)
  }

  // Swapping only makes sense for two new operands; otherwise pick order decides the subtract target.
  const canSwap = !existing && props.operands.length === 2
  const operands = canSwap && swapped ? [...props.operands].reverse() : props.operands
  const ordered = op === 'subtract'
  const role = (i: number) => (ordered ? (i === 0 ? 'Target' : 'Tool') : `#${i + 1}`)
  const dirty = !existing || op !== existing.op || k !== existing.k

  const apply = () =>
    existing
      ? send({ type: 'setOperation', id: existing.id, op, k })
      : send({ type: 'applyOperation', operands: operands.map((o) => o.id), op, k })

  return (
    <Section title={existing ? labels.get(existing.id) ?? 'Operation' : 'Combine'} badge={existing ? 'Operation' : `${operands.length} operands`}>
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
        onValueChange={(v) => v && setOp(v as OperationKind)}
        className="w-full"
      >
        {(Object.keys(OPERATIONS) as OperationKind[]).map((kind) => {
          const { label, icon: Icon } = OPERATIONS[kind]
          return (
            <ToggleGroupItem key={kind} value={kind} className="flex-1" aria-label={label}>
              <Icon /> {label}
            </ToggleGroupItem>
          )
        })}
      </ToggleGroup>

      <ParamSlider label="Blend" hint={k === 0 ? 'sharp' : units} value={k} max={5} onChange={setK} />

      <div className="flex gap-2">
        <Button className="flex-1" disabled={!dirty} onClick={apply}>
          {existing ? 'Update' : `Apply ${OPERATIONS[op].label.toLowerCase()}`}
        </Button>
        {existing && (
          <Button variant="outline" aria-label="Delete" onClick={() => send({ type: 'deleteNodes', ids: [existing.id] })}>
            <Trash2 />
          </Button>
        )}
      </div>
    </Section>
  )
}

function Operand(props: { node: SceneNode; label?: string; role: string }) {
  return (
    <div className="flex min-w-0 flex-1 items-center gap-1.5 rounded-md border px-2 py-1">
      <NodeIcon node={props.node} className="size-3.5 shrink-0 text-muted-foreground" />
      <div className="grid min-w-0">
        <span className="text-[0.625rem] text-muted-foreground uppercase">{props.role}</span>
        <span className="truncate">{props.label}</span>
      </div>
    </div>
  )
}
