import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  compareTexts, diffSequences, expand, layOut, prettyJson, splitLines,
  CONTEXT_LINES, EXPAND_STEP, MAX_COMPARE_CHARS,
  type Comparison, type CompareResult, type DiffRow, type Section,
} from './versionDiff.ts'
import { HANDBOOK_VERSIONS, RELEASE_VERSIONS, largeDefinition } from './fixtures/versionFixtures.ts'

function ok(result: CompareResult): Comparison {
  assert.equal(result.ok, true, 'the comparison was refused')
  return result as Comparison
}

/** The rows as a unified diff reads: a mark, then the line. */
const marked = (rows: DiffRow[]) => rows.map(r => `${{ same: ' ', add: '+', remove: '-' }[r.kind]}${r.text}`)
const changed = (rows: DiffRow[]) => marked(rows.filter(r => r.kind !== 'same'))

/** Applies a comparison to the older text's lines: what the newer text must then be. */
function apply(oldLines: string[], rows: DiffRow[]): string[] {
  const out: string[] = []
  for (const row of rows) {
    if (row.kind === 'same') out.push(oldLines[row.oldNo! - 1])
    else if (row.kind === 'add') out.push(row.text)
  }
  return out
}

/** A small generator with a seed, so a failure can be run again. */
function random(seed: number): () => number {
  let state = seed
  return () => (state = (state * 1664525 + 1013904223) >>> 0) / 4294967296
}

// ── Lines ───────────────────────────────────────────────────────────────────────────────────────

test('identical texts have no changes', () => {
  const result = ok(compareTexts('one\ntwo\nthree', 'one\ntwo\nthree', 'text'))
  assert.deepEqual(marked(result.rows), [' one', ' two', ' three'])
  assert.deepEqual([result.added, result.removed, result.blocks.length, result.onlySpacing], [0, 0, 0, false])
})

test('against nothing, every line is added; against an empty newer text, every line is removed', () => {
  const first = ok(compareTexts(null, 'one\ntwo', 'text'))
  assert.deepEqual(marked(first.rows), ['+one', '+two'])
  assert.deepEqual([first.added, first.removed, first.blocks], [2, 0, [{ start: 0, end: 2 }]])
  assert.deepEqual(first.rows.map(r => [r.oldNo, r.newNo]), [[null, 1], [null, 2]])

  const emptied = ok(compareTexts('one\ntwo', '', 'text'))
  assert.deepEqual(marked(emptied.rows), ['-one', '-two'])
  assert.deepEqual([emptied.added, emptied.removed], [0, 2])
})

test('a change in the middle is its removed line, then its added one, with both sides numbered', () => {
  const result = ok(compareTexts('a\nb\nc\nd', 'a\nB\nc\nd\ne', 'text'))
  assert.deepEqual(marked(result.rows), [' a', '-b', '+B', ' c', ' d', '+e'])
  assert.deepEqual(result.rows.map(r => [r.oldNo, r.newNo]), [[1, 1], [2, null], [null, 2], [3, 3], [4, 4], [null, 5]])
  assert.deepEqual(result.blocks, [{ start: 1, end: 3 }, { start: 5, end: 6 }])
  assert.deepEqual([result.added, result.removed], [2, 1])
})

test('a run of replaced lines is one change: all the removed lines, then all the added', () => {
  const result = ok(compareTexts('keep\nold 1\nold 2\nold 3\nkeep too', 'keep\nnew 1\nnew 2\nkeep too', 'text'))
  assert.deepEqual(marked(result.rows), [' keep', '-old 1', '-old 2', '-old 3', '+new 1', '+new 2', ' keep too'])
  assert.deepEqual(result.blocks, [{ start: 1, end: 6 }])
})

