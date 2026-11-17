// What changed between two versions of a text: the lines, folded to the changes with a little
// context, and the words inside each change. No React and no DOM, so `node --test` can run it.

// ── Limits ──────────────────────────────────────────────────────────────────────────────────────

/** Unchanged lines kept on each side of a change. */
export const CONTEXT_LINES = 3
/** How many more lines one press on a folded region reveals. */
export const EXPAND_STEP = 20
/** A side longer than this is not compared at all. A real workflow definition is about 240,000. */
export const MAX_COMPARE_CHARS = 2_000_000
/**
 * The steps the line comparison may take before it gives up. The search costs about the square of
 * the number of differing lines, so this is what stops two unrelated 20,000-line texts from holding
 * the page: 60 million steps is a few hundred milliseconds on a desk and about a second on a phone.
 */
export const LINE_WORK_LIMIT = 60_000_000
/** The same, for the words inside one change. Past it the change keeps its lines without word marks. */
export const WORD_WORK_LIMIT = 2_000_000
/** A change with more word tokens than this is not compared by words. */
export const WORD_TOKEN_LIMIT = 6_000

// ── The sequence comparison ─────────────────────────────────────────────────────────────────────

const EXCEEDED = Symbol('work limit exceeded')

export interface SequenceMarks {
  /** 1 where the element of the older sequence has no partner in the newer one. */
  removed: Uint8Array
  /** 1 where the element of the newer sequence has no partner in the older one. */
  added: Uint8Array
}

/**
 * The shortest edit script between two sequences of ids, as marks on each: Myers' O(ND) search
 * (E. Myers, "An O(ND) Difference Algorithm and Its Variations", 1986) in its linear-space form,
 * which finds the middle of the script from both ends and then solves each half. Memory is two
 * rows of integers, never a table. Null when it would take more than `workLimit` steps.
 */
export function diffSequences(older: Int32Array, newer: Int32Array, workLimit: number = LINE_WORK_LIMIT): SequenceMarks | null {
  const marks: SequenceMarks = { removed: new Uint8Array(older.length), added: new Uint8Array(newer.length) }
  const budget = { left: workLimit }
  try {
    solve(older, 0, older.length, newer, 0, newer.length, marks, budget)
  } catch (e) {
    if (e === EXCEEDED) return null
    throw e
  }
  return marks
}

