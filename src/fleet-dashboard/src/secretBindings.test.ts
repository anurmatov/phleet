import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  agentSummary, bindingBody, emptyForm, formFor, splitList, statusProblem, withKind,
  type SecretBinding, type SecretBindingsStatus,
} from './secretBindings.ts'

const binding = (over: Partial<SecretBinding> = {}): SecretBinding => ({
  envKeyName: 'EXAMPLE_API_KEY', kind: 'static', hosts: ['api.example.test'], headerName: 'Authorization',
  exposeAs: ['EXAMPLE_API_KEY'], appIdEnvKey: null, agentAttachable: false, valuePresent: true,
  agents: [], pendingReprovision: [], ...over,
})

const status = (over: Partial<SecretBindingsStatus> = {}): SecretBindingsStatus => ({
  enabled: true, fault: null, subnet: '10.213.96.0/22', address: '10.213.96.2', caPresent: true, bindings: [], ...over,
})

test('splitList accepts commas, spaces and newlines and drops blanks', () => {
  assert.deepEqual(splitList(' api.example.test,\n other.example.test  third.example.test ,, '),
    ['api.example.test', 'other.example.test', 'third.example.test'])
  assert.deepEqual(splitList('   '), [])
})

test('bindingBody lowercases hosts, defaults the header and sends an App id key only for a GitHub App', () => {
  const body = bindingBody({ ...emptyForm(), envKeyName: 'K', hosts: 'API.Example.test', headerName: ' ', appIdEnvKey: 'IGNORED' })
  assert.deepEqual(body, {
    kind: 'static', hosts: ['api.example.test'], headerName: 'Authorization', exposeAs: [], appIdEnvKey: null, agentAttachable: false,
  })
  const app = bindingBody(withKind({ ...emptyForm(), envKeyName: 'GITHUB_APP_PEM' }, 'github_app'))
  assert.equal(app.kind, 'github_app')
  assert.deepEqual(app.hosts, ['api.github.com', 'github.com', 'uploads.github.com'])
  assert.deepEqual(app.exposeAs, ['GH_TOKEN', 'GITHUB_TOKEN'])
  assert.equal(app.appIdEnvKey, 'GITHUB_APP_ID')
})

test('withKind keeps what the operator already typed, and clears the App id key when leaving GitHub App', () => {
  const typed = withKind({ ...emptyForm(), hosts: 'ghe.example.test', exposeAs: 'GH_ENTERPRISE_TOKEN' }, 'github_app')
  assert.equal(typed.hosts, 'ghe.example.test')
  assert.equal(typed.exposeAs, 'GH_ENTERPRISE_TOKEN')
  assert.equal(withKind(typed, 'static').appIdEnvKey, '')
})

test('formFor shows the default exposure as blank and a custom one as typed', () => {
  assert.equal(formFor(binding()).exposeAs, '')
  assert.equal(formFor(binding({ exposeAs: ['GH_TOKEN', 'GITHUB_TOKEN'] })).exposeAs, 'GH_TOKEN, GITHUB_TOKEN')
  assert.equal(formFor(binding({ hosts: ['a.example.test', 'b.example.test'] })).hosts, 'a.example.test, b.example.test')
})

test('statusProblem names the first thing that stops placeholders from working', () => {
  assert.equal(statusProblem(status()), null)
  assert.match(statusProblem(status({ enabled: false }))!, /FLEET_SECRETS_ENABLED/)
  assert.match(statusProblem(status({ caPresent: false }))!, /CA/)
  assert.equal(statusProblem(status({ enabled: false, fault: 'secret_proxy_subnet_invalid: x' })), 'secret_proxy_subnet_invalid: x')
})

test('agentSummary lists the agents and counts those still to reprovision', () => {
  assert.equal(agentSummary(binding()), 'no agent references it')
  const two = binding({
    agents: [{ agent: 'agent1', placeholderHeld: true }, { agent: 'agent2', placeholderHeld: false }],
    pendingReprovision: ['agent2'],
  })
  assert.equal(agentSummary(two), 'agent1, agent2 (1 to reprovision)')
  assert.equal(agentSummary({ ...two, pendingReprovision: [] }), 'agent1, agent2')
})
