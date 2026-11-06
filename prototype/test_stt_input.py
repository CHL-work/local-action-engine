"""Audio boundary tests. Uses only Python standard libraries; no model loads."""
import base64
import importlib.util
import io
from pathlib import Path
import unittest
import wave

spec = importlib.util.spec_from_file_location("stt_worker", Path(__file__).with_name("stt-worker.py"))
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


def audio(channels=1, width=2, rate=16000, seconds=0.1):
    output = io.BytesIO()
    with wave.open(output, "wb") as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(width)
        wav.setframerate(rate)
        wav.writeframes(bytes(round(rate * seconds) * width * channels))
    return base64.b64encode(output.getvalue()).decode("ascii")


class AudioTests(unittest.TestCase):
    def test_valid_mono_keeps_original_duration(self):
        raw, rate, seconds = worker.read_audio(audio())
        self.assertEqual(len(raw), 3200)
        self.assertEqual(rate, 16000)
        self.assertEqual(seconds, 0.1)

    def test_rejects_stereo_and_float_sized_samples(self):
        for args in ({"channels": 2}, {"width": 4}):
            with self.assertRaisesRegex(ValueError, "mono 16-bit"):
                worker.read_audio(audio(**args))

    def test_rejects_long_recordings(self):
        with self.assertRaisesRegex(ValueError, "at most 10 seconds"):
            worker.read_audio(audio(seconds=10.01))

    def test_rejects_truncated_payload(self):
        payload = base64.b64decode(audio())[:-100]
        with self.assertRaisesRegex(ValueError, "Truncated"):
            worker.read_audio(base64.b64encode(payload).decode("ascii"))

    def test_rejects_oversized_base64_before_decode(self):
        with self.assertRaisesRegex(ValueError, "size limit"):
            worker.read_audio("A" * 2_666_673)


if __name__ == "__main__":
    unittest.main()