function solve(
  a: Int32Array, aLo: number, aHi: number, b: Int32Array, bLo: number, bHi: number,
  marks: SequenceMarks, budget: { left: number },
): void {
  // What the two ranges start and end with in common is not part of any change.
  while (aLo < aHi && bLo < bHi && a[aLo] === b[bLo]) { aLo++; bLo++ }
  while (aLo < aHi && bLo < bHi && a[aHi - 1] === b[bHi - 1]) { aHi--; bHi-- }
  if (aLo === aHi) { marks.added.fill(1, bLo, bHi); return }
  if (bLo === bHi) { marks.removed.fill(1, aLo, aHi); return }

  const n = aHi - aLo, m = bHi - bLo
  const max = (n + m + 1) >> 1
  const offset = max
  const size = 2 * max + 2
  // The furthest x reached on each diagonal k = x - y, from the start and from the end.
  const forward = new Int32Array(size).fill(-1)
  const backward = new Int32Array(size).fill(-1)
  forward[offset + 1] = 0
  backward[offset + 1] = 0
  const delta = n - m
  // With an odd delta the two searches can only meet on a forward step, with an even one on a backward step.
  const odd = (delta & 1) !== 0
  // Diagonals that have run off an edge of the grid are left out of later rounds.
  let fTrimLo = 0, fTrimHi = 0, bTrimLo = 0, bTrimHi = 0

  for (let d = 0; d < max; d++) {
    budget.left -= 2 * (d + 1)
    if (budget.left < 0) throw EXCEEDED

    for (let k = -d + fTrimLo; k <= d - fTrimHi; k += 2) {
      const ki = offset + k
      let x = k === -d || (k !== d && forward[ki - 1] < forward[ki + 1]) ? forward[ki + 1] : forward[ki - 1] + 1
      let y = x - k
      const from = x
      while (x < n && y < m && a[aLo + x] === b[bLo + y]) { x++; y++ }
      budget.left -= x - from
      forward[ki] = x
      if (x > n) fTrimHi += 2
      else if (y > m) fTrimLo += 2
      else if (odd) {
        const mirror = offset + delta - k
        if (mirror >= 0 && mirror < size && backward[mirror] !== -1 && x >= n - backward[mirror]) {
          solve(a, aLo, aLo + x, b, bLo, bLo + y, marks, budget)
          solve(a, aLo + x, aHi, b, bLo + y, bHi, marks, budget)
          return
        }
      }
    }

    for (let k = -d + bTrimLo; k <= d - bTrimHi; k += 2) {
      const ki = offset + k
      let x = k === -d || (k !== d && backward[ki - 1] < backward[ki + 1]) ? backward[ki + 1] : backward[ki - 1] + 1
      let y = x - k
      const from = x
      while (x < n && y < m && a[aHi - 1 - x] === b[bHi - 1 - y]) { x++; y++ }
      budget.left -= x - from
      backward[ki] = x
      if (x > n) bTrimHi += 2
      else if (y > m) bTrimLo += 2
      else if (!odd) {
        const mirror = offset + delta - k
        if (mirror >= 0 && mirror < size && forward[mirror] !== -1) {
          const fx = forward[mirror]
          const fy = offset + fx - mirror
          if (fx >= n - x) {
            solve(a, aLo, aLo + fx, b, bLo, bLo + fy, marks, budget)
            solve(a, aLo + fx, aHi, b, bLo + fy, bHi, marks, budget)
            return
          }
        }
      }
    }
  }

  // The searches never met: the two ranges share nothing.
  marks.removed.fill(1, aLo, aHi)
  marks.added.fill(1, bLo, bHi)
}

/** Gives each distinct string one integer, so sequences of strings compare as integers. */
function interner(): (text: string) => number {
  const ids = new Map<string, number>()
  return text => {
    let id = ids.get(text)
    if (id === undefined) { id = ids.size; ids.set(text, id) }
    return id
  }
}

// ── Text into lines ─────────────────────────────────────────────────────────────────────────────

/**
 * The lines of a text. A line ends at LF or CRLF, and the newline that ends the last line does
 * not start another, so "a\n" and "a" are the same one line and an empty text has none.
 */
export function splitLines(text: string): string[] {
  if (text === '') return []
  const lines = text.split(/\r?\n/)
  if (lines[lines.length - 1] === '') lines.pop()
  return lines
}

/**
 * JSON laid out one member per line with two-space indentation, or null when the text is not
 * JSON. The text is re-indented, not re-serialised: every key, string and number stays exactly as
 * written and in its place (a parse and print would reorder number-like keys and round long
 * numbers, and so hide or invent differences).
 */
export function prettyJson(text: string): string | null {
  try { JSON.parse(text) } catch { return null }
  let out = ''
  let depth = 0
  const newline = () => { out += '\n' + '  '.repeat(depth) }
  for (let i = 0; i < text.length; i++) {
    const c = text[i]
    if (c === '"') {
      let end = i + 1
      while (text[end] !== '"') end += text[end] === '\\' ? 2 : 1
      out += text.slice(i, end + 1)
      i = end
    } else if (c === '{' || c === '[') {
      let next = i + 1
      while (isJsonSpace(text[next])) next++
      // An empty object or array stays on its line.
      if (text[next] === (c === '{' ? '}' : ']')) { out += c + text[next]; i = next }
      else { out += c; depth++; newline() }
    } else if (c === '}' || c === ']') { depth--; newline(); out += c }
    else if (c === ',') { out += c; newline() }
    else if (c === ':') out += ': '
    else if (!isJsonSpace(c)) out += c
  }
  return out
}

