// The pages of the dashboard, and the URL hash that names one.
//
// The list is the single definition: the `ActiveView` type is derived from it, so a view cannot be
// added to the type and forgotten here. (`memory` was: the type had it, the hand-kept list did
// not, and reloading on `#memory` landed on agents.)
// Kept free of JSX and `import.meta.env` so `node --test` can run it.

export const ACTIVE_VIEWS = [
  'agents', 'workflows', 'chat', 'instructions', 'project-contexts', 'output-styles',
  'wf-definitions', 'alerts', 'schedules', 'namespaces', 'repositories', 'credentials',
  'memory', 'epic-grants',
] as const

export type ActiveView = (typeof ACTIVE_VIEWS)[number]

export function isActiveView(value: string): value is ActiveView {
  return (ACTIVE_VIEWS as readonly string[]).includes(value)
}

/**
 * `#view` or `#view/param`. The second segment exists so a link can name a row rather than only
 * a page; without it `#output-styles/alpha` matches no view. Null for anything unrecognised.
 * A param that is not valid percent-encoding is returned as written rather than thrown on: this
 * runs while the page first renders, where a throw takes the page down.
 */
export function parseViewHash(hash: string): { view: ActiveView; param: string } | null {
  const raw = hash.startsWith('#') ? hash.slice(1) : hash
  const slash = raw.indexOf('/')
  const head = slash === -1 ? raw : raw.slice(0, slash)
  if (!isActiveView(head)) return null
  if (slash === -1) return { view: head, param: '' }
  const rest = raw.slice(slash + 1)
  let param = rest
  try { param = decodeURIComponent(rest) } catch { /* malformed escape: keep it as written */ }
  return { view: head, param }
}
