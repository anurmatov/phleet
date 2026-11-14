import { test } from 'node:test'
import assert from 'node:assert/strict'
import { NAV_GROUPS, NAV_ITEMS, TAB_VIEWS, navItem, navigationCoversEveryView } from './navigation.ts'
import { ACTIVE_VIEWS } from './activeViews.ts'

test('every page is in the navigation exactly once', () => {
  assert.equal(navigationCoversEveryView(), true)
  assert.equal(NAV_ITEMS.length, ACTIVE_VIEWS.length)
})

test('no group is empty and no two pages share a label', () => {
  for (const group of NAV_GROUPS) assert.ok(group.items.length > 0, `${group.label} is empty`)
  const labels = NAV_ITEMS.map(item => item.label)
  assert.equal(new Set(labels).size, labels.length)
})

test('the phone tabs are pages that exist, with room left for More', () => {
  for (const view of TAB_VIEWS) assert.ok(ACTIVE_VIEWS.includes(view))
  assert.ok(TAB_VIEWS.length <= 4)
})

test('a page is found by its view', () => {
  assert.equal(navItem('memory').label, 'Memory')
  assert.equal(navItem('wf-definitions').icon, 'definitions')
})
