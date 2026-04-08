import { useState, useEffect } from 'react'

interface KVRow { key: string; value: string }

interface KeyValueEditorProps {
  rows: KVRow[]
  keyPlaceholder?: string
  valuePlaceholder?: string
  onChange: (rows: KVRow[]) => void
}

export default function KeyValueEditor({ rows, keyPlaceholder = 'key', valuePlaceholder = 'value', onChange }: KeyValueEditorProps) {
  const [localRows, setLocalRows] = useState<KVRow[]>(rows)

  // Sync from parent when external data changes (e.g. step switch)
  useEffect(() => { setLocalRows(rows) }, [JSON.stringify(rows)])

  function emit(updated: KVRow[]) {
    onChange(updated.filter(r => r.key.trim() !== ''))
  }

  function update(i: number, field: 'key' | 'value', val: string) {
    const next = localRows.map((r, idx) => idx === i ? { ...r, [field]: val } : r)
    setLocalRows(next)
    // Emit value changes immediately; key changes emit on blur
    if (field === 'value') emit(next)
  }

  function handleKeyBlur() { emit(localRows) }

  function remove(i: number) {
    const next = localRows.filter((_, idx) => idx !== i)
    setLocalRows(next)
    emit(next)
  }

  function add() {
    setLocalRows(prev => [...prev, { key: '', value: '' }])
    // Don't emit — empty key row is local-only until user types a key
  }

  return (
    <div className="wfe-kv-editor">
      {localRows.map((row, i) => (
        <div key={i} className="wfe-kv-row">
          <input
            className="config-input wfe-kv-key"
            placeholder={keyPlaceholder}
            value={row.key}
            onChange={e => update(i, 'key', e.target.value)}
            onBlur={handleKeyBlur}
          />
          <input
            className="config-input wfe-kv-val"
            placeholder={valuePlaceholder}
            value={row.value}
            onChange={e => update(i, 'value', e.target.value)}
          />
          <button className="wfe-kv-remove" onClick={() => remove(i)}>✕</button>
        </div>
      ))}
      <button className="wfe-add-btn" onClick={add}>+ Add row</button>
    </div>
  )
}