test('the final newline and CRLF line endings are not lines of their own', () => {
  assert.deepEqual(splitLines('a\nb\n'), ['a', 'b'])
  assert.deepEqual(splitLines('a\r\nb\r\n'), ['a', 'b'])
  assert.deepEqual(splitLines('a\n\n'), ['a', ''])
  assert.deepEqual(splitLines(''), [])

  const newline = ok(compareTexts('a\nb', 'a\nb\n', 'text'))
  assert.deepEqual([newline.added, newline.removed, newline.onlySpacing], [0, 0, true])

  const crlf = ok(compareTexts('a\r\nb\r\nc\r\n', 'a\nB\nc\n', 'text'))
  assert.deepEqual(marked(crlf.rows), [' a', '-b', '+B', ' c'])
  assert.equal(crlf.onlySpacing, false)
})

test('on random texts the rows turn the older text into the newer one, and no shorter script exists', () => {
  const next = random(20261117)
  const words = ['alpha', 'beta', 'gamma', 'delta', '', '  indented']
  /** The longest common subsequence by the full table: slow, and plainly right. */
  const longestCommon = (a: string[], b: string[]) => {
    const table = Array.from({ length: a.length + 1 }, () => new Array<number>(b.length + 1).fill(0))
    for (let i = 1; i <= a.length; i++) {
      for (let j = 1; j <= b.length; j++) {
        table[i][j] = a[i - 1] === b[j - 1] ? table[i - 1][j - 1] + 1 : Math.max(table[i - 1][j], table[i][j - 1])
      }
    }
    return table[a.length][b.length]
  }
  for (let round = 0; round < 3000; round++) {
    const text = () => Array.from({ length: Math.floor(next() * 12) }, () => words[Math.floor(next() * (2 + round % 5))])
    const oldLines = text(), newLines = text()
    // A line "" at the end would be the final newline: join with one, as a saved text has.
    const result = ok(compareTexts(oldLines.map(l => l + '\n').join(''), newLines.map(l => l + '\n').join(''), 'text'))
    const where = `round ${round}: ${JSON.stringify(oldLines)} -> ${JSON.stringify(newLines)}`
    assert.deepEqual(apply(oldLines, result.rows), newLines, where)
    assert.deepEqual(result.rows.filter(r => r.kind !== 'add').map(r => r.text), oldLines, where)
    const common = longestCommon(oldLines, newLines)
    assert.deepEqual([result.removed, result.added], [oldLines.length - common, newLines.length - common], where)
    // Changes are whole: no two of them touch, and inside one no added line comes before a removed one.
    result.blocks.forEach((block, b) => {
      if (b > 0) assert.ok(block.start > result.blocks[b - 1].end, where)
      const kinds = result.rows.slice(block.start, block.end).map(r => r.kind).join(' ')
      assert.match(kinds, /^(remove ?)*(add ?)*$/, where)
    })
  }
})

test('the sequence comparison gives up at its work limit instead of running on', () => {
  const a = Int32Array.from({ length: 4000 }, (_, i) => i)
  const b = Int32Array.from({ length: 4000 }, (_, i) => i + 10_000)
  assert.equal(diffSequences(a, b, 100_000), null)
  const marks = diffSequences(a, b)!
  assert.equal(marks.removed.every(m => m === 1) && marks.added.every(m => m === 1), true)
})

// ── What is shown ───────────────────────────────────────────────────────────────────────────────

const numbered = (count: number, changes: Record<number, string> = {}) =>
  Array.from({ length: count }, (_, i) => changes[i + 1] ?? `line ${i + 1}`).join('\n')

/** Sections as text: `12-18` for shown rows (by the newer side's line numbers), `fold 5` for five hidden lines. */
function shape(result: Comparison, sections: Section[]): string[] {
  return sections.map(s => {
    if (s.kind === 'fold') return `fold ${s.end - s.start}${s.canHead ? ' head' : ''}${s.canTail ? ' tail' : ''}`
    const numbers = result.rows.slice(s.start, s.end).map(r => r.newNo).filter(n => n !== null)
    return `${numbers[0]}-${numbers[numbers.length - 1]}`
  })
}

