import { X } from 'lucide-react'
import { useEffect, useMemo } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ScrollArea } from '@/components/ui/scroll-area'
import { Separator } from '@/components/ui/separator'
import { Inspector } from '@/components/inspector'
import { SceneTree } from '@/components/scene-tree'
import { dismissNotice, send, useHost } from '@/bridge/host'
import { indexNodes, labelNodes } from '@/lib/scene'
import { cn } from '@/lib/utils'

export default function App() {
  const host = useHost((s) => s.host)
  const synced = useHost((s) => s.synced)
  const scene = useHost((s) => s.scene)
  const selection = useHost((s) => s.selection)
  const theme = useHost((s) => s.theme)

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark')
  }, [theme])

  const labels = useMemo(() => labelNodes(scene), [scene])
  const byId = useMemo(() => indexNodes(scene), [scene])
  const selectedSet = useMemo(() => new Set(selection), [selection])
  const selected = useMemo(() => selection.map((id) => byId.get(id)).filter((n) => !!n), [selection, byId])

  return (
    <div className="relative flex h-dvh flex-col overflow-hidden text-xs">
      <header className="flex h-8 shrink-0 items-center gap-2 px-3">
        <h1 className="font-semibold">Freppuccino</h1>
        {host === 'mock' && <Badge variant="outline">Mock host</Badge>}
        {!synced && <span className="text-muted-foreground">connecting…</span>}
      </header>
      <Separator />

      <div className="flex h-7 shrink-0 items-center px-3 text-muted-foreground">
        <span className="font-medium tracking-wide uppercase text-[0.625rem]">Browser</span>
        <span className="ml-auto tabular-nums">{byId.size}</span>
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

      <Separator />
      <div className="max-h-[65%] shrink-0 overflow-y-auto">
        <Inspector selected={selected} labels={labels} />
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
        'absolute inset-x-2 bottom-2 flex items-start gap-2 rounded-md border bg-popover px-3 py-2 text-popover-foreground shadow-md',
        notice.level === 'error' && 'border-destructive/50 text-destructive',
      )}
    >
      <span className="flex-1">{notice.text}</span>
      <Button variant="ghost" size="icon-xs" aria-label="Dismiss" onClick={() => dismissNotice(notice.id)}>
        <X />
      </Button>
    </div>
  )
}
