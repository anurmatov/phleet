import type { Alert } from '../types'
import { formatTime } from '../utils'

interface AlertsViewProps {
  alerts: Alert[]
  onDismiss: (id: string) => void
  onDismissAll: () => void
  onAlertEntityLink: (alert: Alert) => void
}

export default function AlertsView({ alerts, onDismiss, onDismissAll, onAlertEntityLink }: AlertsViewProps) {
  const unread = alerts.filter(a => !a.dismissed)

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">Alerts</h1>
        {unread.length > 0 && (
          <button className="view-page-action" onClick={onDismissAll}>Dismiss all</button>
        )}
      </div>
      {unread.length === 0 ? (
        <div className="view-empty">No active alerts.</div>
      ) : (
        <div className="alerts-list">
          {unread.map(alert => (
            <div key={alert.id} className={`alert-item alert-${alert.type}`}>
              <div className="alert-item-msg">
                {alert.message}
                {(alert.workflowId || alert.agentName) && (
                  <button
                    className="alert-entity-link"
                    onClick={() => { onDismiss(alert.id); onAlertEntityLink(alert) }}
                  >
                    {alert.workflowId ? 'view workflow' : 'view agent'}
                  </button>
                )}
              </div>
              <div className="alert-item-meta">
                <span className="alert-item-time">{formatTime(alert.timestamp)}</span>
                <button className="alert-dismiss-btn" onClick={() => onDismiss(alert.id)}>dismiss</button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