test('each change is shown with three lines of context, and the rest is folded', () => {
  assert.equal(CONTEXT_LINES, 3)
  const result = ok(compareTexts(numbered(60), numbered(60, { 20: 'changed 20', 40: 'changed 40' }), 'text'))
  const sections = layOut(result)
  assert.deepEqual(shape(result, sections), ['fold 16 tail', '17-23', 'fold 13 head tail', '37-43', 'fold 17 head'])
  // Around the first change: three unchanged lines, the removed and the added line, three more.
  const first = sections[1] as Extract<Section, { kind: 'rows' }>
  assert.deepEqual(marked(result.rows.slice(first.start, first.end)),
    [' line 17', ' line 18', ' line 19', '-line 20', '+changed 20', ' line 21', ' line 22', ' line 23'])
})

test('changes whose contexts meet are shown as one stretch, and a single hidden line is not folded', () => {
  // Six unchanged lines between the changes: their contexts touch.
  const touching = ok(compareTexts(numbered(40), numbered(40, { 10: 'x', 17: 'y' }), 'text'))
  assert.deepEqual(shape(touching, layOut(touching)), ['fold 6 tail', '7-20', 'fold 20 head'])
  // Seven between them would leave one line folded: it is shown instead.
  const oneApart = ok(compareTexts(numbered(40), numbered(40, { 10: 'x', 18: 'y' }), 'text'))
  assert.deepEqual(shape(oneApart, layOut(oneApart)), ['fold 6 tail', '7-21', 'fold 19 head'])
  // Eight leave two, which is a fold.
  const twoApart = ok(compareTexts(numbered(40), numbered(40, { 10: 'x', 19: 'y' }), 'text'))
  assert.deepEqual(shape(twoApart, layOut(twoApart)), ['fold 6 tail', '7-13', 'fold 2 head tail', '16-22', 'fold 18 head'])
  // A change on the first lines has no fold above it.
  const atTop = ok(compareTexts(numbered(40), numbered(40, { 2: 'x' }), 'text'))
  assert.deepEqual(shape(atTop, layOut(atTop)), ['1-5', 'fold 35 head'])
})

test('a fold opens by a step from either end, or entirely', () => {
  const result = ok(compareTexts(numbered(200), numbered(200, { 50: 'x', 150: 'y' }), 'text'))
  assert.deepEqual(shape(result, layOut(result)), ['fold 46 tail', '47-53', 'fold 93 head tail', '147-153', 'fold 47 head'])

  // The fold between the two changes is the second one: id 1.
  let opened = expand({}, 1, 'head')
  assert.deepEqual(shape(result, layOut(result, opened)), ['fold 46 tail', `47-${53 + EXPAND_STEP}`, 'fold 73 head tail', '147-153', 'fold 47 head'])
  opened = expand(opened, 1, 'tail')
  assert.deepEqual(shape(result, layOut(result, opened)), ['fold 46 tail', '47-73', 'fold 53 head tail', '127-153', 'fold 47 head'])
  // The other folds keep their ids while this one is opened.
  opened = expand(opened, 2, 'all')
  assert.deepEqual(shape(result, layOut(result, opened)), ['fold 46 tail', '47-73', 'fold 53 head tail', '127-200'])
  opened = expand(expand(opened, 1, 'head'), 1, 'head')
  assert.deepEqual(shape(result, layOut(result, opened)), ['fold 46 tail', '47-113', 'fold 13 head tail', '127-200'])
  // A step that leaves less than a fold's worth opens the rest.
  opened = expand(opened, 1, 'tail')
  assert.deepEqual(shape(result, layOut(result, opened)), ['fold 46 tail', '47-200'])
  opened = expand(opened, 0, 'all')
  assert.deepEqual(shape(result, layOut(result, opened)), ['1-200'])
})

test('with no change, the whole text is one fold that opens entirely', () => {
  const result = ok(compareTexts(numbered(30), numbered(30), 'text'))
  assert.deepEqual(shape(result, layOut(result)), ['fold 30'])
  assert.deepEqual(shape(result, layOut(result, expand({}, 0, 'all'))), ['1-30'])
})

