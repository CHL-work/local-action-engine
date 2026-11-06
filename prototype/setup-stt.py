"""Create an isolated Windows CPU STT environment. Never installs into Laya.

Model source: https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-26
Package wheels and model files are byte-pinned. Existing mismatches are errors,
not an invitation to overwrite another experiment's files.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import urllib.request
import venv

MODEL_ID = "csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-26"
REVISION = "672fbf1b30579d6585301139bb363f42a0ad4a24"
MODEL_DIRECTORY = "zipformer-en-2023-06-26-672fbf1"
MODEL_FILES = {
    "encoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx": "0d072fd4ef956294ba9db9e9a71a541ac70659095ec4934c8453d8b2fe740187",
    "decoder-epoch-99-avg-1-chunk-16-left-64.int8.onnx": "98da299f471e38bb4e1a8df579b8cc9122d6039576a77e357b3c60f17dd83b02",
    "joiner-epoch-99-avg-1-chunk-16-left-64.int8.onnx": "d944208d660d67c8d72cd2acaeac971fa5ceb8c80e76c1968148846fedd6e297",
    "tokens.txt": "49e3c2646595fd907228b3c6787069658f67b17377c60aeb8619c4551b2316fb",
}
PACKAGES = {"sherpa-onnx": "1.13.8", "sherpa-onnx-core": "1.13.8", "numpy": "2.5.4"}
WHEELS = [
    ("sherpa_onnx-1.13.8-cp312-cp312-win_amd64.whl",
     "https://files.pythonhosted.org/packages/24/af/33e9dcaa527bbc0c2526959713626607a46da2d7b19871b7773d8607d356/sherpa_onnx-1.13.8-cp312-cp312-win_amd64.whl",
     "566e49ad3fb2aa8ceb723a52f9809aa6c2ee0a5eb3b0946c3fd2374af807a5c6"),
    ("sherpa_onnx_core-1.13.8-py3-none-win_amd64.whl",
     "https://files.pythonhosted.org/packages/94/38/64356ad97f68fffcf01fe7545407ad18a2ea86c1ffae5b0950f7fac73638/sherpa_onnx_core-1.13.8-py3-none-win_amd64.whl",
     "5579e80196d516e6dae23c8f629292ce3142ab8869925d32b94612fd86f93733"),
    ("numpy-2.5.4-cp312-cp312-win_amd64.whl",
     "https://files.pythonhosted.org/packages/12/33/fefe62073dc8acfd0f2b9ed7c003af2f50aa61555e113e6db02b8f79f145/numpy-2.5.4-cp312-cp312-win_amd64.whl",
     "a7b1b6353e36a7e50de2973a38d705c88ee93adcf120673cee7f45a4a3fa223a"),
]


def sha256(filename):
    with Path(filename).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verified_download(url, target, expected_hash):
    target = Path(target)
    if target.exists():
        if sha256(target) != expected_hash:
            raise RuntimeError(f"Existing file has different bytes; left untouched: {target}")
        print(f"Verified {target.name}", flush=True)
        return
    temporary = target.with_name(target.name + f".download-{os.getpid()}")
    try:
        request = urllib.request.Request(url, headers={"User-Agent": "Brainwash-Voice-Prototype/1"})
        with urllib.request.urlopen(request, timeout=60) as response, temporary.open("xb") as output:
            while chunk := response.read(1024 * 1024):
                output.write(chunk)
        if sha256(temporary) != expected_hash:
            raise RuntimeError(f"Downloaded byte hash mismatch: {target.name}")
        # The process holds setup.lock; an unrelated existing file still wins.
        if target.exists():
            raise RuntimeError(f"Destination appeared during download: {target}")
        temporary.rename(target)
        print(f"Downloaded and verified {target.name}", flush=True)
    finally:
        if temporary.exists():
            temporary.unlink()


def model_manifest():
    return {"version": 1, "engine": "sherpa-onnx", "model": MODEL_ID,
            "revision": REVISION, "files": MODEL_FILES, "packages": PACKAGES,
            "provider": "cpu", "language": "en", "decoding": "greedy_search"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--local-root", required=True, type=Path)
    args = parser.parse_args()
    if sys.version_info[:2] != (3, 12) or sys.platform != "win32" or platform.machine().lower() not in ("amd64", "x86_64"):
        raise RuntimeError("This pinned setup requires 64-bit Windows Python 3.12")
    local = args.local_root.resolve() / ".local"
    environment = local / "prototype-stt-env"
    model = local / "models" / "stt" / MODEL_DIRECTORY
    downloads = local / "prototype-downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    model.mkdir(parents=True, exist_ok=True)
    lock = downloads / "setup.lock"
    # Exclusive creation prevents concurrent installs. Never remove another lock.
    with lock.open("x", encoding="utf-8") as handle:
        handle.write(str(os.getpid()))
    try:
        for filename, url, expected in WHEELS:
            verified_download(url, downloads / filename, expected)
        for filename, expected in MODEL_FILES.items():
            url = f"https://huggingface.co/{MODEL_ID}/resolve/{REVISION}/{filename}"
            verified_download(url, model / filename, expected)
        python = environment / "Scripts" / "python.exe"
        if not python.exists():
            venv.EnvBuilder(with_pip=True).create(environment)
        subprocess.run([str(python), "-m", "pip", "install", "--no-index", "--no-deps",
                        *[str(downloads / filename) for filename, _, _ in WHEELS]], check=True)
        versions = subprocess.check_output([str(python), "-c",
            "import importlib.metadata as m,json; print(json.dumps({p:m.version(p) for p in ['sherpa-onnx','sherpa-onnx-core','numpy']}))"], text=True)
        if json.loads(versions) != PACKAGES:
            raise RuntimeError("Installed package versions differ from the pin")
        manifest = model / "prototype-model.json"
        expected = model_manifest()
        if manifest.exists():
            if json.loads(manifest.read_text(encoding="utf-8")) != expected:
                raise RuntimeError(f"Existing manifest differs; left untouched: {manifest}")
        else:
            with manifest.open("x", encoding="utf-8") as output:
                json.dump(expected, output, indent=2)
                output.write("\n")
        print(json.dumps({"ready": True, "python": str(python), "model": str(model),
                          "trainingEnvironmentModified": False}), flush=True)
    finally:
        lock.unlink()


if __name__ == "__main__":
    main()
