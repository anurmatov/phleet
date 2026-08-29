import asyncio
import importlib
import sys

import pytest

# Synthetic terms only — never commit real project or product vocabulary.
GLOSSARY = "Zyntharo, Quillbase"


class FakeSegment:
    def __init__(self, text, avg_logprob):
        self.text = text
        self.avg_logprob = avg_logprob


class FakeInfo:
    def __init__(self, language, language_probability):
        self.language = language
        self.language_probability = language_probability


class RecordingModel:
    """Stands in for a loaded WhisperModel and records every decode call."""

    def __init__(self, language_probability):
        self._language_probability = language_probability
        self.calls = []

    def transcribe(self, path, **kwargs):
        self.calls.append(kwargs)
        segments = [FakeSegment("hello", -0.2)]
        return iter(segments), FakeInfo("en", self._language_probability)


class FakeUpload:
    async def read(self):
        return b"fake-audio-bytes"


def load_app(monkeypatch, hotwords_env):
    """Import whisper/app.py fresh with WHISPER_HOTWORDS set to the given value."""
    if hotwords_env is None:
        monkeypatch.delenv("WHISPER_HOTWORDS", raising=False)
    else:
        monkeypatch.setenv("WHISPER_HOTWORDS", hotwords_env)

    sys.modules.pop("app", None)
    app_module = importlib.import_module("app")

    # ffmpeg never runs in tests; the endpoint's cleanup tolerates a missing file.
    monkeypatch.setattr(app_module.subprocess, "run", lambda *a, **kw: None)
    return app_module


def run_transcribe(app_module, language_probability):
    """Drive the endpoint with a recording model and return that model."""
    recorder = RecordingModel(language_probability)
    app_module.model = recorder
    asyncio.run(app_module.transcribe(FakeUpload()))
    return recorder


# ── resolve_hotwords ─────────────────────────────────────────────────────────


@pytest.mark.parametrize("raw", [None, "", "   ", "\t\n "])
def test_resolve_hotwords_treats_blank_input_as_disabled(monkeypatch, raw):
    app_module = load_app(monkeypatch, None)
    assert app_module.resolve_hotwords(raw) is None


def test_resolve_hotwords_strips_surrounding_whitespace(monkeypatch):
    app_module = load_app(monkeypatch, None)
    assert app_module.resolve_hotwords(f"  {GLOSSARY}  ") == GLOSSARY


# ── disabled path: decode arguments unchanged ────────────────────────────────


@pytest.mark.parametrize("hotwords_env", [None, "", "   "])
@pytest.mark.parametrize("language_probability", [0.9, 0.1])
def test_blank_config_preserves_decode_arguments(
    monkeypatch, hotwords_env, language_probability
):
    app_module = load_app(monkeypatch, hotwords_env)
    assert app_module.HOTWORDS is None
    assert app_module.HOTWORD_KWARGS == {}

    recorder = run_transcribe(app_module, language_probability)

    assert recorder.calls, "expected at least one decode call"
    for call in recorder.calls:
        assert "hotwords" not in call
        assert call["beam_size"] == app_module.BEAM_SIZE


# ── configured path: value reaches every decode call ─────────────────────────


def test_configured_value_passed_to_automatic_language_call(monkeypatch):
    app_module = load_app(monkeypatch, GLOSSARY)

    # language_probability above the threshold: only the automatic pass runs.
    recorder = run_transcribe(app_module, 0.9)

    assert len(recorder.calls) == 1
    assert recorder.calls[0]["hotwords"] == GLOSSARY
    assert "language" not in recorder.calls[0]


def test_configured_value_passed_to_each_forced_language_call(monkeypatch):
    app_module = load_app(monkeypatch, GLOSSARY)

    # language_probability below the threshold: automatic pass + one call per
    # forced language.
    recorder = run_transcribe(app_module, 0.1)

    assert len(recorder.calls) == 1 + len(app_module.FORCED_LANGS)
    assert all(call["hotwords"] == GLOSSARY for call in recorder.calls)

    forced_langs = [call["language"] for call in recorder.calls if "language" in call]
    assert forced_langs == app_module.FORCED_LANGS


def test_configured_value_is_stripped_before_use(monkeypatch):
    app_module = load_app(monkeypatch, f"  {GLOSSARY}  ")

    recorder = run_transcribe(app_module, 0.9)

    assert recorder.calls[0]["hotwords"] == GLOSSARY


# ── the glossary must not leak ───────────────────────────────────────────────


def test_health_does_not_expose_the_glossary(monkeypatch):
    app_module = load_app(monkeypatch, GLOSSARY)

    payload = app_module.health()

    assert payload == {"status": "ok", "model": app_module.WHISPER_MODEL}
    assert GLOSSARY not in repr(payload)


def test_transcribe_response_does_not_echo_the_glossary(monkeypatch):
    app_module = load_app(monkeypatch, GLOSSARY)
    app_module.model = RecordingModel(0.9)

    response = asyncio.run(app_module.transcribe(FakeUpload()))

    assert set(response) == {"text", "language"}
    assert GLOSSARY not in repr(response)
