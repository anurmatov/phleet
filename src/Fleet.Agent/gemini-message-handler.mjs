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
