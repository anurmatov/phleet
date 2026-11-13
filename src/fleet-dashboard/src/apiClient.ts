// Requests that fail loudly.
//
// `apiFetch` (utils.tsx) returns the Response whatever its status, which is right for the few
// callers that read the status themselves and wrong for everyone else: a caller that forgets
// `ok` shows "Saved" for a refused write, or stores `{ error: "…" }` where a list was expected.
// The helpers here throw instead, so the only way to get a value is for the request to have worked.
//
// Kept free of JSX and `import.meta.env` so `node --test` can run it; utils.tsx binds these to
// `apiFetch`, which is what adds the write header and announces a 401.

/** A request the server answered with a non-2xx status, or a read it answered with something that is not JSON. */
export class ApiError extends Error {
  /** The HTTP status the server answered with. */
  readonly status: number
  /** The parsed response body, or null when there was none or it was not JSON. */
  readonly body: unknown

  constructor(status: number, body: unknown, message?: string) {
    super(message ?? serverErrorText(body) ?? `Error ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }
}

function nonEmptyString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value : null
}

/**
 * The server's own words for a failure: the orchestrator's `{ error }`, else a ProblemDetails
 * `title` (what `Results.Problem` and model binding answer with). Null when the body has neither.
 */
export function serverErrorText(body: unknown): string | null {
  if (!body || typeof body !== 'object') return null
  const fields = body as Record<string, unknown>
  return nonEmptyString(fields.error) ?? nonEmptyString(fields.title)
}

/**
 * Words for a failure, for a status line or a toast. An {@link ApiError} says what the server
 * said; anything else is a request that never got an answer (offline, refused, aborted).
 */
export function errorMessage(error: unknown, fallback = 'Request failed'): string {
  return error instanceof ApiError ? error.message : fallback
}

/** The slice of `Response` these helpers read, so a test can hand one in without a network. */
export interface ResponseLike {
  ok: boolean
  status: number
  text(): Promise<string>
}

export type Fetcher = (url: string, init?: RequestInit) => Promise<ResponseLike>

type ParsedBody = { json: true; value: unknown } | { json: false }

async function parseBody(response: ResponseLike): Promise<ParsedBody> {
  let raw: string
  try { raw = await response.text() } catch { return { json: false } }
  if (raw.trim() === '') return { json: false }
  try { return { json: true, value: JSON.parse(raw) } } catch { return { json: false } }
}

/**
 * Throws an {@link ApiError} for a non-2xx response and returns the response otherwise. For the
 * callers that need the Response itself (a download, a multipart upload) and still must not
 * mistake a refusal for success.
 */
export async function ensureOk<R extends ResponseLike>(response: R): Promise<R> {
  if (response.ok) return response
  const parsed = await parseBody(response)
  throw new ApiError(response.status, parsed.json ? parsed.value : null)
}

/**
 * A read. Resolves with the parsed JSON body of a 2xx answer; throws an {@link ApiError} for any
 * other status, and for a 2xx whose body is not JSON (a proxy's HTML page is not a list).
 */
export async function requestJson<T>(fetcher: Fetcher, url: string, init?: RequestInit): Promise<T> {
  const response = await fetcher(url, init)
  const parsed = await parseBody(response)
  if (!response.ok) throw new ApiError(response.status, parsed.json ? parsed.value : null)
  if (!parsed.json) throw new ApiError(response.status, null, `Unreadable answer (HTTP ${response.status})`)
  return parsed.value as T
}

/**
 * A write. `json`, when given, is sent as the JSON body. Resolves with the parsed body of a 2xx
 * answer, or null when it had none (a 204, or a body that is not JSON); throws an {@link ApiError}
 * for any other status.
 */
export async function requestWrite<T = unknown>(
  fetcher: Fetcher, url: string, method: string, json?: unknown,
): Promise<T> {
  const init: RequestInit = json === undefined
    ? { method }
    : { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(json) }
  const response = await fetcher(url, init)
  const parsed = await parseBody(response)
  if (!response.ok) throw new ApiError(response.status, parsed.json ? parsed.value : null)
  return (parsed.json ? parsed.value : null) as T
}

/** How many of a batch of writes failed, and the first failure's words. Null when all succeeded. */
export function firstFailure(results: PromiseSettledResult<unknown>[]): { failed: number; message: string } | null {
  const rejected = results.filter((r): r is PromiseRejectedResult => r.status === 'rejected')
  if (rejected.length === 0) return null
  return { failed: rejected.length, message: errorMessage(rejected[0].reason) }
}
