import type { Page, WebSocketRoute } from '@playwright/test'

// A substituted chat service (Fleet.Comms as the page reaches it under /v1/) and the
// orchestrator's list of who can be chatted with. Installed after FakeFleet, whose own /v1/ answer
// is "no such route". Only what the page uses is here.

const PROTOCOL = 'fleet.conversation.v1'

export interface ChatCall { method: string; path: string; body: unknown; headers: Record<string, string> }

/** A four-pixel-square PNG: what every file in this chat service turns out to be. */
export const PIXEL = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR42mOwO98HRwzEcQCXMhmxeFRE8wAAAABJRU5ErkJggg==',
  'base64',
)

export class FakeChat {
  agents = [{ agentName: 'alpha', displayName: 'Alpha', replySupported: true }, { agentName: 'beta', displayName: 'Beta', replySupported: true }]
  /** By conversation id (`c-<agent>`), in seq order. */
  events: Record<string, Array<Record<string, unknown>>> = {}
  calls: ChatCall[] = []
  /** Whether the chat service has the file routes. */
  filesOn = true
  private sockets: Record<string, WebSocketRoute> = {}
  private uploads: Record<string, { kind: string; contentType: string; byteSize: number; fileName: string }> = {}
  private reserved = 0

  writes(method: string, match: RegExp): ChatCall[] {
    return this.calls.filter(c => c.method === method && match.test(c.path))
  }

  /** Appends an event to a conversation and, when the page is attached to it, streams it. */
  push(agent: string, kind: string, submissionId: string, payload: Record<string, unknown> = {}) {
    const id = `c-${agent}`
    const list = (this.events[id] ??= [])
    const seq = list.length + 1
    const event = {
      protocol: PROTOCOL, eventId: `${id}-e${seq}`, seq, emittedAt: new Date().toISOString(), kind,
      identity: { principalId: 'owner', role: 'owner', channelId: 'client', conversationId: id, submissionId, attempt: 1 },
      payload,
    }
    list.push(event)
    this.sockets[id]?.send(JSON.stringify(event))
    return seq
  }

  async install(page: Page) {
    await page.route('**/api/chat/agents', route => route.fulfill({ json: { available: true, agents: this.agents } }))
    await page.routeWebSocket(/\/v1\/conversations\/[^/]+\/stream/, ws => {
      const id = new URL(ws.url()).pathname.split('/')[3]
      this.sockets[id] = ws
      ws.send(JSON.stringify({ protocol: PROTOCOL, kind: 'hello', conversationId: id }))
    })
    await page.route('**/v1/**', async route => {
      const request = route.request()
      const url = new URL(request.url())
      const method = request.method()
      const path = decodeURIComponent(url.pathname)
      let body: unknown
      try { body = request.postDataJSON() } catch { body = request.postDataBuffer()?.length ?? null }
      this.calls.push({ method, path, body, headers: request.headers() })

      const json = (status: number, value: Record<string, unknown>) => route.fulfill({ status, json: { protocol: PROTOCOL, ...value } })

      if (method === 'GET' && path === '/v1/session') {
        return json(200, { principalId: 'owner', limits: { inboundTextBytes: 32768, catchUpLimitDefault: 200, catchUpLimitMax: 1000 } })
      }
      if (method === 'GET' && path === '/v1/conversations') {
        return json(200, {
          conversations: Object.entries(this.events).map(([conversationId, list]) => {
            // The newest thing someone said, as the chat service finds it: not progress, not a notice.
            const said = [...list].reverse().find(e => ['submission.text', 'turn.final', 'turn.message'].includes(e.kind as string))
            return {
              conversationId, agent: conversationId.slice(2), externalRef: 'main', nextSeq: list.length + 1, retainedFloorSeq: 1,
              lastActivityAt: new Date().toISOString(),
              ...(said ? { lastMessage: { from: said.kind === 'submission.text' ? 'user' : 'agent', text: (said.payload as { text: string }).text } } : {}),
            }
          }),
        })
      }
      if (method === 'POST' && path === '/v1/conversations') {
        const agent = (body as { agent: string }).agent
        this.events[`c-${agent}`] ??= []
        return json(200, { conversationId: `c-${agent}`, agent, nextSeq: this.events[`c-${agent}`].length + 1, retainedFloorSeq: 1 })
      }
      let m = path.match(/^\/v1\/conversations\/([^/]+)\/events$/)
      if (method === 'GET' && m) {
        const after = Number(url.searchParams.get('afterSeq'))
        return json(200, { events: (this.events[m[1]] ?? []).filter(e => (e.seq as number) > after), nextAfterSeq: 0, hasMore: false })
      }
      m = path.match(/^\/v1\/conversations\/c-([^/]+)\/submissions$/)
      if (method === 'POST' && m) {
        const sent = body as { submissionId: string; text: string; replyToEventId?: string; attachments?: Array<{ attachmentId: string }> }
        const attachments = (sent.attachments ?? []).map(a => ({ attachmentId: a.attachmentId, ...this.uploads[a.attachmentId] }))
        const seq = this.push(m[1], 'submission.text', sent.submissionId, {
          text: sent.text, ...(attachments.length ? { attachments } : {}), ...(sent.replyToEventId ? { replyToEventId: sent.replyToEventId } : {}),
        })
        return json(201, { submissionId: sent.submissionId, acceptedSeq: seq })
      }
      m = path.match(/^\/v1\/conversations\/c-([^/]+):cancel$/)
      if (method === 'POST' && m) return json(200, {})
      m = path.match(/^\/v1\/conversations\/[^/]+\/attachments$/)
      if (method === 'POST' && m) {
        if (!this.filesOn) return route.fulfill({ status: 404, body: '' })
        const wanted = body as { kind: string; contentType: string; byteSize: number; fileName: string }
        const attachmentId = `att-${++this.reserved}`
        // As the chat service records them: a picture keeps its type, a file never keeps the one it claimed.
        this.uploads[attachmentId] = wanted.kind === 'file'
          ? { kind: 'other', contentType: 'application/octet-stream', byteSize: wanted.byteSize, fileName: wanted.fileName }
          : { kind: 'image', contentType: wanted.contentType, byteSize: wanted.byteSize, fileName: wanted.fileName }
        return json(201, { attachmentId, uploadUrl: `/v1/attachments/${attachmentId}/content`, uploadToken: `token-${attachmentId}`, maxBytes: 8388608 })
      }
      m = path.match(/^\/v1\/attachments\/([^/]+)\/content$/)
      if (method === 'PUT' && m) return json(201, { attachmentId: m[1], state: 'sealed' })
      if (method === 'GET' && m) return route.fulfill({ status: 200, contentType: 'image/png', body: PIXEL })
      return json(404, { code: 'not_found' })
    })
  }
}
