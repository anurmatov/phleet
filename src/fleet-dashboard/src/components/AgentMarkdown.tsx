import { isValidElement, type ReactNode } from 'react'
import ReactMarkdown, { type Components } from 'react-markdown'
import CopyButton from './CopyButton'

/**
 * Links in agent-written text open in a new tab and give that tab no handle back to this one.
 *
 * Images are shown as links, never loaded: an agent's reply or a stored memory can carry any URL,
 * and loading it would tell that server who is reading and when.
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

/** The text inside rendered Markdown: what a person would select and copy. */
function nodeText(node: ReactNode): string {
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(nodeText).join('')
  if (isValidElement<{ children?: ReactNode }>(node)) return nodeText(node.props.children)
  return ''
}

/** The same, with a copy button on each block of code. */
const WITH_CODE_COPY: Components = {
  ...MARKDOWN_COMPONENTS,
  pre: ({ node: _node, children, ...props }) => (
    <div className="md-code">
      <CopyButton text={nodeText(children).replace(/\n$/, '')} label="Copy code" className="md-code-copy" />
      <pre {...props}>{children}</pre>
    </div>
  ),
}

/**
 * Text an agent wrote, as Markdown. The one place this page turns Markdown into elements: chat
 * replies and memory content both go through it, so neither can load what the other refuses to
 * (`agentMarkdown.render.test.ts` fails if another file imports react-markdown).
 */
export default function AgentMarkdown({ text, copyCode = false }: { text: string; copyCode?: boolean }) {
  return <ReactMarkdown components={copyCode ? WITH_CODE_COPY : MARKDOWN_COMPONENTS}>{text}</ReactMarkdown>
}