function isJsonSpace(c: string | undefined): boolean {
  return c === ' ' || c === '\n' || c === '\r' || c === '\t'
}

// ── The comparison of two versions ──────────────────────────────────────────────────────────────

export type CompareFormat = 'text' | 'json'
export type RowKind = 'same' | 'add' | 'remove'

/** A run of characters in a changed line: `changed` when the other side has no such words there. */
export interface DiffPart { text: string; changed: boolean }

export interface DiffRow {
  kind: RowKind
  /** The line as shown: the newer side's, and the older side's for a removed line. */
  text: string
  /** The line's number on each side, from 1; null on the side that does not have it. */
  oldNo: number | null
  newNo: number | null
  /** How many characters of `text` are its leading spaces and tabs. */
  indent: number
  /** The text after the indentation, cut where words changed. Only on a line of a change that was compared by words. */
  parts?: DiffPart[]
}

/** A run of removed and added rows with nothing unchanged between them: rows `start` up to `end`. */
export interface ChangeBlock { start: number; end: number }

export interface Comparison {
  ok: true
  /** Every line of both sides in order: unchanged lines once, a change as its removed lines then its added ones. */
  rows: DiffRow[]
  blocks: ChangeBlock[]
  added: number
  removed: number
  /** `json` when both sides were laid out as JSON and compared without indentation and separating commas. */
  layout: CompareFormat
  /** JSON was asked for and these sides are not JSON, so the texts were compared as they are. */
  notJson: ('older' | 'newer')[]
  /** No line differs, yet the two texts are not the same characters: line endings, the final newline, or JSON spacing. */
  onlySpacing: boolean
}

export interface TooLarge { ok: false; reason: 'too-large' }

export type CompareResult = Comparison | TooLarge

/**
 * Compares `older` with `newer`. `older` is null when there is nothing to compare with (the first
 * version), and then every line is added. For `json` both sides are laid out with {@link prettyJson}
 * and lines are matched without their indentation or separating comma, so a subtree that moved
 * one level in or out is unchanged; if a side is not JSON, the two texts are compared as plain
 * text instead.
 */
export function compareTexts(older: string | null, newer: string, format: CompareFormat): CompareResult {
  if ((older?.length ?? 0) > MAX_COMPARE_CHARS || newer.length > MAX_COMPARE_CHARS) return { ok: false, reason: 'too-large' }

  let oldText = older ?? ''
  let newText = newer
  let layout: CompareFormat = 'text'
  const notJson: ('older' | 'newer')[] = []
  if (format === 'json') {
    const oldJson = older === null ? '' : prettyJson(older)
    const newJson = prettyJson(newer)
    if (oldJson === null) notJson.push('older')
    if (newJson === null) notJson.push('newer')
    if (oldJson !== null && newJson !== null) { oldText = oldJson; newText = newJson; layout = 'json' }
  }

  const oldLines = splitLines(oldText)
  const newLines = splitLines(newText)
  const intern = interner()
  const key = layout === 'json' ? (line: string) => intern(jsonLineKey(line)) : (line: string) => intern(line)
  const oldKeys = Int32Array.from(oldLines, key)
  const newKeys = Int32Array.from(newLines, key)
  const marks = diffSequences(oldKeys, newKeys)
  if (!marks) return { ok: false, reason: 'too-large' }
  settleLines(oldLines, oldKeys, newLines, newKeys, marks)

  const rows: DiffRow[] = []
  const blocks: ChangeBlock[] = []
  let added = 0, removed = 0
  let i = 0, j = 0
  while (i < oldLines.length || j < newLines.length) {
    if (i < oldLines.length && j < newLines.length && !marks.removed[i] && !marks.added[j]) {
      rows.push(row('same', newLines[j], i + 1, j + 1))
      i++; j++
      continue
    }
    const start = rows.length
    while (i < oldLines.length && marks.removed[i]) { rows.push(row('remove', oldLines[i], i + 1, null)); i++; removed++ }
    const firstAdded = rows.length
    while (j < newLines.length && marks.added[j]) { rows.push(row('add', newLines[j], null, j + 1)); j++; added++ }
    blocks.push({ start, end: rows.length })
    if (firstAdded > start && rows.length > firstAdded) markWords(rows, start, firstAdded, rows.length)
  }

  return {
    ok: true, rows, blocks, added, removed, layout, notJson,
    onlySpacing: added === 0 && removed === 0 && older !== null && older !== newer,
  }
}

