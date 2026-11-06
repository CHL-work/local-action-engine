"""CPU-only speech worker: one recognition stream per released prototype cast.

Audio stays in memory. Token timestamps are diagnostic starts, not verified
word-end boundaries. This one-listener harness does not implement late joins.
"""
import base64
import contextlib
import importlib.metadata
import importlib.util
import io
import json
from pathlib import Path
import sys
import time
import wave

MAX_AUDIO_BYTES = 2_000_000
MAX_AUDIO_SECONDS = 10


def read_audio(encoded):
    if not isinstance(encoded, str) or len(encoded) > 2_666_672:
        raise ValueError("Audio request exceeds its size limit")
    audio = base64.b64decode(encoded, validate=True)
    if not 44 <= len(audio) <= MAX_AUDIO_BYTES:
        raise ValueError("Audio must be a bounded PCM WAV")
    with wave.open(io.BytesIO(audio), "rb") as stream:
        if stream.getnchannels() != 1 or stream.getsampwidth() != 2 or stream.getcomptype() != "NONE":
            raise ValueError("Expected mono 16-bit PCM WAV")
        rate, frames = stream.getframerate(), stream.getnframes()
        if not 8000 <= rate <= 96000 or not 0 < frames <= rate * MAX_AUDIO_SECONDS:
            raise ValueError("Expected 8–96 kHz audio of at most 10 seconds")
        samples = stream.readframes(frames)
        if len(samples) != frames * 2:
            raise ValueError("Truncated WAV data")
    return samples, rate, frames / rate


def emit(value):
    print(json.dumps(value, ensure_ascii=False, allow_nan=False), flush=True)


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    model = Path(sys.argv[1]).resolve()
    manifest = json.loads((model / "prototype-model.json").read_text(encoding="utf-8"))
    english = manifest.get("model") == "nvidia/parakeet-unified-en-0.6b"
    setup_file = "setup-stt-english.py" if english else "setup-stt.py"
    spec = importlib.util.spec_from_file_location("prototype_stt_setup", Path(__file__).with_name(setup_file))
    setup = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(setup)
    if manifest != setup.model_manifest():
        raise RuntimeError("Unexpected STT manifest; run the pinned prototype setup")
    for filename, expected in setup.MODEL_FILES.items():
        if setup.sha256(model / filename) != expected:
            raise RuntimeError(f"STT model byte hash mismatch: {filename}")
    versions = {package: importlib.metadata.version(package) for package in setup.PACKAGES}
    if versions != setup.PACKAGES:
        raise RuntimeError("Unexpected STT package version; run the pinned prototype setup")
    with contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        import sherpa_onnx
        if english:
            recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
                tokens=str(model / "tokens.txt"), encoder=str(model / "encoder.onnx"),
                decoder=str(model / "decoder.onnx"), joiner=str(model / "joiner.onnx"),
                num_threads=4, sample_rate=16000, feature_dim=80,
                decoding_method="greedy_search", provider="cpu", model_type="nemo_transducer",
            )
            # Initialize kernels before announcing ready; never reuse this stream.
            warmup = recognizer.create_stream()
            warmup.accept_waveform(16000, np.zeros(16000, dtype=np.float32))
            recognizer.decode_stream(warmup)
        else:
            recognizer = sherpa_onnx.OnlineRecognizer.from_transducer(
                tokens=str(model / "tokens.txt"),
                encoder=str(model / "encoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx"),
                decoder=str(model / "decoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx"),
                joiner=str(model / "joiner-epoch-99-avg-1-chunk-16-left-64.int8.onnx"),
                num_threads=2, sample_rate=16000, feature_dim=80,
                decoding_method="greedy_search", enable_endpoint_detection=False, provider="cpu",
            )
    emit({"ready": {**manifest, "version": versions["sherpa-onnx"], "threads": 4 if english else 2,
                    "path": str(model), "timestampMeaning": "token starts; not word end boundaries"}})
    for line in sys.stdin:
        request = None
        try:
            request = json.loads(line)
            raw, rate, seconds = read_audio(request["audio"])
            started = time.perf_counter()
            with contextlib.redirect_stdout(sys.stderr):
                samples = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
                if not np.any(samples):
                    result_text, tokens, timestamps = "", [], []
                else:
                    stream = recognizer.create_stream()
                    stream.accept_waveform(rate, samples)
                    if english:
                        recognizer.decode_stream(stream)
                        result = stream.result
                    else:
                        stream.accept_waveform(rate, np.zeros(round(rate * 0.66), dtype=np.float32))
                        stream.input_finished()
                        while recognizer.is_ready(stream):
                            recognizer.decode_stream(stream)
                        result = recognizer.get_result_all(stream)
                    result_text, tokens, timestamps = result.text.strip(), list(result.tokens), list(result.timestamps)
            emit({"id": request["id"], "text": result_text, "tokens": tokens,
                  "timestamps": timestamps, "audioSeconds": seconds,
                  "sttMs": (time.perf_counter() - started) * 1000})
        except Exception as error:
            emit({"id": request.get("id") if isinstance(request, dict) else None,
                  "error": f"{type(error).__name__}: {error}"})


if __name__ == "__main__":
    main()
