import { useState } from 'react'
import type { AnyStep } from './editorTypes'
import { serializeStep } from './treeUtils'

interface JsonPreviewProps {
  root: AnyStep
}

export default function JsonPreview({ root }: JsonPreviewProps) {
  const [copied, setCopied] = useState(false)
  const json = serializeStep(root)

  function handleCopy() {
    navigator.clipboard.writeText(json).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className="wfe-json-preview">
      <div className="wfe-json-toolbar">
        <span className="wfe-json-label">JSON</span>
        <button className="wfe-json-copy" onClick={handleCopy}>
          {copied ? 'Copied!' : 'Copy'}
        </button>
      </div>
      <div className="wfe-json-body">
        <code dangerouslySetInnerHTML={{ __html: tokenize(json) }} />
      </div>
    </div>
  )
}

function tokenize(json: string): string {
  return json
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"(type)"\s*:/g, '<span class="jt-key jt-type-key">"$1"</span>:')
    .replace(/"([^"]+)"\s*:/g, '<span class="jt-key">"$1"</span>:')
    .replace(/:\s*"([^"]*)"/g, ': <span class="jt-string">"$1"</span>')
    .replace(/:\s*(\d+(\.\d+)?)\b/g, ': <span class="jt-number">$1</span>')
    .replace(/:\s*(true|false)\b/g, ': <span class="jt-bool">$1</span>')
    .replace(/:\s*(null)\b/g, ': <span class="jt-null">$1</span>')
}
