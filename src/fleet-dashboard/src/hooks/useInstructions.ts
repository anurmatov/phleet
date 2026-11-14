import { useState } from 'react'
import type { InstructionDetail, InstructionSummary } from '../types'
import type { KeyedTimers } from '../reconnect'
import { apiSend, errorMessage } from '../utils'
import { useKeyedConfirm } from './useKeyedStatus'
import { useVersionedResource, type VersionedResource } from './useVersionedResource'

export type DeployState = 'idle' | 'deploying' | 'success' | 'error'

/** The instructions page: the versioned editor, plus deploying one version to an assigned agent. */
export interface InstructionsState extends VersionedResource<InstructionSummary, InstructionDetail> {
  deployConfirm: Record<string, boolean>
  deployState: Record<string, DeployState>
  deployMsg: Record<string, string>
  deployClick: (e: React.MouseEvent, name: string, version: number, agentName: string) => void
}

export function useInstructions(timers: KeyedTimers): InstructionsState {
  const resource = useVersionedResource<InstructionSummary, InstructionDetail>('/api/instructions', 'instr', timers)
  const confirm = useKeyedConfirm(timers, 'deploy')
  const [deployState, setDeployState] = useState<Record<string, DeployState>>({})
  const [deployMsg, setDeployMsg] = useState<Record<string, string>>({})

  /** First click arms, second restarts the agent on that version. */
  function deployClick(e: React.MouseEvent, name: string, version: number, agentName: string) {
    e.stopPropagation()
    const key = `${name}:${version}:${agentName}`
    if (deployState[key] === 'deploying') return
    if (!confirm.armed[key]) { confirm.arm(key); return }
    confirm.disarm(key)
    setDeployState(prev => ({ ...prev, [key]: 'deploying' }))
    apiSend(`/api/agents/${encodeURIComponent(agentName)}/restart-with-version`, 'POST', { instructionName: name, versionNumber: version })
      .then(() => { setDeployState(prev => ({ ...prev, [key]: 'success' })); setDeployMsg(prev => ({ ...prev, [key]: `Deployed to ${agentName}` })) })
      .catch((err: unknown) => { setDeployState(prev => ({ ...prev, [key]: 'error' })); setDeployMsg(prev => ({ ...prev, [key]: errorMessage(err) })) })
      .finally(() => timers.after(`deploy-result:${key}`, 4000, () => setDeployState(prev => ({ ...prev, [key]: 'idle' }))))
  }

  return { ...resource, deployConfirm: confirm.armed, deployState, deployMsg, deployClick }
}
