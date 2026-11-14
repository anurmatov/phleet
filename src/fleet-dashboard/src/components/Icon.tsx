// The dashboard's icons: one small set, drawn the same way, so a thing means the same wherever
// it appears. Each is a 24-unit outline that takes the colour of the text around it.
//
// An icon is decoration unless it is the only thing in a control. Then the control carries the
// label (`aria-label` and `title`), never the icon.

const paths = {
  agents: 'M8.5 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Zm8 1a2.75 2.75 0 1 0 0-5.5 2.75 2.75 0 0 0 0 5.5ZM2.5 19.5c.4-3 2.9-5 6-5s5.6 2 6 5M16 14.6c2.7.1 4.9 1.8 5.5 4.4',
  workflows: 'M6 7.5a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM6 20.5a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM18 10a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM6 7.5v9M18 10c0 3.5-3 4-6 4.2-3 .3-6 .8-6 2.3',
  chat: 'M4 6.5A2.5 2.5 0 0 1 6.5 4h11A2.5 2.5 0 0 1 20 6.5v7a2.5 2.5 0 0 1-2.5 2.5H11l-4.5 3.5V16h0A2.5 2.5 0 0 1 4 13.5v-7Z',
  instructions: 'M7 3.5h7l4 4V19a1.5 1.5 0 0 1-1.5 1.5h-9.5A1.5 1.5 0 0 1 5.5 19V5A1.5 1.5 0 0 1 7 3.5Zm6.5 0V8h4.5M9 12.5h6M9 16h6',
  contexts: 'M5 5.5A2.5 2.5 0 0 1 7.5 3H19v15H7.5A2.5 2.5 0 0 0 5 20.5v-15Zm0 15A2.5 2.5 0 0 0 7.5 21H19M9 7.5h6M9 11h4',
  styles: 'M5 6.5V5h14v1.5M12 5v14M9.5 19h5',
  memory: 'M12 9c4.1 0 7.5-1.3 7.5-3S16.1 3 12 3 4.5 4.3 4.5 6 7.9 9 12 9Zm7.5-3v12c0 1.7-3.4 3-7.5 3s-7.5-1.3-7.5-3V6m15 6c0 1.7-3.4 3-7.5 3s-7.5-1.3-7.5-3',
  definitions: 'M4 5.5A1.5 1.5 0 0 1 5.5 4h4A1.5 1.5 0 0 1 11 5.5v4A1.5 1.5 0 0 1 9.5 11h-4A1.5 1.5 0 0 1 4 9.5v-4Zm9 9a1.5 1.5 0 0 1 1.5-1.5h4a1.5 1.5 0 0 1 1.5 1.5v4a1.5 1.5 0 0 1-1.5 1.5h-4a1.5 1.5 0 0 1-1.5-1.5v-4ZM7.5 11v3A2.5 2.5 0 0 0 10 16.5h3',
  schedules: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18Zm0-13.5V12l3 2',
  namespaces: 'm12 3 8.5 4.5L12 12 3.5 7.5 12 3Zm-8.5 9L12 16.5l8.5-4.5M3.5 16.5 12 21l8.5-4.5',
  grants: 'M12 3 5 5.5v6c0 4.2 2.8 7.7 7 9.5 4.2-1.8 7-5.3 7-9.5v-6L12 3Zm-3 9 2.2 2.2L15.5 10',
  repositories: 'M6.5 3.5h11A1.5 1.5 0 0 1 19 5v15.5H7A2 2 0 0 1 5 18.5V5a1.5 1.5 0 0 1 1.5-1.5ZM5 18.5A2 2 0 0 1 7 16.5h12M9 7.5h6',
  credentials: 'M8 20a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm2.8-6.8L20 4M16.5 7.5l2.5 2.5M13.5 10.5l2 2',
  alerts: 'M6 16.5V11a6 6 0 1 1 12 0v5.5l1.5 2h-15l1.5-2ZM10 20.5a2.2 2.2 0 0 0 4 0',
  search: 'M10.5 17a6.5 6.5 0 1 0 0-13 6.5 6.5 0 0 0 0 13Zm4.7-1.8L20 20',
  more: 'M5 12a.4.4 0 1 0 .8 0 .4.4 0 0 0-.8 0Zm6.6 0a.4.4 0 1 0 .8 0 .4.4 0 0 0-.8 0Zm6.6 0a.4.4 0 1 0 .8 0 .4.4 0 0 0-.8 0Z',
  menu: 'M4 7h16M4 12h16M4 17h16',
  close: 'M6 6l12 12M18 6 6 18',
  plus: 'M12 5v14M5 12h14',
  refresh: 'M19.5 12a7.5 7.5 0 1 1-2.2-5.3M19.5 4.5v4h-4',
  reprovision: 'M4.5 12a7.5 7.5 0 0 1 13-5.1M19.5 12a7.5 7.5 0 0 1-13 5.1M17.5 3.5v3.5H14M6.5 20.5V17H10',
  restart: 'M12 3.5V11M7.2 6.3a7.5 7.5 0 1 0 9.6 0',
  stop: 'M7 7h10v10H7z',
  play: 'M8 5.5v13l10.5-6.5L8 5.5Z',
  logs: 'M5 6h14M5 10h14M5 14h9M5 18h6',
  settings: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm7.4-3a7.4 7.4 0 0 0-.1-1.3l2-1.5-2-3.4-2.3 1a7.4 7.4 0 0 0-2.2-1.3L14.4 3h-4l-.4 2.5a7.4 7.4 0 0 0-2.2 1.3l-2.3-1-2 3.4 2 1.5a7.4 7.4 0 0 0 0 2.6l-2 1.5 2 3.4 2.3-1a7.4 7.4 0 0 0 2.2 1.3l.4 2.5h4l.4-2.5a7.4 7.4 0 0 0 2.2-1.3l2.3 1 2-3.4-2-1.5c.1-.400.1-.900.1-1.3Z',
  trash: 'M5 7h14M10 7V4.5h4V7M7 7l.8 12.5h8.4L17 7M10 11v5M14 11v5',
  chevronDown: 'm6 9 6 6 6-6',
  chevronRight: 'm9 6 6 6-6 6',
  external: 'M14 5h5v5M19 5l-8 8M11 6H6.5A1.5 1.5 0 0 0 5 7.5v10A1.5 1.5 0 0 0 6.5 19h10a1.5 1.5 0 0 0 1.5-1.5V13',
  sun: 'M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm0-13v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M5.6 18.4 7 17M17 7l1.4-1.4',
  moon: 'M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z',
  system: 'M4 5.5A1.5 1.5 0 0 1 5.5 4h13A1.5 1.5 0 0 1 20 5.5v9a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 14.5v-9ZM9 20h6M12 16v4',
  check: 'm5 12.5 4.5 4.5L19 7.5',
  warning: 'M12 4 2.5 20h19L12 4Zm0 6v4.5M12 17.5h.01',
  edit: 'M4.5 19.5h3.5L19 8.5 15.5 5 4.5 16v3.5ZM13.5 7l3.5 3.5',
  copy: 'M9 9.5A1.5 1.5 0 0 1 10.5 8h8A1.5 1.5 0 0 1 20 9.5v9a1.5 1.5 0 0 1-1.5 1.5h-8A1.5 1.5 0 0 1 9 18.5v-9ZM6 16H5.5A1.5 1.5 0 0 1 4 14.5v-9A1.5 1.5 0 0 1 5.5 4h8A1.5 1.5 0 0 1 15 5.5V6',
  clock: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18Zm0-13.5V12l3 2',
  cancel: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18ZM9 9l6 6M15 9l-6 6',
  hand: 'M8 11V6a1.5 1.5 0 0 1 3 0v4m0-5a1.5 1.5 0 0 1 3 0v5m0-4a1.5 1.5 0 0 1 3 0v7.5c0 3.5-2.3 6-5.8 6-2.7 0-4.2-1.2-5.6-3.5L4 13.5a1.5 1.5 0 0 1 2.6-1.5L8 14',
} as const

export type IconName = keyof typeof paths

export default function Icon({ name, size = 18, className }: { name: IconName; size?: number; className?: string }) {
  return (
    <svg
      className={`icon${className ? ' ' + className : ''}`}
      width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false"
    >
      <path d={paths[name]} />
    </svg>
  )
}
