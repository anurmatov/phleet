import type { ProjectContextSummary } from '../types'
import { assignProject, projectRows, unassignProject, type ProjectAssignments } from '../projectAssignments'
import { sizeCell, type MeterLimit } from '../sizeMeter'
import SizeCellView from './SizeCellView'

interface ProjectAssignmentTableProps {
  contexts: ProjectContextSummary[]
  value: ProjectAssignments
  /** The project-context soft limit from the server: null = disabled, undefined = unavailable. */
  sizeLimit: MeterLimit
  onChange: (next: ProjectAssignments) => void
}

/** The only edit surface for an agent's project assignments. */
export default function ProjectAssignmentTable({ contexts, value, sizeLimit, onChange }: ProjectAssignmentTableProps) {
  const rows = projectRows(contexts, value)
  if (rows.length === 0) {
    return <div className="config-hint-text">No project contexts yet — create one under Project Contexts.</div>
  }
  return (
    <table className="project-table">
      <colgroup>
        <col className="project-table-col-assigned" />
        <col />
        <col className="project-table-col-size" />
      </colgroup>
      <thead>
        <tr>
          <th title="Assigned">✓</th>
          <th>Project</th>
          <th title="UTF-8 bytes of the current project context, loaded in full on every turn">Size</th>
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
                {row.hasContext
                  ? <SizeCellView cell={sizeCell(row.currentBytes, sizeLimit)} />
                  : <span className="project-table-muted" title="No project context has this name.">no context</span>}
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}
