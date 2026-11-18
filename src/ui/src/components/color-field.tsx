import { useEffect, useId, useRef, useState } from 'react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

const HEX = /^#?([0-9a-f]{6})$/i

/** "#rrggbb" / "rrggbb" → "#RRGGBB", or null. */
function normalizeHex(text: string): string | null {
  const m = HEX.exec(text.trim())
  return m ? `#${m[1].toUpperCase()}` : null
}

/**
 * Label + swatch of the current color (opens the native color picker) + hex field.
 * onChange fires while the picker is being dragged (cheap live updates); onCommit when it closes / a hex is
 * typed (Enter / blur). Every picker gesture that fired onChange ends with one onCommit.
 */
export function ColorField(props: {
  label: string
  value: string
  disabled?: boolean
  onChange?: (value: string) => void
  onCommit?: (value: string) => void
}) {
  const id = useId()
  const [value, setValue] = useState(props.value)
  const [text, setText] = useState(props.value)
  /** picker open and moved: host echoes of earlier previews must not yank it back */
  const [dragging, setDragging] = useState(false)
  // Follow the prop when the host changes it (undo, another edit).
  const [prop, setProp] = useState(props.value)
  if (props.value !== prop && !dragging) {
    setProp(props.value)
    setValue(props.value)
    setText(props.value)
  }

  const show = (v: string) => {
    setValue(v)
    setText(v)
  }
  const commit = (v: string) => {
    setDragging(false)
    setProp(props.value)
    show(v)
    props.onCommit?.(v)
  }
  const commitText = () => {
    const v = normalizeHex(text)
    if (!v || v === value) return setText(value)
    commit(v)
  }

  // React's onChange on <input type="color"> fires on every "input"; the native "change" fires once when the picker closes.
  const picker = useRef<HTMLInputElement>(null)
  const onCommit = useRef(commit)
  useEffect(() => {
    onCommit.current = commit
  })
  useEffect(() => {
    const el = picker.current
    if (!el) return
    const done = () => onCommit.current(normalizeHex(el.value) ?? el.value)
    el.addEventListener('change', done)
    return () => el.removeEventListener('change', done)
  }, [])

  return (
    <div className="grid gap-2">
      <div className="flex items-center justify-between gap-2">
        <Label htmlFor={id} className="type-label min-w-0 truncate text-muted-foreground">
          {props.label}
        </Label>
        <div className="flex items-center gap-1.5">
          <input
            ref={picker}
            type="color"
            aria-label={`${props.label} picker`}
            value={value.toLowerCase()}
            disabled={props.disabled}
            onChange={(e) => {
              const v = normalizeHex(e.target.value)
              if (!v) return
              show(v)
              setDragging(true)
              props.onChange?.(v)
            }}
            className="h-7 w-9 cursor-pointer rounded-md border border-input bg-card p-0.5 disabled:cursor-not-allowed disabled:opacity-50"
          />
          <Input
            id={id}
            value={text}
            spellCheck={false}
            maxLength={7}
            disabled={props.disabled}
            onChange={(e) => setText(e.target.value)}
            onBlur={commitText}
            onKeyDown={(e) => e.key === 'Enter' && (e.currentTarget as HTMLInputElement).blur()}
            className="type-numeric h-7 w-20 px-1.5 text-right uppercase"
          />
        </div>
      </div>
    </div>
  )
}
