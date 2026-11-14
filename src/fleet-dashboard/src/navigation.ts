// The dashboard's pages as the navigation shows them: what each is called, the icon beside it,
// and which group it sits in.
//
// One list feeds the sidebar, the phone's tab bar and the page's title, so the three cannot
// disagree about what a page is called. Kept free of JSX so `node --test` can run it.

import { ACTIVE_VIEWS, type ActiveView } from './activeViews.ts'

export interface NavItem {
  view: ActiveView
  label: string
  /** A name from components/Icon.tsx. */
  icon: string
}

export interface NavGroup {
  label: string
  items: NavItem[]
}

export const NAV_GROUPS: readonly NavGroup[] = [
  {
    label: 'Fleet',
    items: [
      { view: 'agents', label: 'Agents', icon: 'agents' },
      { view: 'workflows', label: 'Workflows', icon: 'workflows' },
      { view: 'chat', label: 'Chat', icon: 'chat' },
    ],
  },
  {
    label: 'Knowledge',
    items: [
      { view: 'instructions', label: 'Instructions', icon: 'instructions' },
      { view: 'project-contexts', label: 'Project Contexts', icon: 'contexts' },
      { view: 'output-styles', label: 'Output Styles', icon: 'styles' },
      { view: 'memory', label: 'Memory', icon: 'memory' },
    ],
  },
  {
    label: 'Automation',
    items: [
      { view: 'wf-definitions', label: 'Definitions', icon: 'definitions' },
      { view: 'schedules', label: 'Schedules', icon: 'schedules' },
      { view: 'namespaces', label: 'Namespaces', icon: 'namespaces' },
      { view: 'epic-grants', label: 'Epic Grants', icon: 'grants' },
    ],
  },
  {
    label: 'Settings',
    items: [
      { view: 'repositories', label: 'Repositories', icon: 'repositories' },
      { view: 'credentials', label: 'Credentials', icon: 'credentials' },
      { view: 'alerts', label: 'Alerts', icon: 'alerts' },
    ],
  },
]

/** What a phone's tab bar holds. Everything else is one tap further, under More. */
export const TAB_VIEWS: readonly ActiveView[] = ['agents', 'workflows', 'chat']

export const NAV_ITEMS: readonly NavItem[] = NAV_GROUPS.flatMap(group => group.items)

export function navItem(view: ActiveView): NavItem {
  return NAV_ITEMS.find(item => item.view === view) ?? NAV_ITEMS[0]
}

/** Every page is reachable from the navigation, and none is listed twice. */
export function navigationCoversEveryView(): boolean {
  const listed = NAV_ITEMS.map(item => item.view)
  return new Set(listed).size === listed.length && ACTIVE_VIEWS.every(view => listed.includes(view))
}
