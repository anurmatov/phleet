// Which chat threads have moved on since this browser last showed them.
//
// "Moved on" is all it claims, the same as the phone app: the newest event may be the agent's
// answer, a message sent from the phone, or progress on a turn still running. The page says there
// is something to look at, never what it is and never how many.
//
// How far each thread was shown is kept in this browser only. The first time a browser reads the
// list it takes every thread as read up to where it stands, so a new browser does not open with
// every old conversation marked.
//
// Free of JSX and of `window`, so `node --test` can run it; the hook that gives it the browser's
// fetch and storage is hooks/useChatActivity.ts.

import { readChatAgents, readThreads } from './chatProtocol.ts'
import type { ChatAgent, ThreadSummary } from './chatProtocol'

/** Where the seq each thread was shown up to is kept, by agent name. */
export const SEEN_KEY = 'fleet.chat.seen'

type SeenStorage = Pick<Storage, 'getItem' | 'setItem'> | null

/** The kept positions, or null when this browser has never kept any (or cannot). */
export function readSeen(storage: SeenStorage): Record<string, number> | null {
  let raw: string | null
  try { raw = storage?.getItem(SEEN_KEY) ?? null } catch { return null }
  if (raw === null) return null
  try {
    const value: unknown = JSON.parse(raw)
    if (!value || typeof value !== 'object' || Array.isArray(value)) return {}
    const seen: Record<string, number> = {}
    for (const [agent, seq] of Object.entries(value)) {
      if (typeof seq === 'number' && Number.isSafeInteger(seq) && seq >= 0) seen[agent] = seq
    }
    return seen
  } catch {
    return {}
  }
}

export function writeSeen(storage: SeenStorage, seen: Record<string, number>): void {
  try { storage?.setItem(SEEN_KEY, JSON.stringify(seen)) } catch { /* kept for this page only */ }
}

/** Whether a thread holds something this browser has not shown. */
export function isUnread(thread: ThreadSummary | undefined, seen: number | undefined): boolean {
  return thread !== undefined && thread.nextSeq - 1 > (seen ?? 0)
}

export interface ChatActivity {
  /** Whether this deployment has chat. Null until the orchestrator has said. */
  available: boolean | null
  agents: ChatAgent[]
  /** By agent name. */
  threads: Record<string, ThreadSummary>
  /** The seq each thread was shown up to, by agent name. */
  seen: Record<string, number>
}

/** The agents, of those that can be chatted with, whose thread has moved on. */
export function unreadAgents(activity: ChatActivity): string[] {
  return activity.agents
    .map(a => a.agentName)
    .filter(name => isUnread(activity.threads[name], activity.seen[name]))
}

export interface ActivityDeps {
  fetch(url: string, init?: RequestInit): Promise<Response>
  storage: SeenStorage
}

export class ChatActivityStore {
  private state: ChatActivity = { available: null, agents: [], threads: {}, seen: {} }
  private listeners = new Set<() => void>()
  /** Whether this browser had positions kept before this page. False until the first list is read. */
  private baselined: boolean
  private refreshing: Promise<void> | null = null
  private readonly deps: ActivityDeps

  constructor(deps: ActivityDeps) {
    this.deps = deps
    const kept = readSeen(deps.storage)
    this.baselined = kept !== null
    if (kept) this.state = { ...this.state, seen: kept }
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }

  snapshot = (): ChatActivity => this.state

  /** Read who can be chatted with and where each thread stands. One read at a time. */
  refresh(): Promise<void> {
    this.refreshing ??= this.read().finally(() => { this.refreshing = null })
    return this.refreshing
  }