test('the counts are of lines added and removed, whatever is folded', () => {
  const [v1, v2, v3, v4] = HANDBOOK_VERSIONS
  const second = ok(compareTexts(v1, v2, 'text'))
  assert.deepEqual([second.added, second.removed, second.blocks.length], [2, 1, 2])
  const third = ok(compareTexts(v2, v3, 'text'))
  assert.deepEqual([third.added, third.removed, third.blocks.length], [4, 4, 2])
  const fourth = ok(compareTexts(v3, v4, 'text'))
  assert.deepEqual([fourth.added, fourth.removed, fourth.blocks.length], [0, 1, 1])
  const all = ok(compareTexts(v1, v4, 'text'))
  assert.deepEqual([all.added, all.removed], [6, 6])
  assert.ok(layOut(third).some(s => s.kind === 'fold'), 'an instruction with two changes far apart has folded context')
})

// ── Words ───────────────────────────────────────────────────────────────────────────────────────

/** A row's text with its changed words in [brackets]. */
const withMarks = (row: DiffRow) => row.text.slice(0, row.indent)
  + (row.parts ? row.parts.map(p => (p.changed ? `[${p.text}]` : p.text)).join('') : row.text.slice(row.indent))

test('a reworded sentence is marked by words, not by letters', () => {
  const result = ok(compareTexts(
    'Read the issue before the code.',
    'Read the whole issue, and its comments, before the code.', 'text'))
  assert.deepEqual(result.rows.map(withMarks), [
    'Read the issue before the code.',
    'Read the [whole ]issue[, and its comments,] before the code.',
  ])

  const swapped = ok(compareTexts('The timeout is 30 seconds by default.', 'The timeout is 45 minutes by default.', 'text'))
  assert.deepEqual(swapped.rows.map(withMarks), [
    'The timeout is [30 seconds] by default.',
    'The timeout is [45 minutes] by default.',
  ])
})

test('three replaced lines are compared as one block, whichever line stands opposite which', () => {
  const [, v2, v3] = HANDBOOK_VERSIONS
  const result = ok(compareTexts(v2, v3, 'text'))
  const block = result.blocks[0]
  assert.deepEqual(result.rows.slice(block.start, block.end).map(r => `${r.kind}: ${withMarks(r)}`), [
    'remove: A review answers [one question]: is this change safe to merge as it stands?',
    'remove: Lead with the finding, then the evidence, then what you would do instead.',
    'remove: Do not approve a change you have not read in full.',
    'add: A review answers [two questions]: is this change [correct, and is it ]safe to merge as it stands?',
    'add: Lead with the [most serious ]finding, then the evidence[ for it], then what you would do instead.',
    'add: Do not approve a change you have not read in full[, and say which parts you only skimmed].',
  ])

  // A sentence that moved from one line to two is still matched word for word.
  const rewrapped = ok(compareTexts(
    'intro\nKeep each change small enough to review in one sitting and run the tests.\noutro',
    'intro\nKeep each change small enough\nto review in one sitting and always run the tests.\noutro', 'text'))
  assert.deepEqual(rewrapped.rows.filter(r => r.kind === 'add').map(withMarks),
    ['Keep each change small enough', 'to review in one sitting and [always ]run the tests.'])
})

test('lines that share nothing are not marked by words, and neither is a line that is new from end to end', () => {
  const unrelated = ok(compareTexts('Quote exact error text.', 'Never commit a private host name!', 'text'))
  assert.deepEqual(unrelated.rows.map(r => r.parts), [undefined, undefined])

  const grown = ok(compareTexts('a\nKeep changes small.\nz', 'a\nKeep all changes small.\nGive file paths in full, always.\nz', 'text'))
  assert.deepEqual(grown.rows.filter(r => r.kind !== 'same').map(withMarks),
    ['Keep changes small.', 'Keep [all ]changes small.', 'Give file paths in full, always.'])
  assert.equal(grown.rows[3].parts, undefined)
})

