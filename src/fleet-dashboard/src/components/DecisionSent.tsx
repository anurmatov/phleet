// What a waiting run shows in place of its decision buttons once a decision was sent from this
// page: the run keeps its phase until the workflow takes the signal up.

interface DecisionSentProps {
  /** The label of the button that was sent. */
  label: string
  /** Brings the buttons back, for a decision that has to be sent once more. */
  onSendAgain: () => void
}

export default function DecisionSent({ label, onSendAgain }: DecisionSentProps) {
  return (
    <span className="wf-decision-sent">
      <span className="wf-action-feedback wf-action-success">{label} sent</span>
      <span className="wf-decision-wait">waiting for the workflow</span>
      <button className="btn btn-s btn-quiet wf-decision-again" title="Show the buttons again" onClick={onSendAgain}>
        Send again
      </button>
    </span>
  )
}
