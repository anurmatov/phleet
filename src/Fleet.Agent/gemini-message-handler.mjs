/**
 * gemini-message-handler.mjs
 *
 * Shared handler logic extracted from gemini-bridge.mjs so it can be imported
 * in unit tests without triggering the bridge's top-level process guards
 * (GEMINI_API_KEY check, GEMINI_SYSTEM_MD file read, etc.).
 */

/**
 * Extracts displayable text from a LegacyAgentProtocol 'message' event.
 *
 * LegacyAgentProtocol emits 'message' for both the injected user turn (role='user')
 * and the model's reply (role='agent'). Only agent-role events with non-empty text
 * content should be accumulated in the response payload.
 *
 * @param {object} event - A LegacyAgentProtocol event object.
 * @returns {string|null} The extracted text, or null if the event should be skipped.
 */
export function extractAgentMessageText(event) {
  if (event.type !== 'message' || event.role !== 'agent') return null;
  const text = Array.isArray(event.content)
    ? event.content.filter(c => c.type === 'text').map(c => c.text ?? '').join('')
    : '';
  return text || null;
}

// ── Rate-limit retry helpers ──────────────────────────────────────────────────

/** Default wait when the API doesn't include a retryDelay value. */
export const DEFAULT_RETRY_DELAY_MS = 30_000;

/** Hard cap so a single rate-limit never blocks the bridge indefinitely. */
export const MAX_RETRY_DELAY_MS = 90_000;

/**
 * Parses a Gemini retryDelay string into milliseconds.
 *
 * Accepted formats: "23s", "1m", "1m30s".
 * Returns DEFAULT_RETRY_DELAY_MS for null/undefined/empty/malformed input.
 * Result is capped at MAX_RETRY_DELAY_MS.
 *
 * @param {string|null|undefined} delayStr - The raw retryDelay value from the API.
 * @returns {number} Milliseconds to wait before retrying.
 */
export function parseRetryDelayMs(delayStr) {
  if (!delayStr || typeof delayStr !== 'string') return DEFAULT_RETRY_DELAY_MS;
  const s = delayStr.trim();

  // "NmNs" — minutes + seconds
  const full = /^(\d+)m(\d+)s$/.exec(s);
  if (full) {
    const ms = (parseInt(full[1], 10) * 60 + parseInt(full[2], 10)) * 1000;
    return ms > 0 ? Math.min(ms, MAX_RETRY_DELAY_MS) : DEFAULT_RETRY_DELAY_MS;
  }

  // "Nm" — minutes only
  const minsOnly = /^(\d+)m$/.exec(s);
  if (minsOnly) {
    const ms = parseInt(minsOnly[1], 10) * 60_000;
    return ms > 0 ? Math.min(ms, MAX_RETRY_DELAY_MS) : DEFAULT_RETRY_DELAY_MS;
  }

  // "Ns" — seconds only
  const secsOnly = /^(\d+)s$/.exec(s);
  if (secsOnly) {
    const ms = parseInt(secsOnly[1], 10) * 1000;
    return ms > 0 ? Math.min(ms, MAX_RETRY_DELAY_MS) : DEFAULT_RETRY_DELAY_MS;
  }

  return DEFAULT_RETRY_DELAY_MS;
}
