/**
 * What a reprovision request says about the agent's provider session.
 *
 * An agent keeps its session across a reprovision when it can (same provider, unchanged
 * instructions). The one thing a request can add is `session: 'fresh'`, which makes the
 * orchestrator remove the agent's session file so the new container starts a new session.
 */

/** The word the orchestrator's reprovision routes take for a new session (`AgentSessionFile.Fresh`). */
export const FRESH_SESSION = 'fresh'

/** The one line of help shown beside the choice. */
export const SESSION_CHOICE_HELP = 'A new session forgets the conversation. Changed instructions always start a new session.'

/**
 * The JSON body of a reprovision request. Resuming sends none, so the default request is the one
 * the dashboard has always sent.
 */
export function reprovisionBody(fresh: boolean): { session: string } | undefined {
  return fresh ? { session: FRESH_SESSION } : undefined
}
