// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { fileURLToPath } from 'node:url'

const SRC = fileURLToPath(new URL('.', import.meta.url))

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) return sourceFiles(full)
    return /\.tsx?$/.test(entry.name) && !entry.name.endsWith('.test.ts') ? [full] : []
  })
}

test('stored memory content is rendered like a chat reply: no image is loaded, no HTML is run', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { default: AgentMarkdown } = await server.ssrLoadModule('/src/components/AgentMarkdown.tsx')
    const html = renderToStaticMarkup(createElement(AgentMarkdown, {
      text: [
        '# Notes',
        '',
        '![pixel](https://tracker.example/p.png?reader=1) and ![](https://tracker.example/q.png)',
        '',
        '<img src="https://tracker.example/raw.png"><script>alert(1)</script>',
        '',
        '[docs](https://example.com/docs) [bad](javascript:alert(2))',
      ].join('\n'),
    }))

    assert.match(html, /<h1>Notes<\/h1>/)
    // A remote image would tell its server who opened this memory and when. It is a link instead.
    assert.equal(/<img\b/.test(html), false, html)
    assert.match(html, /<a href="https:\/\/tracker\.example\/p\.png\?reader=1" target="_blank" rel="noopener noreferrer">pixel<\/a>/)
    assert.match(html, /<a href="https:\/\/tracker\.example\/q\.png" target="_blank" rel="noopener noreferrer">image<\/a>/)
    assert.equal(/<script\b/.test(html), false, html)
    assert.match(html, /&lt;script&gt;/)
    assert.match(html, /<a href="https:\/\/example\.com\/docs" target="_blank" rel="noopener noreferrer">docs<\/a>/)
    assert.equal(html.includes('javascript:'), false, html)
  } finally {
    await server.close()
  }
})

test('nothing but AgentMarkdown turns Markdown into elements', () => {
  // A second, bare <ReactMarkdown> is how memory content came to load remote images while chat
  // refused to. Every view that shows agent-written Markdown goes through the one component.
  const importers = sourceFiles(SRC)
    .filter(file => /from\s+['"]react-markdown['"]/.test(readFileSync(file, 'utf8')))
    .map(file => file.slice(SRC.length).split('\\').join('/'))
  assert.deepEqual(importers, ['components/AgentMarkdown.tsx'])
})
