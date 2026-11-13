import { memo } from 'react'
import ReactMarkdown, { type Components } from 'react-markdown'
import { statusLabel, type Entry, type SubmissionStatus } from '../chatTranscript'
import type { PendingSend } from '../chatConnection'

/**
 * Links in an agent's reply open in a new tab and give that tab no handle back to this one.
 *
 * Images are shown as links, never loaded: an agent's reply can carry any URL, and loading it would
 * tell that server who is reading and when.
 *
 * Raw HTML is never rendered: react-markdown escapes it to text unless a plugin is added, and none
 * is. Its default URL filter empties `javascript:` and other unsafe URLs; such a link keeps its
 * words and loses the link.
 */
const MARKDOWN_COMPONENTS: Components = {
  a: ({ node: _node, href, children, ...props }) => (href
    ? <a {...props} href={href} target="_blank" rel="noopener noreferrer">{children}</a>
    : <span>{children}</span>),
  img: ({ src, alt }) => (src
    ? <a href={src} target="_blank" rel="noopener noreferrer">{alt || 'image'}</a>
    : <>{alt}</>),
}

/** An agent's reply, as Markdown. */
export function AgentMarkdown({ text }: { text: string }) {
  return <ReactMarkdown components={MARKDOWN_COMPONENTS}>{text}</ReactMarkdown>
}

const EntryRow = memo(function EntryRow({ entry, label }: { entry: Entry; label: string | null }) {
  if (entry.type === 'you') {
    return (
      <div className="chat-row chat-row--you">
        <div className="chat-bubble chat-bubble--you">
          <span className="chat-sr-only">You: </span>{entry.text}
        </div>
        {label && <div className="chat-meta">{label}</div>}
      </div>
    )
  }
  if (entry.type === 'agent') {
    const empty = entry.text.trim() === ''
    return (
      <div className="chat-row chat-row--agent">
        {!empty && (
          <div className="chat-bubble chat-bubble--agent chat-markdown">
            <span className="chat-sr-only">Agent: </span>
            <AgentMarkdown text={entry.text} />
          </div>
        )}
        {entry.note && <div className="chat-meta">{entry.note}</div>}
      </div>
    )
  }
  return <div className={`chat-system chat-system--${entry.tone}`}>{entry.text}</div>
})

const PendingRow = memo(function PendingRow({ pending, onRetry, onDiscard }: {
  pending: PendingSend
  onRetry: (submissionId: string) => void
  onDiscard: (submissionId: string) => void
}) {
  return (
    <div className="chat-row chat-row--you">
      <div className={`chat-bubble chat-bubble--you${pending.status === 'failed' ? ' chat-bubble--failed' : ''}`}>
        <span className="chat-sr-only">You: </span>{pending.text}
      </div>
      {pending.status === 'sending' && <div className="chat-meta">Sending…</div>}
      {pending.status === 'sent' && <div className="chat-meta">Sent</div>}
      {pending.status === 'failed' && (
        <div className="chat-meta chat-meta--error" role="alert">
          {pending.problem}
          {pending.canRetry && (
            <button type="button" className="chat-link-btn" onClick={() => onRetry(pending.submissionId)}>Retry</button>
          )}
          <button type="button" className="chat-link-btn" onClick={() => onDiscard(pending.submissionId)}>Discard</button>
        </div>
      )}
    </div>
  )
})

/**
 * The thread: what the conversation holds, in order, then what this page sent that the
 * conversation does not show yet. The person's own words are plain text; only the agent's replies
 * are read as Markdown.
 */
export default function ChatTranscript({ entries, status, pending, onRetry, onDiscard }: {
  entries: Entry[]
  status: Record<string, SubmissionStatus>
  pending: PendingSend[]
  onRetry: (submissionId: string) => void
  onDiscard: (submissionId: string) => void
}) {
  return (
    <>
      {entries.map(entry => (
        <EntryRow key={entry.key} entry={entry}
          label={entry.type === 'you' ? statusLabel(status[entry.submissionId]) : null} />
      ))}
      {pending.map(p => (
        <PendingRow key={`pending:${p.submissionId}`} pending={p} onRetry={onRetry} onDiscard={onDiscard} />
      ))}
    </>
  )
}
