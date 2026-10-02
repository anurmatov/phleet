// Render the production priority badge, not a test copy.
// Node strips TS but not TSX, so compile the actual function declaration.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import ts from 'typescript'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'

function declaration(file: string, name: string): string {
  const text = readFileSync(new URL(file, import.meta.url), 'utf8')
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const node = source.statements.find(n => ts.isFunctionDeclaration(n) && n.name?.text === name)
  assert.ok(node, `${name} must be the production declaration`)
  return node.getText(source).replace('export default ', '').replace('export ', '')
}

const source = declaration('./components/AgentCard.tsx', 'PriorityBadge') + '\nexports.PriorityBadge = PriorityBadge'
const compiled = ts.transpileModule(source, {
  compilerOptions: { jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.CommonJS },
}).outputText
const exports: { PriorityBadge?: React.ComponentType<{ priority?: boolean }> } = {}
new Function('require', 'exports', compiled)(createRequire(import.meta.url), exports)
assert.ok(exports.PriorityBadge)
const Badge = exports.PriorityBadge
for (const priority of [true, false, undefined]) {
  test(`production queue badge priority=${priority}`, () => {
    const html = renderToStaticMarkup(createElement(Badge, { priority }))
    assert.equal(html.includes('priority'), priority === true)
    if (priority !== true) assert.equal(html, '')
  })
}
