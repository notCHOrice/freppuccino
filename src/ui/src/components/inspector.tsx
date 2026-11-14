import { ArrowLeftRight, Box, Circle, Sparkles } from 'lucide-react'
import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { NodeIcon } from '@/components/node-icon'
import { ParamSlider } from '@/components/param-slider'
import { send, useHost } from '@/bridge/host'
import type { OperationKind, SceneNode } from '@/bridge/protocol'
import { OPERATIONS, paramLabel, typeLabel } from '@/lib/scene'

/** Bottom half of the panel; what it shows depends on the selection. */
export function Inspector(props: { selected: SceneNode[]; labels: Map<string, string> }) {
  const { selected, labels } = props
  if (selected.length === 0) return <CreateSection />
  if (selected.length === 1 && selected[0].kind === 'primitive') return <PrimitiveSection key={selected[0].id} node={selected[0]} labels={labels} />
  if (selected.length === 1 && selected[0].kind === 'operation') {
    const op = selected[0]
    const [a, b] = op.children ?? []
    return a && b ? <CombineSection key={op.id} a={a} b={b} labels={labels} existing={op} /> : null
  }
  if (selected.length === 2) return <CombineSection key={selected.map((s) => s.id).join()} a={selected[0]} b={selected[1]} labels={labels} />
  return <Section title={`${selected.length} selected`}><p className="text-muted-foreground">Select two objects to combine them.</p></Section>
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
  const settings = useHost((s) => s.settings)
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
        <ParamSlider
          label="Global blend"
          hint={settings.units}
          value={settings.blend}
          max={5}
          onChange={(k) => send({ type: 'setBlend', k })}
        />
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
    <Section title={existing ? labels.get(existing.id) ?? 'Operation' : 'Combine'} badge={existing ? 'Operation' : undefined}>
      <div className="flex items-center gap-1">
        <Operand node={a} label={labels.get(a.id)} role={ordered ? 'Target' : 'A'} />
        <Button
          variant="ghost"
          size="icon-xs"
          aria-label="Swap"
          disabled={!!existing}
          onClick={() => setSwapped((s) => !s)}
        >
          <ArrowLeftRight />
        </Button>
        <Operand node={b} label={labels.get(b.id)} role={ordered ? 'Tool' : 'B'} />
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

      <Button onClick={apply}>{existing ? 'Update' : `Apply ${OPERATIONS[op].label.toLowerCase()}`}</Button>
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