test('a change too large to be worth it keeps its lines and has no word marks', () => {
  const big = (word: string) => Array.from({ length: 500 }, (_, i) => `row ${i} holds ${word} and seven more plain words here`).join('\n')
  const result = ok(compareTexts(big('apples'), big('oranges'), 'text'))
  assert.deepEqual([result.removed, result.added, result.blocks.length], [500, 500, 1])
  assert.equal(result.rows.every(r => r.parts === undefined), true)
})

test('many changed lines share one budget for their words, and finish at once', () => {
  // Each change alone is under its own limit, so a limit per change let these take seconds.
  const lines = (word: string) => Array.from({ length: 400 }, (_, i) => word.repeat(1200) + '\nKEEP' + i).join('\n')
  const steps = (word: string) => JSON.stringify({
    type: 'sequence',
    steps: Array.from({ length: 1500 }, (_, i) => ({ type: 'delegate', target: 'agent1', outputVar: 'result' + i, instruction: word.repeat(500) })),
  })
  const cases = [
    { older: lines('a '), newer: lines('b '), format: 'text', changes: 400 },
    { older: steps('x '), newer: steps('y '), format: 'json', changes: 1500 },
  ] as const
  for (const { older, newer, format, changes } of cases) {
    assert.ok(older.length < MAX_COMPARE_CHARS, `${older.length} characters`)
    compareTexts(older, newer, format)
    const start = performance.now()
    const result = ok(compareTexts(older, newer, format))
    const took = performance.now() - start
    assert.equal(result.blocks.length, changes)
    assert.ok(took < 1000, `${format}: took ${took.toFixed(0)} ms`)
  }
})

test('once the words budget is spent, the changes after it keep their lines without word marks', () => {
  // Every other word kept: each change is marked by words when it is reached, and each costs a lot.
  const line = (word: string) => Array.from({ length: 300 }, () => `keep ${word}`).join(' ')
  const text = (word: string) => Array.from({ length: 400 }, (_, i) => `${line(word)}\nunchanged ${i}`).join('\n')
  const result = ok(compareTexts(text('old'), text('new'), 'text'))
  assert.equal(result.blocks.length, 400)
  const marked = result.blocks.map(block => result.rows.slice(block.start, block.end).some(r => r.parts !== undefined))
  assert.equal(marked[0], true)
  assert.equal(marked[marked.length - 1], false)
  // The ones marked come first, and none is marked after the first one left plain.
  assert.equal(marked.indexOf(false), marked.lastIndexOf(true) + 1)
})

// ── JSON ────────────────────────────────────────────────────────────────────────────────────────

test('JSON is laid out with two spaces, exactly as written', () => {
  const samples = [
    '{"type":"sequence","steps":[{"type":"sleep","seconds":60},{"type":"noop"}],"empty":{},"none":[],"nested":[[1,2],[]]}',
    '{ "text" : "braces { } [ ] , : and a \\"quote\\" and a \\\\ stay in the string" , "n" : -1.5 , "t" : true , "z" : null }',
    '[]', '{}', '"just a string"', '42', '[\n  1,\n  2\n]',
  ]
  for (const sample of samples) assert.equal(prettyJson(sample), JSON.stringify(JSON.parse(sample), null, 2), sample)
  // What a parse and print would change is kept: the order of number-like keys, and long numbers.
  assert.equal(prettyJson('{"10":"ten","2":"two","id":12345678901234567890,"n":1.50e3}'),
    '{\n  "10": "ten",\n  "2": "two",\n  "id": 12345678901234567890,\n  "n": 1.50e3\n}')
  assert.equal(prettyJson('{ not json'), null)
  assert.equal(prettyJson(''), null)
})

