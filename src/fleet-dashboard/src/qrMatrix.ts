import qrcode from 'qrcode-generator'

/**
 * The most bytes one QR code holds at the error correction used here (version 40, level M, byte
 * mode). Longer text does not fit in a code at all.
 */
export const QR_MAX_BYTES = 2331

/** Whether the text fits in one code, counted in the UTF-8 bytes the code carries. */
export function fitsInQrCode(text: string): boolean {
  return new TextEncoder().encode(text).length <= QR_MAX_BYTES
}

/**
 * The QR code for a piece of text, as rows of dark/light modules. The text must fit
 * (`fitsInQrCode`); the library refuses anything longer.
 *
 * Returned as plain data so the component draws it with ordinary elements: the text is a secret,
 * and it never passes through an HTML string on its way to the screen.
 */
export function qrMatrix(text: string): boolean[][] {
  // Type 0 picks the smallest version that fits. 'M' recovers about 15% damage, which is enough
  // for a screen photographed at an angle without growing the code past what a phone reads easily.
  const qr = qrcode(0, 'M')
  // The library writes the low byte of each character, so given the text itself it would write
  // 'т' and 'B' as the same byte. One character per UTF-8 byte makes it write the text's bytes.
  qr.addData(String.fromCharCode(...new TextEncoder().encode(text)), 'Byte')
  qr.make()

  const size = qr.getModuleCount()
  return Array.from({ length: size }, (_, row) =>
    Array.from({ length: size }, (_, column) => qr.isDark(row, column)))
}
