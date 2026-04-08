#!/usr/bin/env bash
# Download all-MiniLM-L6-v2 ONNX model and vocabulary for fleet-memory ONNX embedding provider.
# Run this before building the Docker image or running fleet-memory locally with Provider=onnx.
#
# Output: src/Fleet.Memory/models/
#   all-MiniLM-L6-v2.onnx         (~80MB, gitignored)
#   all-MiniLM-L6-v2-vocab.txt    (~230KB, committed)

set -euo pipefail

MODELS_DIR="$(cd "$(dirname "$0")/.." && pwd)/models/fleet-memory"
HF_BASE="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main"

mkdir -p "$MODELS_DIR"

echo "Downloading all-MiniLM-L6-v2 ONNX model (~80MB)..."
curl -fL --progress-bar \
  "${HF_BASE}/onnx/model.onnx" \
  -o "${MODELS_DIR}/all-MiniLM-L6-v2.onnx"

echo "Downloading vocabulary file..."
curl -fL --progress-bar \
  "${HF_BASE}/vocab.txt" \
  -o "${MODELS_DIR}/all-MiniLM-L6-v2-vocab.txt"

echo "Done. Files saved to ${MODELS_DIR}/"
ls -lh "${MODELS_DIR}/"
