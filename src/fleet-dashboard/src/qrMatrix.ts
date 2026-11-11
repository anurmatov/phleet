import qrcode from 'qrcode-generator'

/**
 * The QR code for a piece of text, as rows of dark/light modules.
 *
 * Returned as plain data so the component draws it with ordinary elements: the text is a secret,
 * and it never passes through an HTML string on its way to the screen.
 */
export function qrMatrix(text: string): boolean[][] {
  // Type 0 picks the smallest version that fits. 'M' recovers about 15% damage, which is enough
  // for a screen photographed at an angle without growing the code past what a phone reads easily.
  const qr = qrcode(0, 'M')
  qr.addData(text, 'Byte')
  qr.make()

  const size = qr.getModuleCount()
  return Array.from({ length: size }, (_, row) =>
    Array.from({ length: size }, (_, column) => qr.isDark(row, column)))
}
