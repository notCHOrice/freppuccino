# Freppuccino side panel UI

React + Vite + TypeScript + Tailwind 4 + shadcn/ui, hosted in Rhino by `src/Freppuccino/Ui` (WebView2).

```sh
npm install
npm run dev     # http://localhost:5173 — runs against a mock Rhino host
npm run build   # → dist/, copied into the plugin output by dotnet build
npm run lint
```

Add shadcn components with `npx shadcn@latest add <name>`.

Architecture, protocol, and the browser-tree design: [docs/side-panel.md](../../docs/side-panel.md).
