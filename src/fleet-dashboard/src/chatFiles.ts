// Getting a picked, pasted or dropped file into the shape the chat service takes.
//
// The chat service accepts JPEG, PNG, GIF and WebP up to a size limit and refuses everything else
// when a place is reserved for it. A picture already in one of those types and under the limit goes
// as it is (a screenshot stays a sharp PNG). Any other picture the browser can read (HEIC from a
// phone, a huge photo) is drawn again as a JPEG, scaled down and squeezed until it fits, the same
// as the phone app does.
//
// The decisions are plain functions `node --test` can run; drawing needs a browser and is passed in.

import { IMAGE_TYPES } from './chatProtocol.ts'

/** The chat service's limit for one file. */
export const MAX_FILE_BYTES = 8 * 1024 * 1024

/** The longest side of a picture this page re-encodes. Legible for a screenshot, small for a photo. */
export const MAX_IMAGE_SIDE = 2048

/** The JPEG qualities tried in turn until a re-encoded picture is under the limit. */
export const JPEG_QUALITIES: readonly number[] = [0.85, 0.7, 0.5, 0.3]

export interface PickedFile {
  name: string
  type: string
  size: number
}

export type FilePlan =
  /** Send the bytes as they are. */
  | { type: 'as-is' }
  /** Draw it again as a JPEG. */
  | { type: 'reencode' }
  /** Not something chat can carry. */
  | { type: 'refuse'; problem: string }

/** What to do with a picked file. */
export function planFile(file: PickedFile, maxBytes: number = MAX_FILE_BYTES): FilePlan {
  if (file.size === 0) return { type: 'refuse', problem: `“${file.name}” is empty.` }
  if (IMAGE_TYPES.includes(file.type)) return file.size <= maxBytes ? { type: 'as-is' } : { type: 'reencode' }
  // A type the browser may still be able to draw (HEIC in Safari, BMP, AVIF). SVG is left out: it
  // is a document that can carry script, and drawing one to send as a photo is nobody's intent.
  if (file.type.startsWith('image/') && file.type !== 'image/svg+xml') return { type: 'reencode' }
  return { type: 'refuse', problem: `“${file.name}” is not a picture. Chat takes pictures only.` }
}

/** The size a picture is drawn at so its longer side is at most `maxSide`. Never larger than it is. */
export function scaledSize(width: number, height: number, maxSide: number = MAX_IMAGE_SIDE): { width: number; height: number } {
  const longSide = Math.max(width, height)
  const ratio = longSide > 0 ? Math.min(1, maxSide / longSide) : 1
  return { width: Math.max(1, Math.round(width * ratio)), height: Math.max(1, Math.round(height * ratio)) }
}

/** A file's name with `.jpg` in place of whatever it ended in. */
export function jpegName(name: string): string {
  const base = name.replace(/\.[^./\\]*$/, '')
  return `${base || 'image'}.jpg`
}

/** A size in a few characters: `812 B`, `34 KB`, `2.4 MB`. */
export function sizeLabel(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/** Draws a picture as a JPEG of the given size and quality. Null when the browser cannot. */
export type JpegEncoder = (file: Blob, size: (width: number, height: number) => { width: number; height: number }, quality: number) => Promise<Uint8Array | null>

export type Prepared =
  | { ok: true; fileName: string; contentType: string; bytes: Uint8Array }
  | { ok: false; problem: string }

/** The bytes to send for a picked file, or why it cannot be sent. */
export async function prepareFile(
  file: PickedFile & { arrayBuffer(): Promise<ArrayBuffer> },
  encode: JpegEncoder,
  maxBytes: number = MAX_FILE_BYTES,
): Promise<Prepared> {
  const plan = planFile(file, maxBytes)
  if (plan.type === 'refuse') return { ok: false, problem: plan.problem }
  if (plan.type === 'as-is') {
    try {
      return { ok: true, fileName: file.name || 'image', contentType: file.type, bytes: new Uint8Array(await file.arrayBuffer()) }
    } catch {
      return { ok: false, problem: `“${file.name}” could not be read.` }
    }
  }
  for (const quality of JPEG_QUALITIES) {
    const bytes = await encode(file as unknown as Blob, (w, h) => scaledSize(w, h), quality)
    if (!bytes) return { ok: false, problem: `“${file.name}” is not a picture this browser can read.` }
    if (bytes.length <= maxBytes) return { ok: true, fileName: jpegName(file.name), contentType: 'image/jpeg', bytes }
  }
  return { ok: false, problem: `“${file.name}” is too large to send, even made smaller.` }
}

/** The browser's encoder: decode, draw upright on white at the size asked for, write a JPEG. */
export const browserJpegEncoder: JpegEncoder = async (file, size, quality) => {
  let bitmap: ImageBitmap
  try {
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' })
  } catch {
    return null
  }
  try {
    const target = size(bitmap.width, bitmap.height)
    const canvas = document.createElement('canvas')
    canvas.width = target.width
    canvas.height = target.height
    const context = canvas.getContext('2d')
    if (!context) return null
    // A JPEG has no transparency: what was see-through becomes white, not black.
    context.fillStyle = '#fff'
    context.fillRect(0, 0, target.width, target.height)
    context.drawImage(bitmap, 0, 0, target.width, target.height)
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', quality))
    return blob ? new Uint8Array(await blob.arrayBuffer()) : null
  } catch {
    return null
  } finally {
    bitmap.close()
  }
}
