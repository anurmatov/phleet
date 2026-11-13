import ReactMarkdown, { type Components } from 'react-markdown'

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

/**
 * Text an agent wrote, as Markdown. The one place this page turns Markdown into elements: chat
 * replies and memory content both go through it, so neither can load what the other refuses to
 * (`agentMarkdown.render.test.ts` fails if another file imports react-markdown).
 */
export default function AgentMarkdown({ text }: { text: string }) {
  return <ReactMarkdown components={MARKDOWN_COMPONENTS}>{text}</ReactMarkdown>
}
