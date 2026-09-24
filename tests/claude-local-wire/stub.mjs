// Wire-capture stub for #349: an Anthropic-compatible /v1/messages endpoint that records the
// thinking and output_config fields of every request and answers with a minimal final message.
// No model is involved — this pins what Claude Code 2.1.280 puts on the wire per effort setting.
import http from 'node:http'
import { appendFileSync } from 'node:fs'

const out = process.argv[2] ?? '/tmp/claude-local-wire.jsonl'
const port = Number(process.argv[3] ?? 11434)

const server = http.createServer((req, res) => {
  let body = ''
  req.on('data', chunk => { body += chunk })
  req.on('end', () => {
    let record = { url: req.url }
    try {
      const parsed = JSON.parse(body)
      record.thinking = parsed.thinking ?? null
      record.output_config = parsed.output_config ?? null
    } catch { record.parse_error = true }
    appendFileSync(out, JSON.stringify(record) + '\n')
    res.writeHead(200, { 'content-type': 'application/json' })
    res.end(JSON.stringify({
      id: 'msg_stub', type: 'message', role: 'assistant', model: 'stub',
      content: [{ type: 'text', text: 'ok' }],
      stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 },
    }))
  })
})

server.listen(port, () => console.log(`stub listening on ${port}, recording to ${out}`))