test('a one-line definition is compared line by line once laid out', () => {
  const [v1, v2] = RELEASE_VERSIONS
  assert.equal(v1.includes('\n'), false)
  const result = ok(compareTexts(v1, v2, 'json'))
  assert.equal(result.layout, 'json')
  assert.deepEqual(result.notJson, [])
  assert.deepEqual(changed(result.rows), [
    '-      "timeoutMinutes": 45,',
    '+      "timeoutMinutes": 60,',
    '+        "pullRequest": "{{vars.pr}}"',
  ])
  assert.deepEqual(result.rows.filter(r => r.parts).map(withMarks),
    ['      "timeoutMinutes": [45],', '      "timeoutMinutes": [60],'])
  // The member above the new one gained a comma and nothing else: it is unchanged, and shown as it now reads.
  const above = result.rows.find(r => r.text.includes('"ref"'))!
  assert.deepEqual([above.kind, above.text], ['same', '        "ref": "{{vars.branch}}",'])
})

test('a member added to a list is that member, not the brace above it and half of the next', () => {
  const list = (...names: string[]) => JSON.stringify({ steps: names.map(name => ({ type: 'delegate', target: name })) })
  const middle = ok(compareTexts(list('a', 'b'), list('a', 'c', 'b'), 'json'))
  assert.deepEqual(changed(middle.rows), ['+    {', '+      "type": "delegate",', '+      "target": "c"', '+    },'])
  const last = ok(compareTexts(list('a', 'b'), list('a', 'b', 'c'), 'json'))
  assert.deepEqual(changed(last.rows), ['+    {', '+      "type": "delegate",', '+      "target": "c"', '+    }'])
  const gone = ok(compareTexts(list('a', 'c', 'b'), list('a', 'b'), 'json'))
  assert.deepEqual(changed(gone.rows), ['-    {', '-      "type": "delegate",', '-      "target": "c"', '-    },'])

  // In prose the same choice keeps an added paragraph whole, with the blank line after it.
  const prose = ok(compareTexts('# One\n\nfirst\n\n# Two\n\nsecond\n', '# One\n\nfirst\n\n# New\n\nadded\n\n# Two\n\nsecond\n', 'text'))
  assert.deepEqual(changed(prose.rows), ['+# New', '+', '+added', '+'])
})

test('steps wrapped in a new block show only the wrapper as changed, drawn with the newer indentation', () => {
  const [, v2, v3] = RELEASE_VERSIONS
  const result = ok(compareTexts(v2, v3, 'json'))
  assert.deepEqual(changed(result.rows), [
    '+    {',
    '+      "type": "loop",',
    '+      "maxIterations": 3,',
    '+      "steps": [',
    '+      ]',
    '+    },',
  ])
  // The moved lines are unchanged, and shown where they now stand.
  const moved = result.rows.find(r => r.text.includes('"signalName": "review-verdict"'))!
  assert.equal(moved.kind, 'same')
  assert.equal(moved.text, '          "signalName": "review-verdict",')
  assert.equal(moved.indent, 10)
  assert.equal(moved.oldNo! < moved.newNo!, true)

  // The other way round the wrapper is removed, and a removed line keeps the older side's indentation.
  const unwrapped = ok(compareTexts(v3, v2, 'json'))
  assert.deepEqual(changed(unwrapped.rows).slice(0, 2), ['-    {', '-      "type": "loop",'])
  assert.equal(unwrapped.rows.find(r => r.text.includes('"signalName": "review-verdict"'))!.text, '      "signalName": "review-verdict",')

  // As plain text the same pair is a change on every shifted line.
  const asText = ok(compareTexts(prettyJson(v2)!, prettyJson(v3)!, 'text'))
  assert.ok(asText.added > result.added + 10)
})

test('JSON that differs only in spacing has no changed line, and says so', () => {
  const result = ok(compareTexts('{"a":1,"b":[1,2]}', '{\n    "a": 1,\n    "b": [1, 2]\n}', 'json'))
  assert.deepEqual([result.added, result.removed, result.onlySpacing], [0, 0, true])
})

