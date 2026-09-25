import type { SizeCell } from '../sizeMeter'

/** A Size cell: bytes, with the ⚠ over-limit state the editor meters use. */
export default function SizeCellView({ cell }: { cell: SizeCell }) {
  return (
    <span className={cell.over ? 'size-cell size-cell-over' : 'size-cell'} title={cell.title}>
      {cell.over && '⚠ '}{cell.text}
    </span>
  )
}
