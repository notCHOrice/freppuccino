# Side panel (WebView2 + React)

A dockable Rhino panel (`FrepPanel`) that hosts a React app in WebView2. It has a browser tree of F-rep objects at the top and a selection-driven inspector below it.

```
src/Freppuccino/Ui/
  FrepPanel.cs       Eto panel, registered in FreppuccinoPlugin.OnLoad; command FrepPanel opens it
  WebViewHost.cs     WebView2 plumbing: serves src/ui/dist, JSON postMessage both ways, blocks foreign navigation
  UiController.cs    Rhino events → snapshots for the page; page requests → Rhino (whitelisted)
  UiProtocol.cs      message types  ◄── keep in sync ──►  src/ui/src/bridge/protocol.ts
src/ui/              Vite + React 19 + TypeScript + Tailwind 4 + shadcn/ui (radix, "mira" preset), lucide icons
  src/bridge/        host.ts (transport + store + useHost), mock-host.ts (fake Rhino for the browser)
  src/components/    scene-tree.tsx (browser), inspector.tsx (create / primitive / combine), param-slider.tsx
  src/lib/scene.ts   display names, icons, labels per SDF type / operation
```

## Why Eto's WebView and not a raw WebView2 control

On Windows, Rhino 9's Eto `WebView` *is* WebView2 (`Eto.Wpf` `WebView2Handler`, runtime 1.0.1938.49 ships in `Rhino 9\System`). `WebViewHost` gets the `CoreWebView2` through the native control by reflection and compiles only against `Microsoft.Web.WebView2.Core` (compile-only package reference). So:

- the project stays `net10.0` with no WPF dependency, and still builds on macOS
- the `postMessage` / `SetVirtualHostNameToFolderMapping` APIs are available, so no URL-scheme hacks and no `file://` module/CORS issues

macOS: Eto's WebView is WKWebView. The panel currently shows a placeholder there. To support it: add a `WKScriptMessageHandler` bridge in C#, and a `window.webkit.messageHandlers` transport in `bridge/host.ts`.

## Dev workflow

| Goal | Do |
|---|---|
| Work on the UI without Rhino | `cd src/ui && npm install && npm run dev`, then open http://localhost:5173. A **mock host** stands in for Rhino; its sample scene includes nested operations. |
| Hot reload inside Rhino | Run `npm run dev`, then start the **"Rhino 9 + Freppuccino (UI dev server)"** launch config, which sets `FREPPUCCINO_UI_DEV_URL=http://localhost:5173`. |
| Normal plugin build | `dotnet build` copies `src/ui/dist` to `bin/.../ui/`. It runs `npm run build` itself only for Release builds or when `dist` is missing; otherwise run `npm run build` after UI changes. Pass `-p:SkipUiBuild=true` to skip. |
| Debug the page in Rhino | In Debug builds, F12 or right-click → Inspect opens the WebView2 DevTools. |

In Rhino, run `FrepPanel`, or open the Freppuccino panel from the panels menu.

## Protocol

JSON messages, each with a `type`. Host → page messages are snapshots (simple, and fine at this scale). Page → host messages are requests. Selection order is tracked on the page, because Rhino doesn't report pick order; it matters for subtract (target vs tool).

| Host → page | When |
|---|---|
| `state {scene, selection, settings, theme}` | reply to `ready`; active document changed |
| `scene {scene}` | object added/deleted/replaced/attributes changed (coalesced on Idle) |
| `selection {ids}` | Rhino selection changed |
| `settings {settings}` | blend changed (also from the `FrepBlend` command) |
| `notice {level, text}` | feedback, including "not implemented yet" from stubs |

| Page → host | Status |
|---|---|
| `ready` | ✅ |
| `select {ids, mode: replace\|add\|toggle}` | ✅ tree click → Rhino selection |
| `setVisible {id, visible}` | ✅ Show/Hide |
| `runCommand {command}` | ✅ whitelist only: `FBox`, `FSphere`, `Freppuccino` |
| `setBlend {k}` | ✅ the existing global `FrepSettings.Blend` |
| `setParam {id, name, value}` | **stub**: edit `SDF.<name>` on a primitive |
| `applyOperation {a, b, op, k}` | **stub**: combine two selected objects |
| `setOperation {id, op, k}` | **stub**: edit an existing operation node |

`op` ∈ `union | subtract | intersect`. Every op takes a blend `k` (0 = sharp, > 0 = smooth). For subtract, A/children[0] is the target and the rest are tools.

## Browser tree (Fusion 360 browser / CATIA spec tree)

The UI already renders a general tree; `SceneNode` is recursive:

```
SceneNode { id, kind: primitive|operation, name?, visible, locked,
            sdfType?, params?,          // primitive
            op?, k?, children? }        // operation: children are operands, in order
```

Today the host sends a flat list of primitives. Once operations exist, `UiController.BuildScene` walks the graph and nests operands under their operation; nothing else in the page needs to change. Run `npm run dev` to see the nested case with the mock data. Selecting an operation node in the tree switches the inspector to the same combine UI, which then sends `setOperation`.

Example of what the tree would show:

```
▾ ⊟ Subtract 1          ← root = a visible result
    ▢ Box 1             ← target
  ▾ ⊕ − Union 1         ← tool (nested operation)
      ○ Hole A
      ○ Sphere 2
  ○ Sphere 1            ← un-combined primitive
```

### Open question: where operation nodes live

| Option | Pros | Cons |
|---|---|---|
| **A. Operation = Rhino object** (a small proxy, e.g. a hidden point or result bounding box) with user text `SDF.Type=opSubtract`, `SDF.k`, `SDF.Children=<id>,<id>` | Same model as primitives (the PLAN says "F-reps are normal Rhino objects"). Undo/redo, save, layers, names, selection and gumball (move the whole result) all come from Rhino. | Child ids must be remapped on copy/paste (same problem as `SdfTransformWatcher`'s copy case). Deleting an operand needs a cleanup rule. The proxy geometry is a little artificial. |
| **B. Document-level graph** (JSON in `doc.Strings` or plugin document user data) referencing Rhino object ids | One place to read the whole tree. No fake geometry. | A second source of truth next to user text. Undo and copy/paste between files need manual handling. Operation nodes can't be picked or gumballed in the viewport. |
| **C. Operation = block instance** whose definition holds the operands | Rhino-native nesting and transforms. | Editing an operand means BlockEdit, which is clunky compared with Fusion/CATIA. Transform and SDF bookkeeping get harder. |

Recommendation: **A**. It keeps a single source of truth (user text) and gets most behaviour for free from Rhino. Consumed operands would be hidden from the root of the tree, and from the flat shader union, because their parent draws them.

### Rendering impact

`FrepShaderGen` currently `smin`s every primitive with one global k. With a graph, `map()` is generated by walking the tree (`opU/opS/opI` with per-node k). The tree's *shape* becomes part of the shader cache key; per-node k values go in the uniform block next to the primitives, so dragging a blend slider never recompiles.
