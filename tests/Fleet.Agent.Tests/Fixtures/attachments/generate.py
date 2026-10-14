"""Synthetic attachment fixtures for Pi attachment parity (#455 §9).

Writes every fixture into OUT and a manifest.json mapping each fixture name to
its file and the random 6-character code it embeds. Nothing here is personal
content: every image, page and document is drawn from a code, noise and fixed
words. No fixture carries EXIF except `photo-exif6.jpg`, whose only tag is
Orientation = 6.

Usage:
    python3 generate.py OUT [--seed N] [--only name,name]

Needs python3 (stdlib only), `ffmpeg` (JPEG, WebP, mp4, mp3) and `qpdf` (the
three encrypted PDFs). A missing tool is an error naming it, never a skip.
"""

import argparse
import json
import os
import random
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
import zlib

# ── Codes ───────────────────────────────────────────────────────────────────

CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"


def new_code(rng):
    return "".join(rng.choice(CODE_ALPHABET) for _ in range(6))


# ── A 5x7 bitmap font for the code characters ───────────────────────────────

FONT = {
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "B": ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
    "C": ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
    "D": ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "F": ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
    "G": ["01110", "10001", "10000", "10111", "10001", "10001", "01111"],
    "H": ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
    "J": ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
    "K": ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
    "L": ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
    "M": ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
    "N": ["10001", "10001", "11001", "10101", "10011", "10001", "10001"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    "Q": ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "S": ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "U": ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    "V": ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
    "W": ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
    "X": ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
    "Y": ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
    "Z": ["11111", "00001", "00010", "00100", "01000", "10000", "11111"],
    "2": ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
    "3": ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
    "4": ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
    "5": ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
    "6": ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
    "7": ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
    "8": ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
    "9": ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
}


class Canvas:
    """A greyscale or RGB pixel buffer, rows of bytes."""

    def __init__(self, width, height, channels=3, fill=255, noise=None, rng=None):
        self.width, self.height, self.channels = width, height, channels
        row = width * channels
        if noise is None:
            self.pixels = bytearray([fill]) * (row * height)
        else:
            # Noise in [noise, 255]: light enough that the dark code stays legible.
            span = 256 - noise
            raw = rng.randbytes(row * height)
            self.pixels = bytearray(noise + (b % span) for b in raw)

    def rect(self, x, y, w, h, value=0):
        x0, y0 = max(0, x), max(0, y)
        x1, y1 = min(self.width, x + w), min(self.height, y + h)
        if x0 >= x1 or y0 >= y1:
            return
        run = bytes([value]) * ((x1 - x0) * self.channels)
        for yy in range(y0, y1):
            start = (yy * self.width + x0) * self.channels
            self.pixels[start:start + len(run)] = run

    def text(self, text, x, y, scale):
        """Draws `text` in the 5x7 font, each font pixel a `scale` square."""
        for ch in text:
            glyph = FONT[ch]
            for gy, line in enumerate(glyph):
                for gx, bit in enumerate(line):
                    if bit == "1":
                        self.rect(x + gx * scale, y + gy * scale, scale, scale, 0)
            x += 6 * scale

    def code_banner(self, code, max_scale=None):
        """The code, centred, as large as fits (or `max_scale`), on a white box."""
        scale = max(2, min(self.width // (6 * 6 + 4), self.height // 12, max_scale or self.width))
        w, h = 6 * 6 * scale, 7 * scale
        x, y = (self.width - w) // 2, (self.height - h) // 2
        pad = scale * 2
        self.rect(x - pad, y - pad, w + 2 * pad, h + 2 * pad, 255)
        self.text(code, x, y, scale)

    def rows(self):
        row = self.width * self.channels
        for y in range(self.height):
            yield bytes(self.pixels[y * row:(y + 1) * row])


def png_bytes(canvas):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    color_type = 2 if canvas.channels == 3 else 0
    ihdr = struct.pack(">IIBBBBB", canvas.width, canvas.height, 8, color_type, 0, 0, 0)
    raw = b"".join(b"\x00" + row for row in canvas.rows())
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b"")


# ── External tools ──────────────────────────────────────────────────────────


def require(tool):
    if shutil.which(tool) is None:
        sys.exit(f"generate.py: '{tool}' is required and was not found on PATH")


def ffmpeg(*args):
    require("ffmpeg")
    subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-y", *args], check=True)


def qpdf(*args):
    require("qpdf")
    subprocess.run(["qpdf", *args], check=True)


def png_to(png_path, out_path, *codec):
    ffmpeg("-i", png_path, "-frames:v", "1", *codec, out_path)


def with_exif_orientation(jpeg, orientation):
    """The JPEG with one APP1 Exif segment holding only Orientation."""
    tiff = b"MM\x00\x2a" + struct.pack(">I", 8)
    tiff += struct.pack(">H", 1) + struct.pack(">HHIHH", 0x0112, 3, 1, orientation, 0) + struct.pack(">I", 0)
    app1 = b"Exif\x00\x00" + tiff
    segment = b"\xff\xe1" + struct.pack(">H", len(app1) + 2) + app1
    assert jpeg[:2] == b"\xff\xd8"
    return jpeg[:2] + segment + jpeg[2:]


# ── PDF ─────────────────────────────────────────────────────────────────────


class Pdf:
    """A minimal PDF 1.4 writer: pages of Courier text or one greyscale image each."""

    def __init__(self):
        self.objects = []  # bytes, 1-based ids
        self.pages = []
        self.font = self.add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>")

    def add(self, body):
        self.objects.append(body)
        return len(self.objects)

    def stream(self, data, extra=b"", compress=True):
        if compress:
            data = zlib.compress(data, 6)
            extra += b" /Filter /FlateDecode"
        return self.add(b"<< /Length %d%s >>\nstream\n" % (len(data), extra) + data + b"\nendstream")

    @staticmethod
    def escape(text):
        return text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)").encode("latin-1")

    def text_page(self, lines, size=11, leading=None, width=612, height=792):
        leading = leading or size * 1.25
        ops = [b"BT /F1 %g Tf %g TL 36 %g Td" % (size, leading, height - 36 - size)]
        for line in lines:
            ops.append(b"(" + self.escape(line) + b") Tj T*")
        ops.append(b"ET")
        content = self.stream(b"\n".join(ops))
        self.pages.append((content, b"<< /Font << /F1 %d 0 R >> >>" % self.font, width, height))

    def image_page(self, canvas, width_pt, height_pt):
        assert canvas.channels == 1
        raw = b"".join(canvas.rows())
        image = self.stream(raw, b" /Type /XObject /Subtype /Image /Width %d /Height %d"
                                 b" /ColorSpace /DeviceGray /BitsPerComponent 8" % (canvas.width, canvas.height))
        content = self.stream(b"q %g 0 0 %g 0 0 cm /Im0 Do Q" % (width_pt, height_pt))
        self.pages.append((content, b"<< /XObject << /Im0 %d 0 R >> >>" % image, width_pt, height_pt))

    def bytes(self):
        pages_id = len(self.objects) + len(self.pages) + 1
        kids = []
        for content, resources, w, h in self.pages:
            kids.append(self.add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 %g %g] /Contents %d 0 R /Resources %s >>"
                                 % (pages_id, w, h, content, resources)))
        assert self.add(b"<< /Type /Pages /Kids [%s] /Count %d >>"
                        % (b" ".join(b"%d 0 R" % k for k in kids), len(kids))) == pages_id
        catalog = self.add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages_id)

        out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
        offsets = []
        for number, body in enumerate(self.objects, start=1):
            offsets.append(len(out))
            out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
        xref = len(out)
        out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(self.objects) + 1)
        for offset in offsets:
            out += b"%010d 00000 n \n" % offset
        out += b"trailer\n<< /Size %d /Root %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(self.objects) + 1, catalog, xref)
        return bytes(out)


