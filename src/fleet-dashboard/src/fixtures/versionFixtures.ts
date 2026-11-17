// Versions to compare, for the comparison's unit tests, the view tests and the tour. Invented
// content, shaped like the real thing: an instruction is prose with long lines, and a workflow
// definition arrives as one line of JSON a few hundred thousand characters long.

const lines = (text: string) => text.trim().split('\n')
const edit = (base: string[], changes: Record<number, string[]>) =>
  base.flatMap((line, i) => changes[i] ?? [line])
const at = (base: string[], line: string) => {
  const i = base.indexOf(line)
  if (i < 0) throw new Error(`fixture line not found: ${line}`)
  return i
}

const HANDBOOK_V1 = lines(`
# Working agreement

You work in this repository beside other agents. Read the issue before the code.

## Before you start

- Check that nobody else has an open pull request for the same issue.
- Read the project context for the area you are about to change.
- Write down what you expect to change before you change it.

## While you work

- Keep each change small enough to review in one sitting.
- Run the tests that cover the files you touched.
- Say what you verified and what you did not.
- Never commit a secret, a token or a private host name.

## Reviews

A review answers one question: is this change safe to merge as it stands?
Lead with the finding, then the evidence, then what you would do instead.
Do not approve a change you have not read in full.

## Handing over

- Leave the branch in a state the next agent can continue from.
- Record open questions in the pull request, not in chat.
- Close what you opened.

## Escalation

Ask a person when a decision changes cost, access or what users see.
Wait for the answer; do not guess and continue.

## Style

- Plain sentences.
- No headings in short answers.
- Quote exact error text.
`)

// v2: one sentence reworded near the top, one line added near the bottom.
const HANDBOOK_V2 = edit(HANDBOOK_V1, {
  [at(HANDBOOK_V1, 'You work in this repository beside other agents. Read the issue before the code.')]:
    ['You work in this repository beside other agents. Read the whole issue, and its comments, before the code.'],
  [at(HANDBOOK_V1, '- Quote exact error text.')]: ['- Quote exact error text.', '- Give file paths in full.'],
})

// v3: the three review lines replaced by three others, and a rule tightened further down.
const HANDBOOK_V3 = edit(HANDBOOK_V2, {
  [at(HANDBOOK_V2, 'A review answers one question: is this change safe to merge as it stands?')]:
    ['A review answers two questions: is this change correct, and is it safe to merge as it stands?'],
  [at(HANDBOOK_V2, 'Lead with the finding, then the evidence, then what you would do instead.')]:
    ['Lead with the most serious finding, then the evidence for it, then what you would do instead.'],
  [at(HANDBOOK_V2, 'Do not approve a change you have not read in full.')]:
    ['Do not approve a change you have not read in full, and say which parts you only skimmed.'],
  [at(HANDBOOK_V2, 'Wait for the answer; do not guess and continue.')]:
    ['Wait for the answer; do not guess and continue. If nobody answers within a day, say so in the pull request.'],
})

// v4: a line removed.
const HANDBOOK_V4 = edit(HANDBOOK_V3, { [at(HANDBOOK_V3, '- No headings in short answers.')]: [] })

/** Four versions of one instruction, oldest first. Each ends with a newline, as an editor saves it. */
export const HANDBOOK_VERSIONS = [HANDBOOK_V1, HANDBOOK_V2, HANDBOOK_V3, HANDBOOK_V4].map(v => v.join('\n') + '\n')

// ── A workflow definition ───────────────────────────────────────────────────────────────────────

const delegate = (target: string, instruction: string, timeoutMinutes = 30) =>
  ({ type: 'delegate', target, instruction, timeoutMinutes, retry: { attempts: 2, backoffSeconds: 30 } })

const reviewSteps = (minutes: number) => [
  delegate('reviewer', 'Review the pull request and answer approved or changes_requested.', minutes),
  { type: 'wait_for_signal', signalName: 'review-verdict', timeoutMinutes: 240 },
]

const RELEASE_V1 = {
  type: 'sequence',
  steps: [
    { type: 'set_variable', name: 'attempt', value: 0 },
    delegate('planner', 'Read the issue and write a plan with one step per file.'),
    delegate('developer', 'Implement the plan on a new branch and open a pull request.', 120),
    ...reviewSteps(45),
    { type: 'http_request', method: 'POST', url: 'https://ci.example.test/builds', body: { ref: '{{vars.branch}}' } },
    delegate('developer', 'Merge the pull request once the build is green.'),
  ],
}

// v2: the review waits longer, and the request names the pull request.
const RELEASE_V2 = {
  ...RELEASE_V1,
  steps: [
    ...RELEASE_V1.steps.slice(0, 3),
    ...reviewSteps(60),
    { type: 'http_request', method: 'POST', url: 'https://ci.example.test/builds', body: { ref: '{{vars.branch}}', pullRequest: '{{vars.pr}}' } },
    RELEASE_V1.steps[6],
  ],
}

// v3: the two review steps move inside a new loop. Every line of them shifts to the right, and
// nothing in them changes.
const RELEASE_V3 = {
  ...RELEASE_V2,
  steps: [
    ...RELEASE_V2.steps.slice(0, 3),
    { type: 'loop', maxIterations: 3, steps: reviewSteps(60) },
    ...RELEASE_V2.steps.slice(5),
  ],
}

/** Three versions of one definition, oldest first, each as the one line the orchestrator stores. */
export const RELEASE_VERSIONS = [RELEASE_V1, RELEASE_V2, RELEASE_V3].map(v => JSON.stringify(v))

/**
 * A definition the size of a real one: about 240,000 characters on one line, between 5,000 and
 * 8,000 lines once laid out. `revised` is the next version of the same definition: a timeout
 * changed, an instruction reworded, a run of steps moved inside a new loop, a step added and one
 * removed.
 */
export function largeDefinition(revised = false): string {
  const step = (i: number) => ({
    type: 'delegate',
    target: `agent${i % 7}`,
    instruction:
      `Step ${i}: read the notes left by step ${i - 1}, then carry out part ${i} of the plan. `
      + 'Keep the change small, run the tests that cover it, and say exactly what you verified and what you did not. '.repeat(5)
      + `Write the result where step ${i + 1} will look for it.`,
    timeoutMinutes: 30 + (i % 5) * 15,
    retry: { attempts: 2, backoffSeconds: 30, nonRetryable: ['invalid_input', 'not_found'] },
    output: { variable: `result${i}`, schema: { type: 'object', required: ['summary'], properties: { summary: { type: 'string' } } } },
  })
  const steps: unknown[] = Array.from({ length: 250 }, (_, i) => step(i + 1))
  if (revised) {
    steps[19] = { ...step(20), timeoutMinutes: 240 }
    steps[119] = { ...step(120), instruction: step(120).instruction.replace('Keep the change small', 'Keep every change small') }
    steps.splice(200, 4, { type: 'loop', maxIterations: 3, steps: steps.slice(200, 204) })
    steps.splice(230, 1)
    steps.push({ type: 'wait_for_signal', signalName: 'release-approval', timeoutMinutes: 1440 })
  }
  return JSON.stringify({ type: 'sequence', steps })
}
