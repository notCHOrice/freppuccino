import { useSyncExternalStore } from 'react'
import { createMockHost } from './mock-host'
import type { Guid, HostMessage, SceneNode, Theme, UiMessage, UiSettings } from './protocol'

// ── Transport ──
// Inside Rhino (Windows) the page runs in WebView2: window.chrome.webview.
// In a plain browser (`npm run dev`) a mock host stands in so the UI can be built without Rhino.
// TODO(macOS): Rhino's Eto WebView is WKWebView there → window.webkit.messageHandlers + evaluateJavaScript.

interface WebView2 {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void
}
declare global {
  interface Window {
    chrome?: { webview?: WebView2 }
  }
}

export interface Transport {
  kind: 'rhino' | 'mock'
  post(message: UiMessage): void
  listen(onMessage: (message: HostMessage) => void): void
}

function createTransport(): Transport {
  const webview = window.chrome?.webview
  if (!webview) return createMockHost()
  return {
    kind: 'rhino',
    post: (m) => webview.postMessage(m),
    listen: (cb) => webview.addEventListener('message', (e) => cb(e.data as HostMessage)),
  }
}

// ── Store ──

export interface Notice {
  id: number
  level: 'info' | 'warning' | 'error'
  text: string
}

export interface HostState {
  host: Transport['kind']
  /** false until the first "state" arrives */
  synced: boolean
  scene: SceneNode[]
  /** selected ids in pick order (oldest first); Rhino doesn't report order, so the store keeps it */
  selection: Guid[]
  settings: UiSettings
  theme: Theme
  notice: Notice | null
}

const transport = createTransport()

let state: HostState = {
  host: transport.kind,
  synced: false,
  scene: [],
  selection: [],
  settings: { blend: 0, units: '' },
  theme: window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
  notice: null,
}
const listeners = new Set<() => void>()
let noticeId = 0

function set(patch: Partial<HostState>) {
  state = { ...state, ...patch }
  listeners.forEach((l) => l())
}

/** Keeps ids that stay selected in their old order and appends newly selected ones. */
function orderSelection(prev: Guid[], next: Guid[]): Guid[] {
  const nextSet = new Set(next)
  const kept = prev.filter((id) => nextSet.has(id))
  const keptSet = new Set(kept)
  return [...kept, ...next.filter((id) => !keptSet.has(id))]
}

transport.listen((m) => {
  switch (m.type) {
    case 'state':
      set({ synced: true, scene: m.scene, selection: m.selection, settings: m.settings, theme: m.theme })
      break
    case 'scene':
      set({ scene: m.scene })
      break
    case 'selection':
      set({ selection: orderSelection(state.selection, m.ids) })
      break
    case 'settings':
      set({ settings: m.settings })
      break
    case 'notice':
      set({ notice: { id: ++noticeId, level: m.level, text: m.text } })
      break
  }
})

export function send(message: UiMessage) {
  // Optimistic: the host doesn't echo values the page just set.
  if (message.type === 'setBlend') set({ settings: { ...state.settings, blend: message.k } })
  transport.post(message)
}

export function dismissNotice(id: number) {
  if (state.notice?.id === id) set({ notice: null })
}

export function useHost<T>(selector: (s: HostState) => T): T {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    () => selector(state),
  )
}

/** Call once at startup; the host answers with a full "state". */
export function connect() {
  transport.post({ type: 'ready' })
}