function indentOf(text: string): number {
  let indent = 0
  while (text[indent] === ' ' || text[indent] === '\t') indent++
  return indent
}

function row(kind: RowKind, text: string, oldNo: number | null, newNo: number | null): DiffRow {
  return { kind, text, oldNo, newNo, indent: indentOf(text) }
}

/**
 * What a line of laid-out JSON is compared by: the line without its indentation and without the
 * comma that separates it from the next member. Neither says anything about the member itself:
 * the indentation is its depth and the comma is whether another member follows, so a step that
 * moved into a new block, or became the last of its list, is still the same line.
 */
function jsonLineKey(line: string): string {
  const text = line.trimStart()
  return text.endsWith(',') ? text.slice(0, -1) : text
}

/**
 * Chooses where a run of added (or removed) lines stands when the comparison could as well have
 * put it a few lines up or down: `{ a }, { b }` grown by `{ c }` can be read as `{ c },` added or
 * as `}, { c` added, and both are shortest scripts. Of the places it can stand, a run takes the
 * one that begins at the shallowest indentation, and the lowest of those. Only a run with nothing
 * removed (or added) beside it moves, and never into its neighbour.
 */
function settleLines(oldLines: string[], oldKeys: Int32Array, newLines: string[], newKeys: Int32Array, marks: SequenceMarks): void {
  // The changes in order: the removed lines [i0, i1) and the added lines [j0, j1) between two unchanged stretches.
  const changes: { i0: number; i1: number; j0: number; j1: number }[] = []
  let i = 0, j = 0
  while (i < oldLines.length || j < newLines.length) {
    if (i < oldLines.length && j < newLines.length && !marks.removed[i] && !marks.added[j]) { i++; j++; continue }
    const i0 = i, j0 = j
    while (i < oldLines.length && marks.removed[i]) i++
    while (j < newLines.length && marks.added[j]) j++
    changes.push({ i0, i1: i, j0, j1: j })
  }

  // How far the previous run moved down: that many unchanged lines above this one are taken.
  let taken = 0
  changes.forEach((change, c) => {
    const adding = change.i0 === change.i1
    if (!adding && change.j0 !== change.j1) { taken = 0; return }
    const lines = adding ? newLines : oldLines
    const keys = adding ? newKeys : oldKeys
    const marked = adding ? marks.added : marks.removed
    const start = adding ? change.j0 : change.i0
    const end = adding ? change.j1 : change.i1
    const previous = changes[c - 1], next = changes[c + 1]
    // Unchanged lines above and below. One is left standing where another change follows.
    const above = previous ? start - (adding ? previous.j1 : previous.i1) - taken - 1 : start
    const below = next ? (adding ? next.j0 : next.i0) - end - 1 : lines.length - end
    let up = 0
    while (up < above && keys[start - up - 1] === keys[end - up - 1]) up++
    let down = 0
    while (down < below && keys[start + down] === keys[end + down]) down++

    let best = -up
    for (let shift = -up; shift <= down; shift++) {
      if (indentOf(lines[start + shift]) <= indentOf(lines[start + best])) best = shift
    }
    if (best !== 0) {
      marked.fill(0, start, end)
      marked.fill(1, start + best, end + best)
    }
    taken = Math.max(best, 0)
  })
}

// ── The words inside a change ───────────────────────────────────────────────────────────────────