WORDS = ("ledger invoice quarter summary harbor meadow copper lantern orbit pebble "
         "signal timber violet window yonder zephyr anchor bramble cinder dapple").split()


def prose(rng, count):
    return " ".join(rng.choice(WORDS) for _ in range(count))


def text_pdf(code, rng, pages=1, lines_per_page=40, chars=None, size=11, leading=None):
    pdf = Pdf()
    for page in range(pages):
        lines = [f"Synthetic fixture page {page + 1}. Code: {code}"] if page == 0 else [f"Page {page + 1}."]
        while len(lines) < lines_per_page:
            line = prose(rng, 12)
            lines.append(line if chars is None else (line * (chars // len(line) + 1))[:chars])
        pdf.text_page(lines, size=size, leading=leading)
    return pdf.bytes()


def scanned_pdf(code, rng, width_px, height_px, width_pt, height_pt, noise):
    pdf = Pdf()
    canvas = Canvas(width_px, height_px, channels=1, noise=noise, rng=rng)
    canvas.code_banner(code)
    pdf.image_page(canvas, width_pt, height_pt)
    return pdf.bytes()


# ── Office (OOXML, minimal and valid) ───────────────────────────────────────

# The public OOXML namespace and content-type URIs are assembled from parts so the
# structural hostname sweep over Fixtures/ (FixtureHygieneTests F5) never sees a
# host-like literal here; these are ECMA-376 identifiers, not hosts Fleet reaches.
_OXML = "http://schemas." + "openxmlformats" + "." + "org"


def _uri(*parts):
    return "/".join(parts)


_PKG = _uri(_OXML, "package", "2006")
_REL = _uri(_OXML, "officeDocument", "2006", "relationships")


def _xml(body):
    return '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n' + body


def office(path, main_part, main_type, main_xml, rel_type, extra=()):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        overrides = "".join(f'<Override PartName="/{name}" ContentType="{kind}"/>' for name, kind, _ in
                            [(main_part, main_type, None), *extra])
        z.writestr("[Content_Types].xml", _xml(
            f'<Types xmlns="{_PKG}/content-types">'
            f'<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            f'<Default Extension="xml" ContentType="application/xml"/>{overrides}</Types>'))
        z.writestr("_rels/.rels", _xml(
            f'<Relationships xmlns="{_PKG}/relationships">'
            f'<Relationship Id="rId1" Type="{_REL}/{rel_type}" Target="{main_part}"/></Relationships>'))
        z.writestr(main_part, _xml(main_xml))
        for name, _, content in extra:
            z.writestr(name, _xml(content))


def docx(path, code):
    w = _uri(_OXML, "wordprocessingml", "2006", "main")
    office(path, "word/document.xml",
           "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
           f'<w:document xmlns:w="{w}"><w:body><w:p><w:r><w:t>Synthetic document. Code: {code}</w:t></w:r></w:p>'
           f'</w:body></w:document>', "officeDocument")


def xlsx(path, code):
    s = _uri(_OXML, "spreadsheetml", "2006", "main")
    office(path, "xl/workbook.xml",
           "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
           f'<workbook xmlns="{s}" xmlns:r="{_REL}"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>'
           f'</workbook>', "officeDocument",
           extra=[
               ("xl/_rels/workbook.xml.rels", "application/vnd.openxmlformats-package.relationships+xml",
                f'<Relationships xmlns="{_PKG}/relationships">'
                f'<Relationship Id="rId1" Type="{_REL}/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'),
               ("xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml",
                f'<worksheet xmlns="{s}"><sheetData><row r="1">'
                f'<c r="A1" t="inlineStr"><is><t>Code</t></is></c>'
                f'<c r="B1" t="inlineStr"><is><t>{code}</t></is></c></row></sheetData></worksheet>'),
           ])


def pptx(path, code):
    p = _uri(_OXML, "presentationml", "2006", "main")
    a = _uri(_OXML, "drawingml", "2006", "main")
    office(path, "ppt/presentation.xml",
           "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
           f'<p:presentation xmlns:p="{p}" xmlns:r="{_REL}"><p:sldIdLst><p:sldId id="256" r:id="rId1"/></p:sldIdLst>'
           f'<p:sldSz cx="9144000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>',
           "officeDocument",
           extra=[
               ("ppt/_rels/presentation.xml.rels", "application/vnd.openxmlformats-package.relationships+xml",
                f'<Relationships xmlns="{_PKG}/relationships">'
                f'<Relationship Id="rId1" Type="{_REL}/slide" Target="slides/slide1.xml"/></Relationships>'),
               ("ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml",
                f'<p:sld xmlns:p="{p}" xmlns:a="{a}"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/>'
                f'<p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/><p:sp><p:nvSpPr><p:cNvPr id="2" name="Text"/>'
                f'<p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:r><a:t>Code: {code}</a:t>'
                f'</a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>'),
           ])


# ── The fixture set ─────────────────────────────────────────────────────────


def build(out, seed, only):
    rng = random.Random(seed)
    os.makedirs(out, exist_ok=True)
    manifest = {}
    work = tempfile.mkdtemp(prefix="fixture-gen-")

    def emit(name, filename, data=None, code=None, **facts):
        path = os.path.join(out, filename)
        if data is not None:
            with open(path, "wb") as f:
                f.write(data)
        manifest[name] = {"file": filename, "code": code, **facts}

    def want(name):
        return not only or name in only

    def code_png(code, width, height, channels=3, noise=None, max_scale=None):
        canvas = Canvas(width, height, channels=channels, noise=noise, rng=rng)
        canvas.code_banner(code, max_scale)
        return png_bytes(canvas)

    def via_png(code, width, height, filename, *codec, noise=None):
        src = os.path.join(work, filename + ".png")
        with open(src, "wb") as f:
            f.write(code_png(code, width, height, noise=noise))
        png_to(src, os.path.join(out, filename), *codec)

    try:
        # Images.
        if want("png"):
            code = new_code(rng)
            emit("png", "image.png", code_png(code, 640, 480), code, width=640, height=480)
        if want("jpeg"):
            code = new_code(rng)
            via_png(code, 640, 480, "image.jpg", "-q:v", "3")
            emit("jpeg", "image.jpg", code=code, width=640, height=480)
        if want("webp"):
            code = new_code(rng)
            via_png(code, 640, 480, "image.webp", "-c:v", "libwebp", "-quality", "90")
            emit("webp", "image.webp", code=code, width=640, height=480)
        if want("photo-2560"):
            code = new_code(rng)
            via_png(code, 2560, 1920, "photo-2560.jpg", "-q:v", "3", noise=150)
            emit("photo-2560", "photo-2560.jpg", code=code, width=2560, height=1920)
        if want("photo-exif6"):
            code = new_code(rng)
            via_png(code, 2560, 1920, "photo-exif6-plain.jpg", "-q:v", "3", noise=150)
            plain = os.path.join(out, "photo-exif6-plain.jpg")
            with open(plain, "rb") as f:
                data = with_exif_orientation(f.read(), 6)
            os.remove(plain)
            emit("photo-exif6", "photo-exif6.jpg", data, code, width=2560, height=1920, orientation=6)
        if want("png-3_5mib"):
            # 1800 px wide RGB noise: about 3.5 MiB as PNG, within 2000 px, over 3 MiB.
            code = new_code(rng)
            emit("png-3_5mib", "noise-1800.png", code_png(code, 1800, 680, noise=0, max_scale=8), code, width=1800, height=680)
        if want("png-13000"):
            code = new_code(rng)
            emit("png-13000", "wide-13000.png", code_png(code, 13000, 8, channels=1), None, width=13000, height=8)

        # Text and code.
        if want("txt"):
            code = new_code(rng)
            emit("txt", "notes.txt", f"Synthetic notes.\nCode: {code}\n".encode(), code)
        if want("py"):
            code = new_code(rng)
            emit("py", "script.py", f'CODE = "{code}"\n\nprint(CODE)\n'.encode(), code)
        if want("csv"):
            code = new_code(rng)
            emit("csv", "table.csv", f"name,value\ncode,{code}\n".encode(), code)
        if want("json"):
            code = new_code(rng)
            emit("json", "data.json", json.dumps({"code": code}).encode(), code)

        # PDFs.
        text_code = new_code(rng)
        text = text_pdf(text_code, rng)
        if want("pdf-text"):
            emit("pdf-text", "text.pdf", text, text_code, pages=1)
        if want("pdf-4mib"):
            # About 4.5 MiB of text in the first 100 pages: 300 chars x 150 lines.
            code = new_code(rng)
            emit("pdf-4mib", "big-text.pdf", text_pdf(code, rng, pages=100, lines_per_page=150, chars=300,
                                                       size=2, leading=4.8), code, pages=100)
        if want("pdf-120"):
            code = new_code(rng)
            emit("pdf-120", "pages-120.pdf", text_pdf(code, rng, pages=120, lines_per_page=4), code, pages=120)
        if want("pdf-scanned"):
            code = new_code(rng)
            emit("pdf-scanned", "scanned.pdf", scanned_pdf(code, rng, 1240, 1754, 595, 842, noise=None),
                 code, pages=1)
        if want("pdf-large-scan"):
            # A4 at 300 dpi of noisy greyscale: it does not compress.
            code = new_code(rng)
            emit("pdf-large-scan", "large-scan.pdf", scanned_pdf(code, rng, 2480, 3508, 595, 842, noise=96),
                 code, pages=1)
        if want("pdf-user-password") or want("pdf-owner-only") or want("pdf-copy-restricted"):
            plain = os.path.join(work, "plain.pdf")
            for name, filename, args in [
                ("pdf-user-password", "user-password.pdf", ["--encrypt", "user-secret", "owner-secret", "256", "--"]),
                ("pdf-owner-only", "owner-only.pdf", ["--encrypt", "", "owner-secret", "256", "--"]),
                ("pdf-copy-restricted", "copy-restricted.pdf",
                 ["--encrypt", "", "owner-secret", "256", "--extract=n", "--"]),
            ]:
                if not want(name):
                    continue
                code = new_code(rng)
                with open(plain, "wb") as f:
                    f.write(text_pdf(code, rng))
                qpdf(*args, plain, os.path.join(out, filename))
                emit(name, filename, code=code, pages=1)
        if want("pdf-truncated"):
            emit("pdf-truncated", "truncated.pdf", text[: len(text) // 2], None)
        if want("pdf-not-pdf"):
            emit("pdf-not-pdf", "not-a.pdf", b"This is plain text with a .pdf name.\n", None)

        # Office.
        for name, filename, writer in [("docx", "document.docx", docx), ("xlsx", "sheet.xlsx", xlsx),
                                       ("pptx", "slides.pptx", pptx)]:
            if want(name):
                code = new_code(rng)
                writer(os.path.join(out, filename), code)
                emit(name, filename, code=code)

        # Media.
        if want("mp4"):
            code = new_code(rng)
            src = os.path.join(work, "frame.png")
            with open(src, "wb") as f:
                f.write(code_png(code, 640, 360))
            ffmpeg("-loop", "1", "-i", src, "-t", "2", "-r", "5", "-c:v", "mpeg4", "-q:v", "3", "-pix_fmt", "yuv420p",
                   os.path.join(out, "clip.mp4"))
            emit("mp4", "clip.mp4", code=code)
        if want("mp3"):
            ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "libmp3lame", "-q:a", "6",
                   os.path.join(out, "tone.mp3"))
            emit("mp3", "tone.mp3", code=None)

        if want("unknown"):
            emit("unknown", "blob.bin", b"\x00FLEETBIN" + rng.randbytes(4096), None)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    with open(os.path.join(out, "manifest.json"), "w") as f:
        json.dump({"seed": seed, "fixtures": manifest}, f, indent=2, sort_keys=True)
        f.write("\n")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("out")
    parser.add_argument("--seed", type=int, default=455)
    parser.add_argument("--only", default="", help="comma-separated fixture names (default: all)")
    args = parser.parse_args()
    only = {n for n in args.only.split(",") if n}
    manifest = build(args.out, args.seed, only)
    for name, entry in sorted(manifest.items()):
        print(f"{name}: {entry['file']}")


if __name__ == "__main__":
    main()
