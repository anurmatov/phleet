// Render the production toggle with the existing TypeScript compiler and React server renderer.
// Node strips TS but not TSX; compile only the two actual function declarations (no test copy).
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
const source = declaration('./components/FieldHint.tsx', 'FieldHint') + '\n' +
  declaration('./components/AgentConfigModal.tsx', 'JournalToggle') + '\nexports.JournalToggle = JournalToggle'
const compiled = ts.transpileModule(source, {
  compilerOptions: { jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.CommonJS },
}).outputText
const exports: { JournalToggle?: React.ComponentType<{ checked: boolean; disabled: boolean; onChange: () => void }> } = {}
new Function('require', 'exports', compiled)(createRequire(import.meta.url), exports)
assert.ok(exports.JournalToggle)
const Toggle = exports.JournalToggle

for (const checked of [false, true]) {
  for (const disabled of [false, true]) {
    test(`journal toggle renders checked=${checked}, disabled status=${disabled}`, () => {
      const html = renderToStaticMarkup(createElement(Toggle, { checked, disabled, onChange: () => {} }))
      assert.equal(/disabled=""/.test(html), !checked && disabled)
      assert.equal(/checked=""/.test(html), checked)
      assert.equal(html.includes('Comms journal not enabled'), disabled)
    })
  }
}
