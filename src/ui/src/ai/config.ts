// AI settings. See docs/ai.md.
// API key, in order: one pasted in the AI tab (kept in the panel's localStorage), else VITE_OPENROUTER_API_KEY from
// src/ui/.env.local, baked in at build time by Vite.

/** Any OpenAI-compatible chat completions endpoint works (a proxy, a local test server); OpenRouter by default. */
export const OPENROUTER_BASE_URL: string = import.meta.env.VITE_OPENROUTER_BASE_URL || 'https://openrouter.ai/api/v1'

const ENV_KEY: string = import.meta.env.VITE_OPENROUTER_API_KEY ?? ''
const API_KEY_STORAGE = 'freppuccino.ai.apiKey'
/** Fallback when localStorage is unavailable: the pasted key lasts for this session. */
let sessionKey = ''

export type KeySource = 'pasted' | 'env' | 'none'

function storedKey(): string {
  try {
    return localStorage.getItem(API_KEY_STORAGE) ?? sessionKey
  } catch {
    return sessionKey
  }
}

export function getApiKey(): string {
  return storedKey() || ENV_KEY
}

export function keySource(): KeySource {
  return storedKey() ? 'pasted' : ENV_KEY ? 'env' : 'none'
}

/** Empty string removes the pasted key (falls back to the env key, if any). */
export function storeApiKey(key: string) {
  sessionKey = key
  try {
    if (key) localStorage.setItem(API_KEY_STORAGE, key)
    else localStorage.removeItem(API_KEY_STORAGE)
  } catch {
    // storage unavailable: sessionKey covers this session
  }
}

/** Needs vision (screenshots) + tool calling. */
export const DEFAULT_MODEL: string = import.meta.env.VITE_OPENROUTER_MODEL || 'anthropic/claude-sonnet-5.5'

/** Suggestions for the model picker; any OpenRouter id with image input + tools works. */
export const MODEL_PRESETS = [
  'anthropic/claude-sonnet-5.5',
  'anthropic/claude-opus-5.5',
  'anthropic/claude-haiku-5.5',
  'google/gemini-4-carbon',
  'google/gemini-3.8-flash',
]

/** Model turns per prompt before the agent stops and asks the user to continue. */
export const MAX_STEPS = 40

/** Screenshots older than the newest N capture results are replaced by a text stub to keep requests small. */
export const KEEP_SCREENSHOT_SETS = 2

const MODEL_KEY = 'freppuccino.ai.model'

export function loadModel(): string {
  try {
    return localStorage.getItem(MODEL_KEY) || DEFAULT_MODEL
  } catch {
    return DEFAULT_MODEL
  }
}

export function saveModel(model: string) {
  try {
    localStorage.setItem(MODEL_KEY, model)
  } catch {
    // storage unavailable: the choice lasts for this session only
  }
}
