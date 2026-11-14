import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The built app is served by the Rhino side panel (WebView2) from https://freppuccino.ui/
// (see FrepPanel.cs). `npm run dev` serves the same app in a plain browser against a mock host,
// or inside Rhino with FREPPUCCINO_UI_DEV_URL=http://localhost:5173 for hot reload.
export default defineConfig({
  base: './',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, './src') },
  },
  server: { port: 5173, strictPort: true },
})
