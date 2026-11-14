import { getApiKey, OPENROUTER_BASE_URL } from './config'

// Minimal OpenAI-compatible chat completions client (OpenRouter by default). Plain fetch on purpose: the official SDK
// is ~1.3 MB and, inlined into the panel's single IIFE bundle, made the bundler duplicate React.

export interface CacheControl {
  cache_control?: { type: 'ephemeral' }
}
export type ContentPart =
  | ({ type: 'text'; text: string } & CacheControl)
  | ({ type: 'image_url'; image_url: { url: string } } & CacheControl)

export interface ToolCall {
  id: string
  type: 'function'
  function: { name: string; arguments: string }
}

export type Message =
  | { role: 'system'; content: string | ContentPart[] }
  | { role: 'user'; content: string | ContentPart[] }
  | { role: 'assistant'; content: string | null; tool_calls?: ToolCall[] }
  | { role: 'tool'; tool_call_id: string; content: string | ContentPart[] }

export interface ToolDef {
  type: 'function'
  function: { name: string; description: string; parameters: Record<string, unknown> }
}

export interface Usage {
  prompt_tokens: number
  completion_tokens: number
  /** OpenRouter: USD charged for the request */
  cost?: number
}

interface CompletionResponse {
  choices?: { message: { content: string | null; tool_calls?: ToolCall[] }; finish_reason?: string }[]
  usage?: Usage
  /** OpenRouter can answer 200 with an upstream error in the body */
  error?: { message?: string; code?: number | string }
}

export class ApiError extends Error {
  readonly status?: number
  constructor(message: string, status?: number) {
    super(message)
    this.status = status
  }
}

export interface CompletionRequest {
  model: string
  messages: Message[]
  tools?: ToolDef[]
  max_tokens?: number
}

const RETRIES = 2

/** One chat completion. Retries rate limits / server errors twice with backoff. */
export async function chatCompletion(request: CompletionRequest, signal: AbortSignal) {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(`${OPENROUTER_BASE_URL.replace(/\/$/, '')}/chat/completions`, {
      method: 'POST',
      signal,
      headers: {
        Authorization: `Bearer ${getApiKey()}`,
        'Content-Type': 'application/json',
        'X-Title': 'Freppuccino',
      },
      body: JSON.stringify({ ...request, tool_choice: 'auto', usage: { include: true } }),
    })
    const retryable = response.status === 429 || response.status >= 500
    if (retryable && attempt < RETRIES) {
      await sleep(1000 * 2 ** attempt, signal)
      continue
    }
    const text = await response.text()
    let body: CompletionResponse
    try {
      body = JSON.parse(text)
    } catch {
      throw new ApiError(`${response.status} ${response.statusText}: ${text.slice(0, 300)}`, response.status)
    }
    if (!response.ok || body.error) {
      const status = response.ok ? Number(body.error?.code) || undefined : response.status
      throw new ApiError(body.error?.message ?? `${response.status} ${response.statusText}`, status)
    }
    const choice = body.choices?.[0]
    if (!choice) throw new ApiError(`no choices in the response from ${request.model}`)
    return { message: choice.message, finishReason: choice.finish_reason, usage: body.usage }
  }
}

function sleep(ms: number, signal: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    const t = setTimeout(resolve, ms)
    signal.addEventListener('abort', () => (clearTimeout(t), reject(new DOMException('Stopped', 'AbortError'))), { once: true })
  })
}
