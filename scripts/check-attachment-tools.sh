#!/usr/bin/env bash
# The local tools Pi attachment handling (#455 §4) runs through prlimit: poppler's pdfinfo,
# pdftotext and pdftoppm, ffmpeg and prlimit itself. Every one must run, or the agent image build
# fails; poppler-utils is the distro's current package (no version pin), so its version is printed
# for the build log and the PR. Exit 1 names the first binary that does not run.
set -u

for check in "pdfinfo -v" "pdftotext -v" "pdftoppm -v" "prlimit --version" "ffmpeg -version"; do
  tool=${check%% *}
  if ! command -v "$tool" >/dev/null 2>&1 || ! $check >/dev/null 2>&1; then
    echo "ERROR: $tool does not run; Pi PDF extraction and image downscaling need it (#455)" >&2
    exit 1
  fi
done

version=$(dpkg-query -W -f='${Version}' poppler-utils 2>/dev/null || pdftotext -v 2>&1 | head -n 1)
echo "attachment tools ok: poppler-utils ${version}"