  private async read(): Promise<void> {
    const agents = await this.json('/api/chat/agents')
    if (!agents) return
    const answer = readChatAgents(agents.status, agents.body)
    // An orchestrator that could not answer says nothing about chat: what was read before stands.
    if (answer.status !== 'ok') return
    if (!answer.available) {
      this.set({ available: false, agents: [] })
      return
    }
    this.set({ available: true, agents: answer.agents })

    const list = await this.json('/v1/conversations')
    const fetched = list?.status === 200 ? readThreads(list.body) : null
    if (!fetched) return

    // A thread this page is showing live can be ahead of what the list just said.
    const threads: Record<string, ThreadSummary> = { ...fetched }
    for (const [agent, held] of Object.entries(this.state.threads)) {
      const read = threads[agent]
      if (read && read.conversationId === held.conversationId && held.nextSeq > read.nextSeq) threads[agent] = held
    }

    if (!this.baselined) {
      this.baselined = true
      const seen: Record<string, number> = { ...this.state.seen }
      for (const thread of Object.values(threads)) seen[thread.agent] = Math.max(seen[thread.agent] ?? 0, thread.nextSeq - 1)
      writeSeen(this.deps.storage, seen)
      this.set({ threads, seen })
      return
    }
    this.set({ threads })
  }

  /** The thread this page shows live has reached `lastSeq`. */
  noteThread(agent: string, conversationId: string, lastSeq: number, at: number | null): void {
    const held = this.state.threads[agent]
    if (held && held.conversationId === conversationId && held.nextSeq > lastSeq) return
    this.set({
      threads: {
        ...this.state.threads,
        [agent]: { conversationId, agent, nextSeq: lastSeq + 1, lastActivityAt: at ?? held?.lastActivityAt ?? null },
      },
    })
  }

  /** The thread has been shown up to `seq`. Never moves a position back. */
  markSeen(agent: string, seq: number): void {
    if (seq <= (this.state.seen[agent] ?? 0)) return
    // Another tab may have moved other threads on since this one read the positions.
    const seen: Record<string, number> = { ...this.state.seen, [agent]: seq }
    for (const [name, kept] of Object.entries(readSeen(this.deps.storage) ?? {})) {
      if (kept > (seen[name] ?? 0)) seen[name] = kept
    }
    this.baselined = true
    writeSeen(this.deps.storage, seen)
    this.set({ seen })
  }

  /** Another tab changed the kept positions: take the further of each. */
  reloadSeen(): void {
    const kept = readSeen(this.deps.storage)
    if (!kept) return
    const seen: Record<string, number> = { ...this.state.seen }
    let changed = false
    for (const [agent, seq] of Object.entries(kept)) {
      if (seq > (seen[agent] ?? 0)) { seen[agent] = seq; changed = true }
    }
    if (changed) this.set({ seen })
  }

  private async json(url: string): Promise<{ status: number; body: unknown } | null> {
    try {
      const response = await this.deps.fetch(url)
      let body: unknown = null
      try { body = await response.json() } catch { /* the status decides */ }
      return { status: response.status, body }
    } catch {
      return null
    }
  }

  private set(patch: Partial<ChatActivity>): void {
    this.state = { ...this.state, ...patch }
    for (const listener of this.listeners) listener()
  }
}

// ── how an agent is doing, beside its name ──────────────────────────────────

/** What the chat list needs of an agent as the fleet reports it. */
export interface AgentStanding {
  /** A dot colour: `green`, `yellow`, `red` or `grey`. */
  dot: 'green' | 'yellow' | 'red' | 'grey'
  text: string
  /** Whether a message sent now will sit unanswered until someone starts the agent. */
  down: boolean
}

/**
 * How an agent is doing, in a few words: an agent that is down will not answer, and that is worth
 * knowing before writing to it. Null where the fleet does not list the agent.
 */
export function agentStanding(agent: { effectiveStatus: string; currentTask: string | null } | undefined): AgentStanding | null {
  if (!agent) return null
  if (agent.effectiveStatus === 'dead') return { dot: 'red', text: 'Offline', down: true }
  const task = (agent.currentTask ?? '').trim().split('\n')[0]
  if (task) return { dot: 'green', text: `Working: ${task.length > 90 ? `${task.slice(0, 90)}…` : task}`, down: false }
  if (agent.effectiveStatus === 'stale') return { dot: 'yellow', text: 'Not heard from lately', down: false }
  return { dot: 'green', text: 'Idle', down: false }
}
