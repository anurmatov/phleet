import { useMemo } from 'react'
import type { WorkflowSummary, ScheduleSummary, WorkflowTypeInfo } from '../types'

interface NamespacesViewProps {
  namespaces: string[]
  workflows: WorkflowSummary[]
  schedules: ScheduleSummary[]
  workflowTypes: WorkflowTypeInfo[]
  schedulesLoading: boolean
}

export default function NamespacesView({
  namespaces,
  workflows,
  schedules,
  workflowTypes,
  schedulesLoading,
}: NamespacesViewProps) {
  const nsData = useMemo(() => {
    return namespaces.map(ns => {
      const activeWorkflows = workflows.filter(w => w.namespace === ns)
      const nsSchedules = schedules.filter(s => s.namespace === ns)
      const nsTypes = workflowTypes.filter(t => t.namespace === ns)
      return { ns, activeWorkflows, nsSchedules, nsTypes }
    })
  }, [namespaces, workflows, schedules, workflowTypes])

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Namespaces
          {namespaces.length > 0 && <span className="section-count">{namespaces.length}</span>}
        </h1>
      </div>

      <p style={{ color: 'var(--text-secondary)', marginBottom: 16, fontSize: 13 }}>
        Read-only overview. Namespaces are configured on the Temporal server.
      </p>

      {namespaces.length === 0 && (
        <div className="view-empty">No namespaces found. Temporal may not be configured.</div>
      )}

      <div className="instructions-list">
        {nsData.map(({ ns, activeWorkflows, nsSchedules, nsTypes }) => (
          <div key={ns} className="instr-row">
            <div className="instr-header" style={{ cursor: 'default' }}>
              <span className="instr-name">{ns}</span>
              <span className="instr-meta">
                <span className="instr-agents" title={`${activeWorkflows.length} active workflows`}>
                  {activeWorkflows.length} workflow{activeWorkflows.length !== 1 ? 's' : ''}
                </span>
                <span className="instr-total">
                  {schedulesLoading ? '…' : `${nsSchedules.length} schedule${nsSchedules.length !== 1 ? 's' : ''}`}
                </span>
                <span className="instr-total">
                  {nsTypes.length} type{nsTypes.length !== 1 ? 's' : ''}
                </span>
              </span>
            </div>
            {nsTypes.length > 0 && (
              <div style={{ padding: '8px 16px 12px', borderTop: '1px solid var(--border)', background: 'var(--bg-secondary)' }}>
                <div className="config-label" style={{ marginBottom: 6 }}>Registered workflow types</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                  {nsTypes.map(t => (
                    <span
                      key={t.name}
                      style={{
                        background: 'var(--bg-tertiary, #1e2a3a)',
                        border: '1px solid var(--border)',
                        borderRadius: 4,
                        padding: '2px 8px',
                        fontSize: 12,
                        color: 'var(--text-secondary)',
                      }}
                      title={t.description || t.name}
                    >
                      {t.name}
                    </span>
                  ))}
                </div>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
