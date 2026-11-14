import { useId, useState } from 'react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Slider } from '@/components/ui/slider'

/**
 * Label + numeric field + slider. onChange fires while dragging (use for cheap live updates),
 * onCommit on release / Enter / blur (use for anything that writes to the document).
 * Typed values may exceed the slider range.
 */
export function ParamSlider(props: {
  label: string
  value: number
  min?: number
  max: number
  step?: number
  /** Rhino unit system name ("Millimeters") or a word like "sharp"; shown after the number */
  units?: string
  disabled?: boolean
  onChange?: (value: number) => void
  onCommit?: (value: number) => void
}) {
  const { min = 0, step = 0.01 } = props
  const id = useId()
  const [value, setValue] = useState(props.value)
  const [text, setText] = useState(format(props.value))
  // Follow the prop when the host changes it (undo, command line edits).
  const [prop, setProp] = useState(props.value)
  if (props.value !== prop) {
    setProp(props.value)
    setValue(props.value)
    setText(format(props.value))
  }

  const update = (v: number) => {
    setValue(v)
    setText(format(v))
    props.onChange?.(v)
  }
  const commitText = () => {
    const v = Number(text)
    if (!Number.isFinite(v) || v < min || v === value) return setText(format(value))
    update(v)
    props.onCommit?.(v)
  }

  return (
    <div className="grid gap-2">
      <div className="flex items-center justify-between gap-2">
        <Label htmlFor={id} className="type-label min-w-0 truncate text-muted-foreground">
          {props.label}
        </Label>
        <div className="flex items-center gap-1.5">
          <Input
            id={id}
            inputMode="decimal"
            value={text}
            disabled={props.disabled}
            onChange={(e) => setText(e.target.value)}
            onBlur={commitText}
            onKeyDown={(e) => e.key === 'Enter' && (e.currentTarget as HTMLInputElement).blur()}
            className="type-numeric h-7 w-16 px-1.5 text-right"
          />
          {props.units && <span className="type-numeric w-8 text-muted-foreground">{abbreviate(props.units)}</span>}
        </div>
      </div>
      <Slider
        min={min}
        max={Math.max(props.max, value)}
        step={step}
        value={[value]}
        disabled={props.disabled}
        onValueChange={([v]) => update(v)}
        onValueCommit={([v]) => props.onCommit?.(v)}
      />
    </div>
  )
}

const format = (v: number) => String(Math.round(v * 1000) / 1000)

const UNIT_ABBREVIATIONS: Record<string, string> = {
  Millimeters: 'mm', Centimeters: 'cm', Meters: 'm', Kilometers: 'km', Microns: 'µm',
  Inches: 'in', Feet: 'ft', Yards: 'yd', Miles: 'mi',
}
const abbreviate = (units: string) => UNIT_ABBREVIATIONS[units] ?? units
