import { test } from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import React, { type ReactElement } from 'react'
import { createServer } from 'vite'
import type { SignalModalState, WorkflowSummary } from './types'

// Exercise production handlers and async effects without adding a DOM/test-renderer dependency.
// Hook state belongs to each mounted component; cleanup runs before a changed effect.
class Hooks {
  slots: unknown[] = []
  effects: (() => void)[] = []
  cursor = 0
  render<T>(component: (props: T) => ReactElement, props: T): ReactElement {
    const internals = (React as unknown as { __SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED: { ReactCurrentDispatcher: { current: unknown } } })
      .__SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED.ReactCurrentDispatcher
    const previous = internals.current
    this.cursor = 0
    internals.current = {
      useState: (initial: unknown) => {
        const i = this.cursor++
        if (!(i in this.slots)) this.slots[i] = typeof initial === 'function' ? initial() : initial
        return [this.slots[i], (next: unknown) => { this.slots[i] = typeof next === 'function' ? next(this.slots[i]) : next }]
      },
      useRef: (value: unknown) => {
        const i = this.cursor++
        if (!(i in this.slots)) this.slots[i] = { current: value }
        return this.slots[i]
      },
      useCallback: (fn: unknown) => { this.cursor++; return fn },
      useEffect: (effect: () => (() => void) | undefined, deps: unknown[]) => {
        const i = this.cursor++
        const old = this.slots[i] as { deps: unknown[]; cleanup?: () => void } | undefined
        if (!old || deps.some((dep, index) => dep !== old.deps[index])) {
          old?.cleanup?.()
          const state = { deps, cleanup: undefined as (() => void) | undefined }
          this.slots[i] = state
          this.effects.push(() => { state.cleanup = effect() })
        }
      },
    }
    try { return component(props) } finally { internals.current = previous }
  }
  flush() { for (const effect of this.effects.splice(0)) effect() }
}
type Element = ReactElement<{ children?: unknown; onClick?: () => void; disabled?: boolean; className?: string }>
function elements(node: unknown): Element[] {
  if (Array.isArray(node)) return node.flatMap(elements)
  if (!React.isValidElement(node)) return []
  const element = node as Element
  if (typeof element.type === 'function') return elements((element.type as (props: unknown) => unknown)(element.props))
  return [element, ...elements(element.props.children)]
}
function text(node: unknown): string {
  if (Array.isArray(node)) return node.map(text).join('')
  return React.isValidElement(node) ? text((node as Element).props.children) : typeof node === 'string' ? node : ''
}
const send = (tree: ReactElement) => elements(tree).find(e => e.type === 'button' && text(e.props.children) === 'Send')!
const tick = () => new Promise<void>(resolve => setImmediate(resolve))
const wf: WorkflowSummary = {
  workflowId: 'human-approval-123e4567-e89b-42d3-a456-426614174000', runId: 'run-1',
  workflowType: 'UweHumanApprovalRequestWorkflow', namespace: 'fleet', phase: 'request-approval', gateRef: 'run-1',
  taskQueue: 'fleet', status: 'Running', startTime: '2026-11-12T12:00:00Z', closeTime: null,
  issueNumber: null, prNumber: null, repo: null, docPrs: null,
}
const request = { Action: ' \"read\"\nКыргыз \\ {{config.Secret}} ', Scope: 'only this report', RequesterAgent: 'agent1' }
const history = [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify(request) }]

test('both workflows request actions must load the exact request before sending', async t => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    define: { 'import.meta.env.VITE_AUTH_TOKEN': JSON.stringify('synthetic-operator') },
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  const originalFetch = globalThis.fetch
  try {
    const { default: View } = await server.ssrLoadModule('/src/components/WorkflowsView.tsx')
    const { default: Modal } = await server.ssrLoadModule('/src/components/SignalModal.tsx')
    for (const label of ['Approve', 'Reject']) {
      for (const success of [false, true]) await t.test(`${label}: ${success ? 'full literal request before Send' : 'history error refuses Send'}`, async () => {
        let modal: SignalModalState | null = null
        let sends = 0
        let resolve!: (response: Response) => void
        const urls: string[] = []
        globalThis.fetch = ((url: string) => { urls.push(url); return new Promise<Response>(r => { resolve = r }) }) as typeof fetch
        const sig = { name: 'request-approval', label: 'request approval', validPhases: ['request-approval'],
          buttons: ['Approve', 'Reject'].map(label => ({ label, requiresComment: false, payload: JSON.stringify({ Decision: label === 'Approve' ? 'approved' : 'rejected' }) })) }
        const tree = new Hooks().render(View, {
          workflows: [wf], filteredWorkflows: [wf], filteredCompletedWorkflows: [], completedCollapsed: true,
          completedLoading: false, nsFilter: 'all', namespaces: [], agentByWorkflow: {}, wfActionMsg: {},
          signalStates: {}, signalMsg: {}, sentSignals: new Set(), wfMenuOpen: {}, selectedWf: null,
          wfHistory: [], wfHistoryLoading: false, wfHistoryError: null, expandedEvents: new Set(),
          getSignalDefs: () => [sig], wfActionState: () => 'idle', signalKey: () => 'request', wfKey: () => 'request',
          wfStatusClass: () => '', onSendSignal: () => { sends++ },
          onSignalClick: (wf: WorkflowSummary, signal: typeof sig, button: typeof sig.buttons[number]) => {
            modal = { wf, signalName: signal.name, button, comment: '' }
          },
        })
        const button = elements(tree).find(e => e.type === 'button' && e.props.className?.includes('wf-btn-signal') && text(e.props.children) === label)!
        assert.ok(button)
        button.props.onClick!()
        assert.equal(sends, 0)
        assert.ok(modal, 'workflow button must open the exact-request modal, not the generic confirm bar')
        const hooks = new Hooks()
        const props = { modal, onClose: () => {}, onCommentChange: () => {}, onSend: () => { sends++ } }
        let confirmation = hooks.render(Modal, props)
        assert.equal(send(confirmation).props.disabled, true)
        send(confirmation).props.onClick!()
        assert.equal(sends, 0, 'even a directly invoked loading handler must refuse')
        hooks.flush()
        assert.equal(urls.length, 1)
        assert.match(urls[0], /\?runId=run-1$/)
        resolve(new Response(JSON.stringify(history), { status: success ? 200 : 503 }))
        await tick()
        confirmation = hooks.render(Modal, props)
        if (success) {
          assert.ok(text(confirmation).includes(request.Action))
          assert.ok(text(confirmation).includes(request.Scope))
          assert.equal(send(confirmation).props.disabled, false)
        } else {
          assert.ok(text(confirmation).includes('Cannot load the exact request'))
          assert.equal(send(confirmation).props.disabled, true)
        }
        send(confirmation).props.onClick!()
        assert.equal(sends, success ? 1 : 0)
      })
    }
  } finally { globalThis.fetch = originalFetch; await server.close() }
})
