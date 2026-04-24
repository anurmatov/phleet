import os
import tempfile
import subprocess
from contextlib import asynccontextmanager
from fastapi import FastAPI, UploadFile, File
from faster_whisper import WhisperModel

WHISPER_MODEL = os.environ.get("WHISPER_MODEL", "small")
BEAM_SIZE = 5
LANG_PROB_THRESHOLD = 0.5
FORCED_LANGS = ["en", "ru"]

model: WhisperModel = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global model
    model = WhisperModel(WHISPER_MODEL, device="cpu", compute_type="int8")
    yield


app = FastAPI(lifespan=lifespan)


@app.get("/health")
def health():
    return {"status": "ok", "model": WHISPER_MODEL}


@app.post("/transcribe")
async def transcribe(audio: UploadFile = File(...)):
    audio_bytes = await audio.read()

    with tempfile.NamedTemporaryFile(suffix=".input", delete=False) as tmp_in:
        tmp_in.write(audio_bytes)
        tmp_in_path = tmp_in.name

    tmp_wav_path = tmp_in_path + ".wav"
    try:
        subprocess.run(
            ["ffmpeg", "-y", "-i", tmp_in_path, "-ar", "16000", "-ac", "1", tmp_wav_path],
            check=True,
            capture_output=True,
        )

        segments, info = model.transcribe(tmp_wav_path, beam_size=BEAM_SIZE)
        segments = list(segments)

        if info.language_probability >= LANG_PROB_THRESHOLD:
            text = " ".join(s.text for s in segments).strip()
            return {"text": text, "language": info.language}

        best_text = ""
        best_score = float("-inf")
        best_lang = FORCED_LANGS[0]

        for lang in FORCED_LANGS:
            forced_segments, _ = model.transcribe(tmp_wav_path, beam_size=BEAM_SIZE, language=lang)
            forced_segments = list(forced_segments)
            if not forced_segments:
                continue
            avg_logprob = sum(s.avg_logprob for s in forced_segments) / len(forced_segments)
            if avg_logprob > best_score:
                best_score = avg_logprob
                best_text = " ".join(s.text for s in forced_segments).strip()
                best_lang = lang

        return {"text": best_text, "language": best_lang}
    finally:
        for path in (tmp_in_path, tmp_wav_path):
            try:
                os.unlink(path)
            except OSError:
                pass
