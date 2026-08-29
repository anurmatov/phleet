import sys
import types
from pathlib import Path

# Make whisper/app.py importable as `app`.
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

# Stub faster_whisper so importing the service never pulls in the real model
# runtime. The tests drive a recording double in place of the loaded model.
if "faster_whisper" not in sys.modules:
    stub = types.ModuleType("faster_whisper")

    class WhisperModel:  # noqa: D401 - stand-in for the real class
        def __init__(self, *args, **kwargs):
            raise AssertionError("tests must not construct a real WhisperModel")

    stub.WhisperModel = WhisperModel
    sys.modules["faster_whisper"] = stub
