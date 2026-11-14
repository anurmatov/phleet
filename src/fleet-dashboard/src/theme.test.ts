import { test } from 'node:test'
import assert from 'node:assert/strict'
import { THEME_KEY, applyTheme, nextTheme, parseTheme, readTheme, themeLabel } from './theme.ts'

function fakeRoot() {
  const attributes: Record<string, string> = {}
  return {
    attributes,
    setAttribute: (name: string, value: string) => { attributes[name] = value },
    removeAttribute: (name: string) => { delete attributes[name] },
  }
}

function fakeStore(initial: Record<string, string> = {}) {
  const values = { ...initial }
  return {
    values,
    getItem: (key: string) => values[key] ?? null,
    setItem: (key: string, value: string) => { values[key] = value },
    removeItem: (key: string) => { delete values[key] },
  }
}

test('anything that is not a stored choice means follow the system', () => {
  assert.equal(parseTheme('light'), 'light')
  assert.equal(parseTheme('dark'), 'dark')
  for (const other of [null, undefined, '', 'system', 'LIGHT', 'sepia']) assert.equal(parseTheme(other), 'system')
})

test('the toggle steps system, light, dark and round again', () => {
  assert.equal(nextTheme('system'), 'light')
  assert.equal(nextTheme('light'), 'dark')
  assert.equal(nextTheme('dark'), 'system')
})

test('a chosen theme is put on the root and remembered', () => {
  const root = fakeRoot()
  const store = fakeStore()

  applyTheme('dark', root, store)

  assert.equal(root.attributes['data-theme'], 'dark')
  assert.equal(store.values[THEME_KEY], 'dark')
  assert.equal(readTheme(store), 'dark')
})

test('following the system sets no attribute and stores nothing, so the stylesheet decides', () => {
  const root = fakeRoot()
  const store = fakeStore({ [THEME_KEY]: 'light' })
  applyTheme('light', root, store)

  applyTheme('system', root, store)

  assert.equal('data-theme' in root.attributes, false)
  assert.equal(THEME_KEY in store.values, false)
  assert.equal(readTheme(store), 'system')
})

test('a browser that refuses storage still gets the theme for this page', () => {
  const root = fakeRoot()
  const refusing = {
    getItem: () => { throw new Error('denied') },
    setItem: () => { throw new Error('denied') },
    removeItem: () => { throw new Error('denied') },
  }

  assert.equal(readTheme(refusing), 'system')
  applyTheme('light', root, refusing)

  assert.equal(root.attributes['data-theme'], 'light')
})

test('each choice says what it is', () => {
  const labels = (['system', 'light', 'dark'] as const).map(themeLabel)
  assert.equal(new Set(labels).size, 3)
})
