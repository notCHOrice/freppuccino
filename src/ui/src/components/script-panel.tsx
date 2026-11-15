import { CircleAlert, CircleCheck, FilePlus2, Loader2, Play, RotateCcw, Sparkles } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { BrandIcon } from '@/components/brand-icon'
import { CodeEditor, type CodeEditorHandle, type Diagnostic } from '@/components/code-editor'
import { PrimitiveSection } from '@/components/inspector'
import { callTool, ToolError, useHost } from '@/bridge/host'
import type { Id } from '@/bridge/protocol'
import { sendPrompt, useChat } from '@/ai/agent'
import { EXAMPLES, findExample, type Vec3 } from '@/lib/examples'
import { indexNodes, labelNodes } from '@/lib/scene'

const NEW = 'new'
const STARTER = EXAMPLES[0]

interface Check {
  ok: boolean
  diagnostics: Diagnostic[]
  params?: { name: string; default: number }[]
}

interface NewBox {
  size: Vec3
  lipschitz: number
  name: string
}

export function ScriptPanel(props: { editRequest?: { id: Id; seq: number }; onOpenAi: () => void }) {
  const scene = useHost((s) => s.scene)
  const selection = useHost((s) => s.selection)
  const units = useHost((s) => s.settings.units)
  const aiRunning = useChat((s) => s.running)
  const byId = useMemo(() => indexNodes(scene), [scene])
  const labels = useMemo(() => labelNodes(scene), [scene])

  const [target, setTarget] = useState<Id>(NEW)
  const [drafts, setDrafts] = useState<Record<string, string>>({ [NEW]: STARTER.code })
  const [box, setBox] = useState<NewBox>({ size: STARTER.half.map((h) => 2 * h) as Vec3, lipschitz: STARTER.lipschitz, name: 'function' })
  const [description, setDescription] = useState(STARTER.description)
  const [check, setCheck] = useState<Check | null>(null)
  const [busy, setBusy] = useState(false)
  const [runError, setRunError] = useState('')
  const editor = useRef<CodeEditorHandle>(null)

  const node = target === NEW ? undefined : byId.get(target)
  const fn = node?.userFn
  const bound = !!fn
  const key = bound ? target : NEW
  const code = drafts[key] ?? fn?.code ?? ''
  const dirty = bound && drafts[key] !== undefined && drafts[key] !== fn.code

  const selKey = selection.join()
  const [seenSel, setSeenSel] = useState(selKey)
  if (selKey !== seenSel) {
    setSeenSel(selKey)
    const only = selection.length === 1 ? byId.get(selection[0]) : undefined
    if (only?.userFn && only.id !== target) {
      setTarget(only.id)
      setRunError('')
    }
  }
  const [seenRequest, setSeenRequest] = useState(props.editRequest?.seq)
  if (props.editRequest && props.editRequest.seq !== seenRequest) {
    setSeenRequest(props.editRequest.seq)
    setTarget(props.editRequest.id)
    setRunError('')
  }
  if (bound && drafts[key] !== undefined && drafts[key] === fn.code) {
    const { [key]: _, ...rest } = drafts
    setDrafts(rest)
  }

  useEffect(() => {
    let cancelled = false
    const timer = setTimeout(async () => {
      try {
        const r = (await callTool('check_function_code', { code }, 15_000, 'script')) as Check
        if (!cancelled) setCheck(r)
      } catch {
        if (!cancelled) setCheck(null)
      }
    }, 300)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [code])

  const edit = (next: string) => setDrafts((d) => ({ ...d, [key]: next }))
  const dropDraft = () =>
    setDrafts((d) => {
      const { [key]: _, ...rest } = d
      return rest
    })

  const run = async () => {
    if (busy || (bound && !dirty)) return
    setBusy(true)
    setRunError('')
    try {
      if (bound) {
        await callTool('set_function_code', { target, code }, 60_000, 'script')
      } else {
        const created = (await callTool(
          'create_function',
          {
            name: box.name.trim() || 'function',
            center: [0, 0, box.size[1] / 2],
            size: box.size,
            x_axis: [1, 0, 0],
            y_axis: [0, 0, 1],
            lipschitz: box.lipschitz,
            code,
          },
          60_000,
          'script',
        )) as { guid: string }
        setDrafts((d) => ({ ...d, [created.guid]: code }))
        setTarget(created.guid)
        await callTool('select', { refs: [created.guid] }, 15_000, 'script')
      }
    } catch (e) {
      setRunError(e instanceof ToolError ? e.message : (e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const loadExample = (name: string) => {
    const ex = findExample(name)
    if (!ex) return
    setTarget(NEW)
    setRunError('')
    setDrafts((d) => ({ ...d, [NEW]: ex.code }))
    setBox({ size: ex.half.map((h) => 2 * h) as Vec3, lipschitz: ex.lipschitz, name: ex.name })
    setDescription(ex.note ? `${ex.description} ${ex.note}` : ex.description)
  }

  const askAi = (request: string) => {
    const diagnostics = (check?.diagnostics ?? []).map((d) => `${d.line}:${d.column}: ${d.message}`).join('\n')
    const about = bound
      ? `the FFunction "${labels.get(target) ?? target}" (ref ${target})`
      : `a new FFunction (clip box size ${box.size.join(' x ')} ${units}, center [0, 0, ${box.size[1] / 2}], x_axis [1,0,0], y_axis [0,0,1], lipschitz ${box.lipschitz})`
    const prompt = [
      request.trim() || 'Fix the compile errors in this FFunction code.',
      '',
      `This is ${about}. My code in the Script tab${!bound || dirty ? ' (not applied yet)' : ''}:`,
      '```glsl',
      code.trimEnd(),
      '```',
      diagnostics ? `Compiler diagnostics:\n${diagnostics}` : '',
      bound
        ? 'Apply your version with set_function_code, then check it with capture_views.'
        : 'Create it with create_function, check it with capture_views, then select it.',
    ]
      .filter((l) => l !== '')
      .join('\n')
    if (bound) dropDraft()
    props.onOpenAi()
    void sendPrompt(prompt)
  }

  const title = bound ? (labels.get(target) ?? 'Function') : 'New function'

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex h-10 shrink-0 items-center gap-2 border-b px-3">
        <BrandIcon name="function" className="size-5" />
        <span className="min-w-0 flex-1 truncate font-bold" title={title}>
          {title}
        </span>
        {dirty && <Badge variant="secondary">edited</Badge>}
        <select
          value=""
          onChange={(e) => loadExample(e.target.value)}
          aria-label="Load an example"
          className="h-7 max-w-32 rounded-md border border-input bg-card px-1 text-xs font-semibold outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <option value="" disabled>
            Examples…
          </option>
          {EXAMPLES.map((e) => (
            <option key={e.name} value={e.name}>
              {e.title}
            </option>
          ))}
        </select>
        <Button variant="ghost" size="icon-xs" aria-label="New function" title="New function" onClick={() => setTarget(NEW)}>
          <FilePlus2 />
        </Button>
      </div>

      {!bound && description && <p className="type-small shrink-0 px-3 pt-2 text-muted-foreground">{description}</p>}

      <div className="flex min-h-56 flex-1 flex-col px-3 pt-2 pb-2">
        <CodeEditor ref={editor} value={code} onChange={edit} diagnostics={check?.diagnostics ?? []} onRun={run} className="flex-1" />
      </div>

      <div className="max-h-[45%] shrink-0 overflow-y-auto border-t bg-card">
        <div className="grid gap-2.5 px-3 py-3">
          <Status check={check} onGoTo={(line, column) => editor.current?.goTo(line, column)} />
          {!bound && <BoxFields box={box} units={units} onChange={setBox} />}
          <div className="flex gap-2">
            <Button className="flex-1" onClick={run} disabled={busy || (bound && !dirty)} title="Ctrl+Enter">
              {busy ? <Loader2 className="animate-spin" /> : <Play />}
              {bound ? (dirty ? 'Apply' : 'Applied') : 'Create function'}
            </Button>
            {dirty && (
              <Button variant="outline" onClick={dropDraft} title="Back to the function's current code">
                <RotateCcw /> Revert
              </Button>
            )}
          </div>
          {runError && <p className="type-small break-words whitespace-pre-wrap text-destructive">{runError}</p>}
          <AskAi hasErrors={!!check && !check.ok} disabled={aiRunning} onAsk={askAi} />
        </div>
        {bound && node && <PrimitiveSection key={node.id} node={node} labels={labels} hideCode />}
      </div>
    </div>
  )
}

function Status(props: { check: Check | null; onGoTo: (line: number, column: number) => void }) {
  const { check } = props
  if (!check) return <p className="type-small text-muted-foreground">Checking…</p>
  if (check.ok) {
    const params = check.params ?? []
    return (
      <p className="type-small flex items-start gap-1.5 text-muted-foreground">
        <CircleCheck className="mt-0.5 size-3.5 shrink-0 text-matcha-ink" />
        <span className="min-w-0 break-words">
          Compiles OK{params.length > 0 && ` · params: ${params.map((p) => p.name).join(', ')}`}
        </span>
      </p>
    )
  }
  return (
    <ul className="grid max-h-28 gap-1 overflow-y-auto" aria-label="Diagnostics">
      {check.diagnostics.map((d, i) => (
        <li key={i}>
          <button
            type="button"
            className="type-small flex w-full items-start gap-1.5 rounded-sm text-left text-destructive outline-none hover:underline focus-visible:ring-2 focus-visible:ring-ring"
            onClick={() => props.onGoTo(d.line, d.column)}
          >
            <CircleAlert className="mt-0.5 size-3.5 shrink-0" />
            <span className="min-w-0 break-words">
              <span className="type-numeric">
                {d.line}:{d.column}
              </span>{' '}
              {d.message}
            </span>
          </button>
        </li>
      ))}
    </ul>
  )
}

function BoxFields(props: { box: NewBox; units: string; onChange: (box: NewBox) => void }) {
  const { box } = props
  const number = (label: string, value: number, set: (v: number) => void, min = 0.001) => (
    <label className="grid gap-0.5">
      <span className="type-label text-muted-foreground">{label}</span>
      <Input
        type="number"
        value={value}
        min={min}
        step="any"
        onChange={(e) => {
          const v = Number(e.target.value)
          if (Number.isFinite(v) && v >= min) set(v)
        }}
        className="h-7 px-1.5 text-xs"
      />
    </label>
  )
  const setSize = (i: number) => (v: number) => props.onChange({ ...box, size: box.size.map((s, j) => (j === i ? v : s)) as Vec3 })
  return (
    <div className="grid gap-2">
      <div className="grid grid-cols-4 gap-1.5">
        {number('Width', box.size[0], setSize(0))}
        {number('Height', box.size[1], setSize(1))}
        {number('Depth', box.size[2], setSize(2))}
        {number('Lipschitz', box.lipschitz, (v) => props.onChange({ ...box, lipschitz: v }), 0.01)}
      </div>
      <div className="flex items-center gap-2">
        <span className="type-label shrink-0 text-muted-foreground">Name</span>
        <Input value={box.name} onChange={(e) => props.onChange({ ...box, name: e.target.value })} className="h-7 text-xs" />
      </div>
      <p className="type-small text-muted-foreground">
        Clip box in {props.units || 'model units'}, standing on the ground at the origin. Local Y (the DSL's up) is world Z.
      </p>
    </div>
  )
}

function AskAi(props: { hasErrors: boolean; disabled: boolean; onAsk: (request: string) => void }) {
  const [text, setText] = useState('')
  const submit = () => {
    if (props.disabled || (!text.trim() && !props.hasErrors)) return
    props.onAsk(text)
    setText('')
  }
  return (
    <form
      className="flex gap-1.5"
      onSubmit={(e) => {
        e.preventDefault()
        submit()
      }}
    >
      <Input
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder={props.hasErrors ? 'Fix with AI, or describe a change…' : 'Ask AI to change this code…'}
        className="h-8 text-xs"
      />
      <Button type="submit" variant="outline" disabled={props.disabled || (!text.trim() && !props.hasErrors)}>
        <Sparkles /> {props.hasErrors && !text.trim() ? 'Fix' : 'Ask'}
      </Button>
    </form>
  )
}
