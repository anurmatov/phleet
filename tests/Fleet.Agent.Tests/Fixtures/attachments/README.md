# Synthetic attachment fixtures (#455)

No binary is committed here. `generate.py` writes the whole set, plus `manifest.json`, into a
directory you name:

```
python3 generate.py OUT --seed 455
```

`--only png,pdf-text` writes a subset. The same seed always gives the same files and codes.

**Needs:** python3 (stdlib only), `ffmpeg` (JPEG, WebP, mp4, mp3) and `qpdf` (the three
encrypted PDFs). A missing tool stops the script with its name; nothing is skipped.

## What is in it

Every fixture that can carry one embeds a random 6-character code, drawn in a 5x7 bitmap font or
written as text. `manifest.json` maps each fixture name to its file and code. The content is
synthetic: codes, noise and a fixed word list. Only `photo-exif6.jpg` carries EXIF, and its only
tag is Orientation = 6.

| Name | File | Purpose |
|---|---|---|
| `png`, `jpeg`, `webp` | `image.*` | within bounds: sent byte for byte |
| `photo-2560` | `photo-2560.jpg` | Telegram's largest photo size: downscaled to 2000 px |
| `photo-exif6` | `photo-exif6.jpg` | EXIF orientation 6: records what the downscale does with it |
| `png-3_5mib` | `noise-1800.png` | 1800 px, over 3 MiB: re-encoded, never upscaled |
| `png-13000` | `wide-13000.png` | over the decode bound: too large to process |
| `txt`, `py`, `csv`, `json` | | the tool route (`read`) |
| `pdf-text` | `text.pdf` | one page of text |
| `pdf-4mib` | `big-text.pdf` | more than 4 MiB of text in 100 pages: truncated, not failed |
| `pdf-120` | `pages-120.pdf` | 120 pages: pages 1–100 are read |
| `pdf-scanned` | `scanned.pdf` | image only: no text layer |
| `pdf-large-scan` | `large-scan.pdf` | A4 at 300 dpi of noisy greyscale: the render step-down |
| `pdf-user-password` | `user-password.pdf` | needs a user password: password-protected |
| `pdf-owner-only` | `owner-only.pdf` | owner password only, copy allowed: extracted |
| `pdf-copy-restricted` | `copy-restricted.pdf` | copy not allowed: never text-extracted |
| `pdf-truncated` | `truncated.pdf` | the text PDF cut in half: damaged |
| `pdf-not-pdf` | `not-a.pdf` | text with a `.pdf` name: not a valid PDF |
| `docx`, `xlsx`, `pptx` | | minimal valid Office files: the `bash` + `python3` route |
| `mp4` | `clip.mp4` | the code in every frame: the `ffmpeg` frame route |
| `mp3` | `tone.mp3` | a one-second tone, no code |
| `unknown` | `blob.bin` | an unknown binary |

## Who uses it

- `PiAttachmentsRealToolTests` and the attachment rows of `PiExecutorRealPiTests` (CI `pi` job,
  `Category=RealPi`) generate the set into a temporary directory per run.
- The separately approved baseline and canary (#455 §10 B and V) send the same files to throwaway
  agents. Only aggregate pass counts from those runs belong in this repository.
