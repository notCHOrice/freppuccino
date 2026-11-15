/// <reference types="vite/client" />

// src/ui/.env.local (gitignored); see .env.example and docs/ai.md
interface ImportMetaEnv {
  readonly VITE_OPENROUTER_API_KEY?: string
  readonly VITE_OPENROUTER_MODEL?: string
  readonly VITE_OPENROUTER_BASE_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
