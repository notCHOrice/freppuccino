import { forwardRef, useImperativeHandle, useRef, useState } from 'react'
import { cn } from '@/lib/utils'

const LINE_PX = 20
const INDENT = '    '

export interface Diagnostic {
  line: number
  column: number
  message: string
}

export interface CodeEditorHandle {
  goTo(line: number, column?: number): void
}

function insert(ta: HTMLTextAreaElement, text: string) {
  ta.focus()
  if (!document.execCommand('insertText', false, text)) ta.setRangeText(text, ta.selectionStart, ta.selectionEnd, 'end')
}

export const CodeEditor = forwardRef<
  CodeEditorHandle,
  { value: string; onChange: (code: string) => void; diagnostics: Diagnostic[]; onRun?: () => void; className?: string }
>(function CodeEditor(props, ref) {
  const { value, onChange, diagnostics, onRun } = props
  const ta = useRef<HTMLTextAreaElement>(null)
  const [scrollTop, setScrollTop] = useState(0)
  const lines = value.split('\n')
  const byLine = new Map<number, string[]>()
  for (const d of diagnostics) byLine.set(d.line, [...(byLine.get(d.line) ?? []), `${d.line}:${d.column}: ${d.message}`])
  const gutter = `calc(${String(lines.length).length + 1}ch + 0.5rem)`

  useImperativeHandle(ref, () => ({
    goTo(line, column = 1) {
      const el = ta.current
      if (!el) return
      const start = lines.slice(0, Math.max(0, line - 1)).reduce((n, l) => n + l.length + 1, 0) + Math.max(0, column - 1)
      el.focus()
      el.setSelectionRange(start, start)
      el.scrollTop = Math.max(0, (line - 3) * LINE_PX)
    },
  }))

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    const el = e.currentTarget
    const mod = e.ctrlKey || e.metaKey
    if (mod && (e.key === 'Enter' || e.key.toLowerCase() === 's')) {
      e.preventDefault()
      onRun?.()
      return
    }
    const { selectionStart: s, selectionEnd: t } = el
    const lineStart = el.value.lastIndexOf('\n', s - 1) + 1
    if (e.key === 'Tab') {
      e.preventDefault()
      const multi = el.value.slice(s, t).includes('\n')
      if (!multi && !e.shiftKey) {
        insert(el, INDENT)
        return
      }
      const blockEnd = t > s && el.value[t - 1] === '\n' ? t - 1 : t
      const block = el.value.slice(lineStart, blockEnd)
      const next = block
        .split('\n')
        .map((l) => (e.shiftKey ? l.replace(/^( {1,4}|\t)/, '') : INDENT + l))
        .join('\n')
      el.setSelectionRange(lineStart, blockEnd)
      insert(el, next)
      el.setSelectionRange(lineStart, lineStart + next.length)
      return
    }
    if (e.key === 'Enter' && !e.shiftKey && !mod) {
      e.preventDefault()
      const current = el.value.slice(lineStart, s)
      const indent = current.match(/^[ \t]*/)![0]
      const opens = /\{\s*$/.test(current)
      const closes = /^\s*\}/.test(el.value.slice(s, el.value.indexOf('\n', s) === -1 ? undefined : el.value.indexOf('\n', s)))
      if (opens && closes) {
        insert(el, `\n${indent}${INDENT}\n${indent}`)
        const caret = s + 1 + indent.length + INDENT.length
        el.setSelectionRange(caret, caret)
        return
      }
      insert(el, `\n${indent}${opens ? INDENT : ''}`)
      return
    }
    if (e.key === '}' && /^[ \t]+$/.test(el.value.slice(lineStart, s)) && s === t) {
      e.preventDefault()
      const indent = el.value.slice(lineStart, s)
      el.setSelectionRange(lineStart, s)
      insert(el, indent.slice(0, Math.max(0, indent.length - INDENT.length)) + '}')
    }
  }

  return (
    <div
      className={cn(
        'flex min-h-0 overflow-hidden rounded-md border border-input bg-card font-mono text-xs focus-within:ring-2 focus-within:ring-ring',
        props.className,
      )}
    >
      <div className="relative shrink-0 overflow-hidden border-r border-border bg-muted text-right text-muted-foreground tabular-nums select-none" style={{ width: gutter }}>
        <div className="py-1" style={{ transform: `translateY(${-scrollTop}px)`, lineHeight: `${LINE_PX}px` }}>
          {lines.map((_, i) => {
            const issues = byLine.get(i + 1)
            return (
              <div key={i} title={issues?.join('\n')} className={cn('pr-2', issues && 'font-bold text-destructive')}>
                {i + 1}
              </div>
            )
          })}
        </div>
      </div>
      <div className="relative min-w-0 flex-1">
        <div aria-hidden className="pointer-events-none absolute inset-x-0 top-0 py-1" style={{ transform: `translateY(${-scrollTop}px)` }}>
          {lines.map((_, i) => (
            <div key={i} style={{ height: LINE_PX }} className={cn(byLine.has(i + 1) && 'bg-destructive/12')} />
          ))}
        </div>
        <textarea
          ref={ta}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={onKeyDown}
          onScroll={(e) => setScrollTop(e.currentTarget.scrollTop)}
          spellCheck={false}
          autoCapitalize="off"
          autoCorrect="off"
          wrap="off"
          aria-label="FFunction code"
          className="absolute inset-0 size-full resize-none overflow-auto bg-transparent py-1 pr-3 pl-2 whitespace-pre text-foreground outline-none"
          style={{ lineHeight: `${LINE_PX}px`, tabSize: 4 }}
        />
      </div>
    </div>
  )
})
