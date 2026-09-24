// Wire-capture stub for #349: an Anthropic-compatible endpoint that records what Claude Code puts in
// the thinking and output_config fields of every /v1/messages request, and answers each with a
// minimal final message (streamed when asked). No model is involved and no message content is kept.
//
// One line per messages request:  thinking=<type|absent> effort=<value|absent>
// Anything else (e.g. an /api/hello probe) is answered but not recorded.
import http from 'node:http'
import { appendFileSync } from 'node:fs'

const out = process.argv[2] ?? '/tmp/claude-local-wire.txt'
const port = Number(process.argv[3] ?? 11434)
const host = process.argv[4] ?? '0.0.0.0'

const minimalMessage = { id: 'msg_stub', type: 'message', role: 'assistant', model: 'stub' }

http.createServer((req, res) => {
  let body = ''
  req.on('data', chunk => { body += chunk })
  req.on('end', () => {
    let parsed = {}
    try { parsed = JSON.parse(body) } catch { /* not JSON: not a messages request */ }

    const isMessages = /^\/v1\/messages(\?|$)/.test(req.url ?? '')
    if (isMessages) {
      const thinking = parsed.thinking?.type ?? 'absent'
      const effort = parsed.output_config?.effort ?? 'absent'
      appendFileSync(out, `thinking=${thinking} effort=${effort}\n`)
    }

    if (isMessages && parsed.stream) {
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`)
      ev('message_start', { message: { ...minimalMessage, content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 0 } } })
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'ok' } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } })
      ev('message_stop', {})
      res.end()
      return
    }

    res.writeHead(200, { 'content-type': 'application/json' })
    res.end(JSON.stringify({
      ...minimalMessage,
      content: [{ type: 'text', text: 'ok' }],
      stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 },
    }))
  })
}).listen(port, host, () => console.log(`stub listening on ${host}:${port}, recording to ${out}`))
