import { ChevronRight, Eye, EyeOff, Lock } from 'lucide-react'
import { useState, type MouseEvent } from 'react'
import { cn } from '@/lib/utils'
import { NodeIcon } from '@/components/node-icon'
import type { Id, SceneNode, SelectMode } from '@/bridge/protocol'

/**
 * Browser tree (Fusion 360 browser / CATIA spec tree). Primitives are leaves; operation nodes
 * nest their operands. Click selects, Ctrl/Cmd+click toggles, Shift+click adds.
 * Shared operands (DAG) show under each parent and expand/collapse together.
 */
export function SceneTree(props: {
  nodes: SceneNode[]
  labels: Map<string, string>
  selection: Set<Id>
  onSelect: (ids: Id[], mode: SelectMode) => void
  onSetVisible: (id: Id, visible: boolean) => void
}) {
  // Collapsed rather than expanded, so new operation nodes start open.
  const [collapsed, setCollapsed] = useState<Set<Id>>(new Set())
  const toggle = (id: Id) =>
    setCollapsed((prev) => {
      const next = new Set(prev)
      if (!next.delete(id)) next.add(id)
      return next
    })

  if (props.nodes.length === 0)
    return <p className="px-3 py-6 text-center text-muted-foreground">No F-rep objects yet. Create one below.</p>

  return (
    <ul role="tree" aria-multiselectable className="py-1">
      {props.nodes.map((node) => (
        <TreeRow key={node.id} node={node} depth={0} {...props} collapsed={collapsed} onToggle={toggle} />
      ))}
    </ul>
  )
}

function TreeRow(props: {
  node: SceneNode
  depth: number
  labels: Map<string, string>
  selection: Set<Id>
  collapsed: Set<Id>
  onToggle: (id: Id) => void
  onSelect: (ids: Id[], mode: SelectMode) => void
  onSetVisible: (id: Id, visible: boolean) => void
  /** subtract operands after the first are tools */
  role?: 'tool'
}) {
  const { node, depth, selection, collapsed } = props
  const hasChildren = !!node.children?.length
  const open = hasChildren && !collapsed.has(node.id)
  const selected = selection.has(node.id)

  const onClick = (e: MouseEvent) => {
    const mode: SelectMode = e.ctrlKey || e.metaKey ? 'toggle' : e.shiftKey ? 'add' : 'replace'
    props.onSelect([node.id], mode)
  }

  return (
    <li role="treeitem" aria-selected={selected} aria-expanded={hasChildren ? open : undefined}>
      <div
        onClick={onClick}
        className={cn(
          'group flex h-6 cursor-default items-center gap-1 pr-1 select-none hover:bg-muted',
          selected && 'bg-primary/10 hover:bg-primary/15',
          !node.visible && 'text-muted-foreground',
          node.missing && 'italic line-through',
        )}
        style={{ paddingLeft: 4 + depth * 14 }}
      >
        <button
          type="button"
          aria-label={open ? 'Collapse' : 'Expand'}
          tabIndex={-1}
          onClick={(e) => (e.stopPropagation(), props.onToggle(node.id))}
          className={cn('grid size-4 place-items-center rounded-sm hover:bg-foreground/10', !hasChildren && 'invisible')}
        >
          <ChevronRight className={cn('size-3 transition-transform', open && 'rotate-90')} />
        </button>
        <NodeIcon node={node} className={cn('size-3.5 shrink-0', node.kind === 'operation' ? 'text-primary' : 'text-muted-foreground')} />
        <span className={cn('truncate', node.kind === 'operation' && 'font-medium')}>
          {props.role === 'tool' && <span className="text-muted-foreground">− </span>}
          {props.labels.get(node.id)}
        </span>
        <span className="ml-auto flex items-center gap-0.5">
          {node.locked && <Lock className="size-3 text-muted-foreground" />}
          {!node.missing && <button
            type="button"
            aria-label={node.visible ? 'Hide' : 'Show'}
            onClick={(e) => (e.stopPropagation(), props.onSetVisible(node.id, !node.visible))}
            className={cn(
              'grid size-5 place-items-center rounded-sm hover:bg-foreground/10',
              node.visible && 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100',
            )}
          >
            {node.visible ? <Eye className="size-3" /> : <EyeOff className="size-3" />}
          </button>}
        </span>
      </div>
      {open && (
        <ul role="group">
          {node.children!.map((child, i) => (
            <TreeRow
              key={`${i}:${child.id}`}
              {...props}
              node={child}
              depth={depth + 1}
              role={node.op === 'subtract' && i > 0 ? 'tool' : undefined}
            />
          ))}
        </ul>
      )}
    </li>
  )
}
