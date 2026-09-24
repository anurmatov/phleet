import type { ProjectCardState, ProjectContextMode, ProjectContextSummary } from '../types'
import {
  assignProject, cardSelectable, projectRows, setProjectMode, unassignProject,
  type ProjectAssignments, type ProjectRow,
} from '../projectAssignments'

interface ProjectAssignmentTableProps {
  contexts: ProjectContextSummary[]
  value: ProjectAssignments
  /** Card detail by lower-cased project name, where loaded — adds the missing-keeps warning. */
  cardStates: Record<string, ProjectCardState | null>
  onChange: (next: ProjectAssignments) => void
}

function CardCell({ row }: { row: ProjectRow }) {
  if (!row.hasContext) {
    return <span className="project-table-muted" title="No project context has this name.">no context</span>
  }
  if (row.cardVersion == null) {
    return <span className="project-table-muted" title="No card yet, so card mode is unavailable.">none</span>
  }
  return (
    <>
      v{row.cardVersion}
      {row.stale && (
        <span
          className="ctx-warn"
          title={`The card was written for ${row.staleBasedOn != null ? `full v${row.staleBasedOn}` : 'an older full version'}. It is still used; update it under Project Contexts.`}
        > ⚠ stale</span>
      )}
      {row.missingKeeps.length > 0 && (
        <span
          className="ctx-warn"
          title={`Missing keep markers: ${row.missingKeeps.join(', ')}. Provisioning renders the full context for this project until the card carries every keep marker of the current full context.`}
        > ⚠ missing keeps</span>
      )}
    </>
  )
}

/** The only edit surface for an agent's project assignments and their context modes. */
export default function ProjectAssignmentTable({ contexts, value, cardStates, onChange }: ProjectAssignmentTableProps) {
  const rows = projectRows(contexts, value, cardStates)
  if (rows.length === 0) {
    return <div className="config-hint-text">No project contexts yet — create one under Project Contexts.</div>
  }
  const anyWithoutCard = rows.some(r => r.cardVersion == null)
  return (
    <>
      <table className="project-table">
        <colgroup>
          <col className="project-table-col-assigned" />
          <col />
          <col className="project-table-col-context" />
          <col className="project-table-col-card" />
        </colgroup>
        <thead>
          <tr>
            <th title="Assigned">✓</th>
            <th>Project</th>
            <th>Context</th>
            <th>Card</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => {
            const id = `project-assign-${i}`
            return (
              <tr key={row.name.toLowerCase()} className={row.assigned ? undefined : 'project-table-unassigned'}>
                <td>
                  <input
                    id={id}
                    type="checkbox"
                    checked={row.assigned}
                    onChange={e => onChange(e.target.checked ? assignProject(value, row.name) : unassignProject(value, row.name))}
                  />
                </td>
                <td className="project-table-name"><label htmlFor={id}>{row.name}</label></td>
                <td>
                  <select
                    className="config-input project-table-select"
                    aria-label={`Context mode for ${row.name}`}
                    value={row.mode}
                    disabled={!row.assigned}
                    onChange={e => onChange(setProjectMode(value, row.name, e.target.value as ProjectContextMode))}
                  >
                    <option value="full">full</option>
                    <option value="card" disabled={!cardSelectable(row)}>card</option>
                  </select>
                </td>
                <td><CardCell row={row} /></td>
              </tr>
            )
          })}
        </tbody>
      </table>
      {anyWithoutCard && (
        <div className="field-hint">
          <code>card</code> needs a card; rows marked none have none yet.
          {/* New tab, like the output-style link: unsaved edits in this modal survive. */}
          <a className="setup-helper-link" href="#project-contexts" target="_blank" rel="noreferrer">
            Write cards under Project Contexts ↗
          </a>
        </div>
      )}
    </>
  )
}
