// The setup code a phone scans to connect the iOS app to this fleet: Fleet, Runs and Chat at once.
//
// A JSON object, so the app can refuse what it does not understand by name: a `phleet` kind and a
// version. The address is the one the PHONE will use, which is not always the one this page was
// opened at. Empty parts are left out rather than sent empty.
//
// The secret in it is a ONE-TIME enrollment code the orchestrator issues for this purpose: it
// registers one device, once, within fifteen minutes. Nothing long-lived is ever drawn on screen.

import { fitsInQrCode } from './qrMatrix.ts'

export const PHONE_SETUP_KIND = 'setup'
export const PHONE_SETUP_VERSION = 2

/** The longest device name the orchestrator stores. */
export const DEVICE_NAME_MAX_LENGTH = 64

export interface PhoneSetupForm {
  origin: string
  deviceName: string
  accessClientId: string
  accessClientSecret: string
}

export type PhoneSetupProblem =
  | 'origin_empty'
  | 'origin_invalid'
  | 'origin_insecure'
  | 'origin_userinfo'
  | 'service_token_incomplete'
  | 'enrollment_code_missing'
  | 'code_too_long'

export type PhoneSetupResult = { code: string } | { problem: PhoneSetupProblem }

export function emptyPhoneSetupForm(pageOrigin: string): PhoneSetupForm {
  return { origin: pageOrigin, deviceName: '', accessClientId: '', accessClientSecret: '' }
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

/**
 * What is wrong with the form, or null. Checked BEFORE an enrollment code is asked for, so a form
 * that cannot produce a code never costs one.
 */
export function phoneSetupFormProblem(form: PhoneSetupForm): PhoneSetupProblem | null {
  const normalized = normalizeOrigin(form.origin)
  if ('problem' in normalized) return normalized.problem

  const clientId = form.accessClientId.trim()
  const clientSecret = form.accessClientSecret.trim()
  if ((clientId === '') !== (clientSecret === '')) return 'service_token_incomplete'
  return null
}

export function phoneSetupCode(form: PhoneSetupForm, enrollmentCode: string): PhoneSetupResult {
  const problem = phoneSetupFormProblem(form)
  if (problem) return { problem }
  const normalized = normalizeOrigin(form.origin) as { origin: string }

  const enrollment = enrollmentCode.trim()
  if (!enrollment) return { problem: 'enrollment_code_missing' }

  // Key order is fixed so the same form always produces the same code.
  const payload: Record<string, string | number> = {
    phleet: PHONE_SETUP_KIND,
    v: PHONE_SETUP_VERSION,
    origin: normalized.origin,
  }
  const clientId = form.accessClientId.trim()
  if (clientId) {
    payload.accessClientId = clientId
    payload.accessClientSecret = form.accessClientSecret.trim()
  }
  payload.enrollmentCode = enrollment

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
    case 'enrollment_code_missing': return 'The orchestrator did not issue a code. Try again.'
    case 'code_too_long': return 'That is too much for one code. Use a shorter address or service token.'
  }
}

/**
 * Puts a setup code on the clipboard as text, for a phone that has no camera to point at the
 * picture: the app reads a pasted code exactly as it reads a scanned one.
 *
 * It is the same one-time code either way, so copying it is no more than showing it. The
 * clipboard is the browser's to refuse — outside a secure context there is none, and a write can
 * be denied — and both are answered with false rather than thrown, so the page can say so.
 */
export async function copyPhoneSetupCode(
  code: string,
  clipboard: { writeText(text: string): Promise<void> } | undefined | null,
): Promise<boolean> {
  if (!code || !clipboard) return false
  try {
    await clipboard.writeText(code)
    return true
  } catch {
    return false
  }
}
