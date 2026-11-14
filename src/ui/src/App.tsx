import { X } from 'lucide-react'
import { useEffect, useMemo } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Inspector } from '@/components/inspector'
import { SceneTree } from '@/components/scene-tree'
import { dismissNotice, send, useHost } from '@/bridge/host'
import { indexNodes, labelNodes } from '@/lib/scene'
import { cn } from '@/lib/utils'
import appIcon from '@/brand/app-icon.svg'
import wordmark from '@/brand/wordmark.svg?raw'

export default function App() {
  const host = useHost((s) => s.host)
  const synced = useHost((s) => s.synced)
  const scene = useHost((s) => s.scene)
  const selection = useHost((s) => s.selection)
  const theme = useHost((s) => s.theme)

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark')
  }, [theme])

  // Esc in the panel deselects like Esc in the viewport (inputs keep Esc for themselves).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || (e.target as HTMLElement | null)?.closest('input, textarea')) return
      send({ type: 'select', ids: [], mode: 'replace' })
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const labels = useMemo(() => labelNodes(scene), [scene])
  const byId = useMemo(() => indexNodes(scene), [scene])
  const roots = useMemo(() => new Set(scene.map((n) => n.id)), [scene])
  const selectedSet = useMemo(() => new Set(selection), [selection])
  const selected = useMemo(() => selection.map((id) => byId.get(id)).filter((n) => !!n), [selection, byId])

  return (
    <div className="relative flex h-dvh flex-col overflow-hidden">
      <header className="flex h-12 shrink-0 items-center gap-2 border-b px-4">
        <img src={appIcon} alt="" className="size-6" />
        {/* Wordmark: ink = currentColor, "FRep" = --wordmark-accent (caramel / caramel-ink per theme) */}
        <span
          role="img"
          aria-label="FRepuccino"
          className="h-4 text-ink [&>svg]:h-full [&>svg]:w-auto"
          dangerouslySetInnerHTML={{ __html: wordmark }}
        />
        <span className="ml-auto flex items-center gap-1.5">
          {host === 'mock' && <Badge variant="new">Mock host</Badge>}
          {!synced && <span className="type-small text-muted-foreground">connecting…</span>}
        </span>
      </header>

      <div className="flex h-9 shrink-0 items-center px-4 text-muted-foreground">
        <span className="type-label">Browser</span>
        <span className="type-numeric ml-auto">{byId.size}</span>
      </div>
      <ScrollArea className="min-h-24 flex-1">
        <SceneTree
          nodes={scene}
          labels={labels}
          selection={selectedSet}
          onSelect={(ids, mode) => send({ type: 'select', ids, mode })}
          onSetVisible={(id, visible) => send({ type: 'setVisible', id, visible })}
        />
      </ScrollArea>

      <div className="max-h-[65%] shrink-0 overflow-y-auto border-t bg-card">
        <Inspector selected={selected} labels={labels} roots={roots} />
      </div>

      <NoticeToast />
    </div>
  )
}

function NoticeToast() {
  const notice = useHost((s) => s.notice)
  useEffect(() => {
    if (!notice) return
    const t = setTimeout(() => dismissNotice(notice.id), 4000)
    return () => clearTimeout(t)
  }, [notice])
  if (!notice) return null
  return (
    <div
      role="status"
      className={cn(
        'type-small absolute inset-x-3 bottom-3 flex items-center gap-2 rounded-full border-2 border-ink bg-popover py-1.5 pr-1.5 pl-4 text-popover-foreground shadow-float',
        notice.level === 'error' && 'border-destructive text-destructive',
      )}
    >
      <span className="flex-1">{notice.text}</span>
      <Button variant="ghost" size="icon-xs" className="rounded-full" aria-label="Dismiss" onClick={() => dismissNotice(notice.id)}>
        <X />
      </Button>
    </div>
  )
}