test('a side that is not JSON makes it a comparison of the raw texts, and names the side', () => {
  const [v1] = RELEASE_VERSIONS
  const broken = v1.slice(0, -1)
  const result = ok(compareTexts(v1, broken, 'json'))
  assert.equal(result.layout, 'text')
  assert.deepEqual(result.notJson, ['newer'])
  assert.deepEqual(marked(result.rows), [`-${v1}`, `+${broken}`])

  assert.deepEqual(ok(compareTexts('{ nope', '[ nope', 'json')).notJson, ['older', 'newer'])
  // Against nothing there is only one side to read.
  const first = ok(compareTexts(null, v1, 'json'))
  assert.equal(first.layout, 'json')
  assert.equal(first.rows.every(r => r.kind === 'add'), true)
  assert.equal(first.rows.length, prettyJson(v1)!.split('\n').length)
})

// ── Size ────────────────────────────────────────────────────────────────────────────────────────

test('two versions of a 240,000-character one-line definition compare in well under a second', () => {
  const older = largeDefinition(), newer = largeDefinition(true)
  for (const text of [older, newer]) {
    assert.equal(text.includes('\n'), false)
    assert.ok(text.length > 230_000 && text.length < 260_000, `${text.length} characters`)
    const laidOut = prettyJson(text)!.split('\n').length
    assert.ok(laidOut >= 5000 && laidOut <= 8000, `${laidOut} lines`)
  }
  // Once to warm the code up, as a page that has already drawn has; then the run that is timed.
  compareTexts(older, newer, 'json')
  const start = performance.now()
  const result = ok(compareTexts(older, newer, 'json'))
  const took = performance.now() - start
  assert.ok(took < 1000, `took ${took.toFixed(0)} ms`)

  assert.equal(result.layout, 'json')
  assert.ok(result.rows.length > 5000)
  // Line-level changes: the handful of edits, not "everything removed, everything added".
  assert.ok(result.added > 5 && result.added < 60, `${result.added} added`)
  assert.ok(result.removed > 5 && result.removed < 60, `${result.removed} removed`)
  assert.deepEqual(changed(result.rows).slice(0, 2), ['-      "timeoutMinutes": 30,', '+      "timeoutMinutes": 240,'])
  const reworded = result.rows.find(r => r.kind === 'add' && r.parts && r.text.includes('Step 120:'))!
  assert.deepEqual(reworded.parts!.filter(p => p.changed).map(p => p.text), ['every'])
  // The steps that moved into the loop are not changes.
  assert.equal(result.rows.find(r => r.text.includes('"variable": "result202"'))!.kind, 'same')
  // What is drawn is the changes with their context, not eight thousand lines.
  const shown = layOut(result).reduce((count, s) => count + (s.kind === 'rows' ? s.end - s.start : 1), 0)
  assert.ok(shown < 200, `${shown} rows drawn`)
  console.log(`      large definition: ${older.length} characters, ${result.rows.length} rows, +${result.added} -${result.removed}, ${took.toFixed(1)} ms`)
})

test('a text past the size limit, or two texts too different to finish, is refused and not attempted', () => {
  const huge = 'x'.repeat(MAX_COMPARE_CHARS + 1)
  assert.deepEqual(compareTexts(huge, 'small', 'text'), { ok: false, reason: 'too-large' })
  assert.deepEqual(compareTexts('small', huge, 'json'), { ok: false, reason: 'too-large' })
  assert.deepEqual(compareTexts(null, huge, 'text'), { ok: false, reason: 'too-large' })

  // 30,000 lines a side with nothing in common: the search would take minutes; it stops at its limit.
  const unrelated = (word: string) => Array.from({ length: 30_000 }, (_, i) => `${word} ${i}`).join('\n')
  const start = performance.now()
  assert.deepEqual(compareTexts(unrelated('left'), unrelated('right'), 'text'), { ok: false, reason: 'too-large' })
  const took = performance.now() - start
  assert.ok(took < 3000, `took ${took.toFixed(0)} ms`)

  // A long text with few changes is not refused for its length.
  const long = (changes: Record<number, string>) => numbered(30_000, changes)
  const result = ok(compareTexts(long({}), long({ 100: 'x', 29_000: 'y' }), 'text'))
  assert.deepEqual([result.added, result.removed], [2, 2])
})
