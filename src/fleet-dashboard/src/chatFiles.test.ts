import { test } from 'node:test'
import assert from 'node:assert/strict'
import { MAX_FILE_BYTES, jpegName, planFile, prepareFile, scaledSize, sizeLabel, type JpegEncoder } from './chatFiles.ts'

const file = (name: string, type: string, bytes: number[] | number) => {
  const data = typeof bytes === 'number' ? new Uint8Array(bytes) : new Uint8Array(bytes)
  return { name, type, size: data.length, arrayBuffer: async () => data.buffer as ArrayBuffer }
}

test('a picture the chat service takes goes as it is; a large or foreign one is drawn again; anything else is a file', () => {
  assert.deepEqual(planFile({ name: 'a.png', type: 'image/png', size: 10 }), { type: 'as-is', kind: 'image' })
  assert.deepEqual(planFile({ name: 'a.jpg', type: 'image/jpeg', size: MAX_FILE_BYTES }), { type: 'as-is', kind: 'image' })
  assert.deepEqual(planFile({ name: 'a.jpg', type: 'image/jpeg', size: MAX_FILE_BYTES + 1 }), { type: 'reencode' })
  assert.deepEqual(planFile({ name: 'a.heic', type: 'image/heic', size: 10 }), { type: 'reencode' })
  // A file goes as the file it is, whatever it is, up to the limit.
  assert.deepEqual(planFile({ name: 'notes.pdf', type: 'application/pdf', size: 10 }), { type: 'as-is', kind: 'file' })
  assert.deepEqual(planFile({ name: 'a.svg', type: 'image/svg+xml', size: 10 }), { type: 'as-is', kind: 'file' })
  assert.deepEqual(planFile({ name: 'unknown', type: '', size: 10 }), { type: 'as-is', kind: 'file' })
  // A name is not a type: `.png` on something the browser does not call a picture is a file.
  assert.deepEqual(planFile({ name: 'page.png', type: 'text/html', size: 10 }), { type: 'as-is', kind: 'file' })
  assert.equal(planFile({ name: 'big.zip', type: 'application/zip', size: MAX_FILE_BYTES + 1 }).type, 'refuse')
  assert.equal(planFile({ name: 'empty.png', type: 'image/png', size: 0 }).type, 'refuse')
})

test('scaling keeps the shape, never enlarges and never reaches zero', () => {
  assert.deepEqual(scaledSize(4096, 2048), { width: 2048, height: 1024 })
  assert.deepEqual(scaledSize(800, 600), { width: 800, height: 600 })
  assert.deepEqual(scaledSize(10000, 1), { width: 2048, height: 1 })
  assert.deepEqual(scaledSize(0, 0), { width: 1, height: 1 })
})

test('names and sizes', () => {
  assert.equal(jpegName('IMG_0001.HEIC'), 'IMG_0001.jpg')
  assert.equal(jpegName('archive.tar.gz'), 'archive.tar.jpg')
  assert.equal(jpegName('noextension'), 'noextension.jpg')
  assert.equal(jpegName('.hidden'), 'image.jpg')
  assert.equal(sizeLabel(812), '812 B')
  assert.equal(sizeLabel(34 * 1024), '34 KB')
  assert.equal(sizeLabel(2.4 * 1024 * 1024), '2.4 MB')
})

test('preparing: as it is, re-encoded at the first quality that fits, or a reason', async () => {
  const never: JpegEncoder = async () => { throw new Error('not expected to draw') }

  const asIs = await prepareFile(file('shot.png', 'image/png', [1, 2, 3]), never)
  assert.deepEqual(asIs, { ok: true, kind: 'image', fileName: 'shot.png', contentType: 'image/png', bytes: new Uint8Array([1, 2, 3]) })

  const pdf = await prepareFile(file('notes.pdf', 'application/pdf', [9, 9]), never)
  assert.deepEqual(pdf, { ok: true, kind: 'file', fileName: 'notes.pdf', contentType: 'application/pdf', bytes: new Uint8Array([9, 9]) })
  const untyped = await prepareFile(file('Makefile', '', [1]), never)
  assert.equal(untyped.ok && untyped.contentType, 'application/octet-stream')

  // Too large at 0.85 and 0.7, fits at 0.5.
  const qualities: number[] = []
  const shrinking: JpegEncoder = async (_file, _size, quality) => {
    qualities.push(quality)
    return new Uint8Array(quality > 0.6 ? 200 : 50)
  }
  const drawn = await prepareFile(file('IMG.heic', 'image/heic', 500), shrinking, 100)
  assert.deepEqual(qualities, [0.85, 0.7, 0.5])
  assert.equal(drawn.ok && drawn.fileName, 'IMG.jpg')
  assert.equal(drawn.ok && drawn.contentType, 'image/jpeg')
  assert.equal(drawn.ok && drawn.bytes.length, 50)

  assert.equal(drawn.ok && drawn.kind, 'image')

  // A picture this browser cannot draw still goes, as the file it is.
  const undrawable = await prepareFile(file('IMG.heic', 'image/heic', 500), async () => null)
  assert.equal(undrawable.ok && undrawable.kind, 'file')
  assert.equal(undrawable.ok && undrawable.fileName, 'IMG.heic')
  const undrawableAndLarge = await prepareFile(file('IMG.heic', 'image/heic', 500), async () => null, 100)
  assert.match(undrawableAndLarge.ok ? '' : undrawableAndLarge.problem, /cannot make it smaller/)

  const tooLarge = await prepareFile(file('big.bmp', 'image/bmp', 500), async () => new Uint8Array(200), 100)
  assert.match(tooLarge.ok ? '' : tooLarge.problem, /too large/)

  const refused = await prepareFile(file('big.zip', 'application/zip', 500), never, 100)
  assert.match(refused.ok ? '' : refused.problem, /too large to send/)
})