// A word, a run of spaces, or one other character.
const TOKEN = /[\p{L}\p{N}_]+|\s+|[^\s\p{L}\p{N}_]/gu
const WORD = /^[\p{L}\p{N}_]/u
const NEWLINE = '\n'

interface BlockTokens { texts: string[]; rowOf: number[] }

function tokensOf(rows: DiffRow[], start: number, end: number): BlockTokens {
  const texts: string[] = []
  const rowOf: number[] = []
  for (let r = start; r < end; r++) {
    if (r > start) { texts.push(NEWLINE); rowOf.push(-1) }
    for (const token of rows[r].text.slice(rows[r].indent).match(TOKEN) ?? []) { texts.push(token); rowOf.push(r) }
  }
  return { texts, rowOf }
}

/**
 * Marks the changed words of one change: its removed rows (`start` up to `middle`) against its
 * added rows (`middle` up to `end`), each side taken as one text, so nothing depends on which
 * line stands opposite which. Leaves the rows unmarked when the change is too large to be worth
 * it, or when the two sides share too few words for the marks to mean anything.
 */
function markWords(rows: DiffRow[], start: number, middle: number, end: number): void {
  const before = tokensOf(rows, start, middle)
  const after = tokensOf(rows, middle, end)
  if (before.texts.length + after.texts.length > WORD_TOKEN_LIMIT) return
  const intern = interner()
  const beforeIds = Int32Array.from(before.texts, text => intern(text))
  const afterIds = Int32Array.from(after.texts, text => intern(text))
  const marks = diffSequences(beforeIds, afterIds, WORD_WORK_LIMIT)
  if (!marks) return
  settleWords(beforeIds, marks.removed)
  settleWords(afterIds, marks.added)

  const words = (side: BlockTokens) => side.texts.reduce((count, text) => count + (WORD.test(text) ? 1 : 0), 0)
  const kept = before.texts.reduce((count, text, t) => count + (WORD.test(text) && !marks.removed[t] ? 1 : 0), 0)
  // Two sides that share less than half of the shorter one's words are different text, not an edit.
  const fewest = Math.min(words(before), words(after))
  if (fewest === 0 || kept * 2 < fewest) return

  applyParts(rows, before, marks.removed)
  applyParts(rows, after, marks.added)
}

/**
 * Moves each run of changed tokens as far right as it can go: `a[ b] c` and `a [b ]c` are the
 * same edit, and the second marks a word with the space after it, which is how an inserted word
 * reads. It also keeps a full stop with the sentence it ended rather than with one added after it.
 */
function settleWords(ids: Int32Array, changed: Uint8Array): void {
  let start = 0
  while (start < ids.length) {
    if (!changed[start]) { start++; continue }
    let end = start
    while (end < ids.length && changed[end]) end++
    while (end < ids.length && ids[start] === ids[end]) {
      changed[start++] = 0
      changed[end++] = 1
      while (end < ids.length && changed[end]) end++
    }
    start = end
  }
}

function applyParts(rows: DiffRow[], side: BlockTokens, changed: Uint8Array): void {
  let t = 0
  while (t < side.texts.length) {
    const r = side.rowOf[t]
    if (r === -1) { t++; continue }
    const parts: DiffPart[] = []
    let words = 0, keptWords = 0
    for (; t < side.texts.length && side.rowOf[t] === r; t++) {
      const text = side.texts[t]
      const isChanged = changed[t] === 1
      if (WORD.test(text)) { words++; if (!isChanged) keptWords++ }
      parts.push({ text, changed: isChanged })
    }
    // A line that is new from end to end is marked by its colour alone; so is one that kept
    // only a stray word.
    if (keptWords === 0 || keptWords * 3 < words) continue
    if (parts.some(p => p.changed)) rows[r].parts = joinParts(parts)
  }
}

