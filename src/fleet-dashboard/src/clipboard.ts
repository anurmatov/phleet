// Putting text on the clipboard, on a dashboard that is often plain HTTP.
//
// `navigator.clipboard` exists only in a secure context. On a LAN address without TLS it is
// missing, so this falls back to the old way: a hidden text box, selected, and the browser's own
// copy command. Answers whether the text went, so a button never says "Copied" when nothing was.

export interface ClipboardDeps {
  clipboard: { writeText(text: string): Promise<void> } | undefined | null
  /** The old way. Answers whether the browser took it. */
  legacyCopy(text: string): boolean
}

export async function copyText(text: string, deps: ClipboardDeps): Promise<boolean> {
  if (deps.clipboard) {
    try {
      await deps.clipboard.writeText(text)
      return true
    } catch { /* refused: try the old way */ }
  }
  try { return deps.legacyCopy(text) } catch { return false }
}

/** The browser's side of {@link copyText}. */
export function browserClipboard(): ClipboardDeps {
  return {
    clipboard: typeof navigator !== 'undefined' ? navigator.clipboard : null,
    legacyCopy(text) {
      const box = document.createElement('textarea')
      box.value = text
      box.setAttribute('readonly', '')
      box.style.position = 'fixed'
      box.style.opacity = '0'
      document.body.appendChild(box)
      box.select()
      try { return document.execCommand('copy') } finally { box.remove() }
    },
  }
}
