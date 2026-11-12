// Sign-in, as this page sees it (docs/sign-in.md).
//
// The page holds no token. A signed-in browser carries an HttpOnly session cookie that script
// cannot read, the browser sends it by itself to this origin, and every write adds one header
// (`X-Fleet-Request`) that a page on another site cannot add.

/** Every write from this page carries it; the orchestrator refuses a cookie-authenticated write without it. */
export const REQUEST_HEADER = 'X-Fleet-Request'

export type SessionState =
  /** Signed in: the page may load. */
  | { status: 'signed-in'; kind: string; userId: string; displayName: string; deviceId: string; authRequired: boolean }
  /** The orchestrator asks for sign-in and this browser has none. */
  | { status: 'signed-out' }
  /** The orchestrator gates nothing. The page may load; it asks for a session only to have one for chat. */
  | { status: 'open' }
  /** No answer that can be read. Shown as itself, never as "signed out". */
  | { status: 'unreachable'; httpStatus: number }

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

/** What `GET /api/auth/session` said. */
export function readSession(httpStatus: number, body: unknown): SessionState {
  const fields = (body && typeof body === 'object' ? body : {}) as Record<string, unknown>

  if (httpStatus === 401) return { status: 'signed-out' }
  // An orchestrator from before sign-in has no such route. It gates reads on nothing, so the page
  // loads as it always did on one.
  if (httpStatus === 404) return { status: 'open' }
  if (httpStatus !== 200) return { status: 'unreachable', httpStatus }

  if (fields.signedIn === true) {
    return {
      status: 'signed-in',
      kind: text(fields.kind),
      userId: text(fields.userId),
      displayName: text(fields.displayName),
      deviceId: text(fields.deviceId),
      authRequired: fields.authRequired !== false,
    }
  }
  return fields.authRequired === false ? { status: 'open' } : { status: 'signed-out' }
}

/** Why a sign-in attempt did not work, in words for the person who typed the token. */
export function signInProblem(httpStatus: number): string {
  switch (httpStatus) {
    case 401: return 'That token was not accepted.'
    case 409: return 'This fleet already has the most devices it allows. Revoke one from a signed-in browser, or wait for an unused browser session to lapse.'
    case 429: return 'Too many attempts. Wait a minute and try again.'
    case 503: return 'The orchestrator could not check the token just now. Try again.'
    default: return `Sign-in failed (HTTP ${httpStatus}).`
  }
}

/**
 * A name for this browser in the Devices list, from its user agent: "Chrome on macOS".
 * A convenience only — the person can change it before signing in, and nothing is decided by it.
 */
export function describeBrowser(userAgent: string): string {
  const ua = userAgent || ''
  const browser =
    /Edg\//.test(ua) ? 'Edge'
    : /OPR\/|Opera/.test(ua) ? 'Opera'
    : /Firefox\//.test(ua) ? 'Firefox'
    : /Chrome\/|CriOS\//.test(ua) ? 'Chrome'
    : /Safari\//.test(ua) ? 'Safari'
    : 'Browser'
  const system =
    /iPhone|iPad|iPod/.test(ua) ? 'iOS'
    : /Android/.test(ua) ? 'Android'
    : /Mac OS X|Macintosh/.test(ua) ? 'macOS'
    : /Windows/.test(ua) ? 'Windows'
    : /Linux/.test(ua) ? 'Linux'
    : ''
  return system ? `${browser} on ${system}` : browser
}

/** Whether a request changes something, and so must carry {@link REQUEST_HEADER}. */
export function isWrite(method: string | undefined): boolean {
  const verb = (method ?? 'GET').toUpperCase()
  return !(verb === 'GET' || verb === 'HEAD' || verb === 'OPTIONS')
}

export interface DeviceRow {
  deviceId: string
  kind: 'browser' | 'app'
  name: string
  registeredAt: string
  lastSeenAt: string | null
  revokedAt: string | null
  current: boolean
}

/** The Devices list as the page shows it: active first, this browser at the top, then newest. */
export function readDevices(body: unknown): DeviceRow[] {
  const list = (body && typeof body === 'object' ? (body as Record<string, unknown>).devices : null)
  if (!Array.isArray(list)) return []

  const rows: DeviceRow[] = []
  for (const entry of list) {
    if (!entry || typeof entry !== 'object') continue
    const fields = entry as Record<string, unknown>
    const deviceId = text(fields.deviceId)
    if (!deviceId) continue
    rows.push({
      deviceId,
      kind: fields.kind === 'browser' ? 'browser' : 'app',
      name: text(fields.name),
      registeredAt: text(fields.registeredAt),
      lastSeenAt: text(fields.lastSeenAt) || null,
      revokedAt: text(fields.revokedAt) || null,
      current: fields.current === true,
    })
  }

  const rank = (row: DeviceRow) => (row.revokedAt ? 2 : row.current ? 0 : 1)
  return rows.sort((a, b) => rank(a) - rank(b) || b.registeredAt.localeCompare(a.registeredAt))
}

/** What to call a device with no name. */
export function deviceTitle(row: Pick<DeviceRow, 'name' | 'kind'>): string {
  return row.name || (row.kind === 'browser' ? 'Browser' : 'App')
}
