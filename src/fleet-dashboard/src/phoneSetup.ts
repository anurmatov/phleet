// The setup code a phone scans to connect the iOS app's control plane to this orchestrator.
//
// A JSON object, so the app can refuse what it does not understand by name: a `phleet` kind and a
// version. The address is the one the PHONE will use, which is not always the one this page was
// opened at. Empty parts are left out rather than sent empty — the app treats a connection with
// no operator token as read-only, and "" must not be mistaken for a token.

import { fitsInQrCode } from './qrMatrix.ts'

export const PHONE_SETUP_KIND = 'control-setup'
export const PHONE_SETUP_VERSION = 1

export interface PhoneSetupForm {
  origin: string
  includeOperatorToken: boolean
  accessClientId: string
  accessClientSecret: string
}

export type PhoneSetupProblem =
  | 'origin_empty'
  | 'origin_invalid'
  | 'origin_insecure'
  | 'origin_userinfo'
  | 'service_token_incomplete'
  | 'operator_token_missing'
  | 'code_too_long'

export type PhoneSetupResult = { code: string } | { problem: PhoneSetupProblem }

export function emptyPhoneSetupForm(pageOrigin: string): PhoneSetupForm {
  return { origin: pageOrigin, includeOperatorToken: true, accessClientId: '', accessClientSecret: '' }
}

/** The address as the app stores it: https only, no credentials in it, no trailing slash. */
export function normalizeOrigin(raw: string): { origin: string } | { problem: PhoneSetupProblem } {
  const trimmed = raw.trim()
  if (!trimmed) return { problem: 'origin_empty' }

  let url: URL
  try { url = new URL(trimmed.includes('://') ? trimmed : `https://${trimmed}`) }
  catch { return { problem: 'origin_invalid' } }

  // Refused, not upgraded: the app refuses http too, and a code that silently changed the
  // address would connect the phone to something the operator did not type.
  if (url.protocol !== 'https:') return { problem: 'origin_insecure' }
  if (url.username || url.password) return { problem: 'origin_userinfo' }
  if (!url.hostname) return { problem: 'origin_invalid' }

  return { origin: url.origin + url.pathname.replace(/\/+$/, '') }
}

export function phoneSetupCode(form: PhoneSetupForm, operatorToken: string): PhoneSetupResult {
  const normalized = normalizeOrigin(form.origin)
  if ('problem' in normalized) return normalized

  const clientId = form.accessClientId.trim()
  const clientSecret = form.accessClientSecret.trim()
  if ((clientId === '') !== (clientSecret === '')) return { problem: 'service_token_incomplete' }

  const token = operatorToken.trim()
  if (form.includeOperatorToken && !token) return { problem: 'operator_token_missing' }

  // Key order is fixed so the same form always produces the same code.
  const payload: Record<string, string | number> = {
    phleet: PHONE_SETUP_KIND,
    v: PHONE_SETUP_VERSION,
    origin: normalized.origin,
  }
  if (clientId) {
    payload.accessClientId = clientId
    payload.accessClientSecret = clientSecret
  }
  if (form.includeOperatorToken) payload.operatorToken = token

  // Anything outside ASCII is written as a JSON escape: the same JSON to whatever reads it, and
  // the same bytes whichever character set a phone's scanner assumes for a QR code.
  const code = JSON.stringify(payload).replace(/[^\x00-\x7f]/g,
    c => `\\u${c.charCodeAt(0).toString(16).padStart(4, '0')}`)
  // Refused here, as a problem with the form, rather than discovered while drawing the code.
  if (!fitsInQrCode(code)) return { problem: 'code_too_long' }
  return { code }
}

export function phoneSetupProblemMessage(problem: PhoneSetupProblem): string {
  switch (problem) {
    case 'origin_empty': return 'Enter the address the phone will use to reach this dashboard.'
    case 'origin_invalid': return 'That is not an address.'
    case 'origin_insecure': return 'The app only connects over HTTPS. Enter the HTTPS address the phone will use.'
    case 'origin_userinfo': return 'The address must not contain a user name or password.'
    case 'service_token_incomplete': return 'Enter both the client ID and the client secret, or leave both empty.'
    case 'operator_token_missing': return 'This dashboard was built without an operator token, so the code can only be read-only. Untick the operator token.'
    case 'code_too_long': return 'That is too much for one code. Use a shorter address or service token.'
  }
}