/** Joins neighbours of one kind, and takes the space between two changed words into the change. */
function joinParts(parts: DiffPart[]): DiffPart[] {
  const out: DiffPart[] = []
  parts.forEach((part, p) => {
    const bridges = !part.changed && /^\s+$/.test(part.text) && parts[p - 1]?.changed === true && parts[p + 1]?.changed === true
    const changed = part.changed || bridges
    const last = out[out.length - 1]
    if (last && last.changed === changed) last.text += part.text
    else out.push({ text: part.text, changed })
  })
  return out
}

// ── What is shown: the changes, each with its context, and the folded rest ──────────────────────

/** How far a folded region has been opened: lines shown at its top and at its bottom. */
export interface Expansion { head: number; tail: number }

export type Section =
  /** Rows `start` up to `end`, shown. */
  | { kind: 'rows'; start: number; end: number }
  /**
   * Unchanged rows `start` up to `end`, hidden. `id` names the whole unchanged region this is
   * left of, and stays the same as it is opened. `canHead` when a change stands above it, so more
   * of its top can be shown as that change's context; `canTail` the same below.
   */
  | { kind: 'fold'; id: number; start: number; end: number; canHead: boolean; canTail: boolean }

/** A fold that would hide a single line is not worth its own line. */
const MIN_FOLD = 2

/**
 * Lays a comparison out as shown: every change with `context` unchanged lines around it, changes
 * whose contexts touch run together, and every other stretch of unchanged lines folded. `opened`
 * says how much of each fold has been revealed, by fold id.
 */
export function layOut(
  comparison: Pick<Comparison, 'rows' | 'blocks'>,
  opened: Readonly<Record<number, Expansion>> = {},
  context: number = CONTEXT_LINES,
): Section[] {
  const total = comparison.rows.length
  // The shown ranges: each change with its context, merged where the gap left between two is too small to fold.
  const shown: { start: number; end: number }[] = []
  for (const block of comparison.blocks) {
    const start = Math.max(0, block.start - context)
    const end = Math.min(total, block.end + context)
    const last = shown[shown.length - 1]
    if (last && start - last.end < MIN_FOLD) last.end = Math.max(last.end, end)
    else shown.push({ start: start < MIN_FOLD ? 0 : start, end })
  }
  const lastShown = shown[shown.length - 1]
  if (lastShown && total - lastShown.end < MIN_FOLD) lastShown.end = total

  const sections: Section[] = []
  const rows = (start: number, end: number) => {
    if (end <= start) return
    const last = sections[sections.length - 1]
    if (last?.kind === 'rows' && last.end === start) last.end = end
    else sections.push({ kind: 'rows', start, end })
  }
  let id = 0
  const fold = (start: number, end: number, canHead: boolean, canTail: boolean) => {
    if (end <= start) return
    const size = end - start
    const open = opened[id] ?? { head: 0, tail: 0 }
    const head = canHead ? Math.min(open.head, size) : 0
    const tail = canTail ? Math.min(open.tail, size - head) : 0
    if (size - head - tail < MIN_FOLD || (!canHead && !canTail && open.head + open.tail > 0)) rows(start, end)
    else {
      rows(start, start + head)
      sections.push({ kind: 'fold', id, start: start + head, end: end - tail, canHead, canTail })
      rows(end - tail, end)
    }
    id++
  }

  let at = 0
  shown.forEach((range, s) => {
    fold(at, range.start, s > 0, true)
    rows(range.start, range.end)
    at = range.end
  })
  fold(at, total, shown.length > 0, false)
  return sections
}

/** The fold opened by `step` more lines at its top (`head`), at its bottom (`tail`), or entirely. */
export function expand(opened: Readonly<Record<number, Expansion>>, id: number, where: 'head' | 'tail' | 'all', step: number = EXPAND_STEP): Record<number, Expansion> {
  const now = opened[id] ?? { head: 0, tail: 0 }
  const next = where === 'all'
    ? { head: Number.MAX_SAFE_INTEGER, tail: Number.MAX_SAFE_INTEGER }
    : { ...now, [where]: now[where] + step }
  return { ...opened, [id]: next }
}
