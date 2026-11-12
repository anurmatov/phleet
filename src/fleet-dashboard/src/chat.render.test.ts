import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { fileURLToPath } from 'node:url'
import type { Entry } from './chatTranscript'
import type { ChatState, PendingSend, StopReason } from './chatConnection'

// Renders the production component with the installed JSX compiler and react-markdown. No HTTP
// listener and no API requests.
test('agent replies are Markdown with safe links and no raw HTML; the person’s own words stay plain text', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { default: ChatTranscript } = await server.ssrLoadModule('/src/components/ChatTranscript.tsx')

    const entries: Entry[] = [
      { type: 'you', key: 'e1', seq: 1, submissionId: 'S1', text: 'Is **this** bold? <b>no</b> [x](https://example.com)' },
      {
        type: 'agent', key: 'e2', seq: 2, note: null,
        text: [
          'Here is **bold**, `code` and a [link](https://example.com/docs).',
          '',
          '<script>alert(1)</script><img src="https://tracker.example/p.png" onerror="alert(2)">',
          '',
          '[bad](javascript:alert(3)) ![chart](https://images.example/chart.png)',
          '',
          '```',
          'two  spaces',
          '```',
        ].join('\n'),
      },
      { type: 'agent', key: 'e3', seq: 3, text: '', note: 'The agent had nothing to add.' },
      { type: 'system', key: 'e4', tone: 'error', text: 'The turn failed.' },
    ]
    const pending: PendingSend[] = [
      { submissionId: 'p1', text: 'not sent', status: 'failed', acceptedSeq: null, problem: 'Not confirmed.', canRetry: true },
      { submissionId: 'p2', text: 'on its way', status: 'sending', acceptedSeq: null, problem: null, canRetry: false },
    ]
    const html = renderToStaticMarkup(createElement(ChatTranscript, {
      entries, status: { S1: 'waiting' }, pending, onRetry: () => {}, onDiscard: () => {},
    }))

    // The person's message: literal text, never Markdown or HTML.
    assert.ok(html.includes('Is **this** bold? &lt;b&gt;no&lt;/b&gt; [x](https://example.com)'), html)
    assert.ok(html.includes('Waiting — the agent is busy with something else'))

    // The agent's reply: Markdown.
    assert.ok(html.includes('<strong>bold</strong>'))
    assert.ok(html.includes('<code>code</code>'))
    assert.ok(html.includes('<pre><code>two  spaces\n</code></pre>'))

    // Every link opens in a new tab with no handle back to this page.
    const links = [...html.matchAll(/<a [^>]*>/g)].map(m => m[0])
    assert.equal(links.length, 2, html)
    for (const link of links) {
      assert.match(link, /target="_blank"/)
      assert.match(link, /rel="noopener noreferrer"/)
    }
    assert.ok(links.some(l => l.includes('href="https://example.com/docs"')))

    // No raw HTML, no script, no image loaded, no javascript: URL.
    assert.doesNotMatch(html, /<script/i)
    assert.doesNotMatch(html, /<img/i)
    assert.doesNotMatch(html, /<[a-z]+[^>]*\son[a-z]+=/i)
    assert.doesNotMatch(html, /href="javascript:/i)
    assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'), 'raw HTML is shown as text')
    // A link whose URL was unsafe keeps its words and loses the link.
    assert.ok(html.includes('<span>bad</span>'), html)
    assert.doesNotMatch(html, /href=""/)
    // An image is a link to it, labelled with its alt text.
    assert.ok(html.includes('<a href="https://images.example/chart.png" target="_blank" rel="noopener noreferrer">chart</a>'))

    // An empty answer shows its note and no empty bubble.
    assert.ok(html.includes('The agent had nothing to add.'))
    assert.equal((html.match(/chat-bubble--agent/g) ?? []).length, 1)

    assert.ok(html.includes('chat-system--error'))
    assert.ok(html.includes('The turn failed.'))

    // Sends not yet in the transcript: a failure with Retry and Discard, and one on its way.
    assert.ok(html.includes('Not confirmed.'))
    assert.match(html, />Retry</)
    assert.match(html, />Discard</)
    assert.ok(html.includes('Sending…'))
  } finally { await server.close() }
})

test('the connection line says where the thread stands and offers only actions that can help', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { ConnectionLine } = await server.ssrLoadModule('/src/components/ChatView.tsx')
    const { emptyTranscript } = await server.ssrLoadModule('/src/chatTranscript.ts')
    const base: ChatState = {
      phase: 'live', connectedOnce: true, stopReason: null, problem: null, conversationId: 'C1',
      transcript: emptyTranscript(), pending: [], limits: { inboundTextBytes: 32768, catchUpLimitMax: 1000 },
      stopping: false, notice: null,
    }
    const line = (patch: Partial<ChatState>) =>
      renderToStaticMarkup(createElement(ConnectionLine, { state: { ...base, ...patch }, onReconnect: () => {} }))
    const buttons = (html: string) => [...html.matchAll(/<button[^>]*>([^<]*)<\/button>/g)].map(m => m[1])

    assert.match(line({}), />Connected</)
    assert.match(line({ phase: 'connecting', connectedOnce: false }), />Connecting…</)
    assert.match(line({ phase: 'connecting', connectedOnce: true }), />Reconnecting…</)

    const waiting = line({ phase: 'waiting', problem: 'The connection to the chat service was lost.' })
    assert.ok(waiting.includes('The connection to the chat service was lost. Reconnecting shortly.'))
    assert.deepEqual(buttons(waiting), ['Reconnect now'])

    const stopped = (stopReason: StopReason) => line({ phase: 'stopped', stopReason, problem: `problem: ${stopReason}` })
    assert.deepEqual(buttons(stopped('superseded')), ['Use it here'])
    assert.deepEqual(buttons(stopped('agent-unavailable')), ['Try again'])
    assert.deepEqual(buttons(stopped('signed-out')), ['Try again'])
    assert.deepEqual(buttons(stopped('refused')), ['Try again'])
    // Asking again cannot change these, so nothing is offered.
    assert.deepEqual(buttons(stopped('not-set-up')), [])
    assert.deepEqual(buttons(stopped('forbidden')), [])
    assert.ok(stopped('not-set-up').includes('problem: not-set-up'))
  } finally { await server.close() }
})
