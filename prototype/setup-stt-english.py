"""Install the pinned, full-precision English Parakeet model in isolation.

The previous Zipformer environment/model and all Laya assets remain untouched.
Export: the sherpa-onnx maintainer's export linked by its official model guide.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import platform
import subprocess
import sys
import venv

spec = importlib.util.spec_from_file_location("legacy_stt_setup", Path(__file__).with_name("setup-stt.py"))
legacy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(legacy)
sha256 = legacy.sha256
PACKAGES = legacy.PACKAGES
MODEL_ID = "nvidia/parakeet-unified-en-0.6b"
EXPORT_ID = "csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-non-streaming"
REVISION = "e69c9fb5322532e9995b116b6f1e4b34ab0c4b13"
MODEL_DIRECTORY = "parakeet-unified-en-0.6b-fp32-e69c9fb"
MODEL_FILES = {
    "encoder.onnx": "b96ad329f7190b672655c7c5020e191098705d4a736508012b89ad8ef820315a",
    "encoder.weights": "c054b2932ee13aa39ed84982a399822f3193311fb6fd8e1e204566722614bd87",
    "decoder.onnx": "41136a36f1673de81e1b5182f2aad09791815ec6cc9861ebaf2c56b1a374474f",
    "joiner.onnx": "680ddf13ab1f8a75b8d9f831980124b826dbbf4640f92cdfd30b72d4d2e811a4",
    "tokens.txt": "dc0b4584ab2e4ddbf888425c076c61b736e7356a015250db7d307e6f1a8188ff",
}


def model_manifest():
    return {"version": 2, "engine": "sherpa-onnx", "model": MODEL_ID,
            "displayName": "Parakeet Unified English 0.6B (FP32)",
            "export": EXPORT_ID, "revision": REVISION, "files": MODEL_FILES,
            "packages": PACKAGES, "provider": "cpu", "language": "en",
            "decoding": "greedy_search", "modelType": "nemo_transducer",
            "license": "NVIDIA Open Model License",
            "licenseUrl": "https://huggingface.co/nvidia/parakeet-unified-en-0.6b"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--local-root", required=True, type=Path)
    args = parser.parse_args()
    if sys.version_info[:2] != (3, 12) or sys.platform != "win32" or platform.machine().lower() not in ("amd64", "x86_64"):
        raise RuntimeError("This pinned setup requires 64-bit Windows Python 3.12")
    local = args.local_root.resolve() / ".local"
    environment = local / "prototype-english-stt-env"
    model = local / "models" / "stt" / MODEL_DIRECTORY
    downloads = local / "prototype-downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    model.mkdir(parents=True, exist_ok=True)
    lock = downloads / "english-stt-setup.lock"
    with lock.open("x", encoding="utf-8") as handle:
        handle.write(str(__import__("os").getpid()))
    try:
        for filename, url, expected in legacy.WHEELS:
            legacy.verified_download(url, downloads / filename, expected)
        for filename, expected in MODEL_FILES.items():
            print(f"Preparing {filename} (full model download is about 2.5 GB)", flush=True)
            legacy.verified_download(f"https://huggingface.co/{EXPORT_ID}/resolve/{REVISION}/{filename}", model / filename, expected)
        python = environment / "Scripts" / "python.exe"
        if not python.exists():
            venv.EnvBuilder(with_pip=True).create(environment)
        subprocess.run([str(python), "-m", "pip", "install", "--no-index", "--no-deps",
                        *[str(downloads / filename) for filename, _, _ in legacy.WHEELS]], check=True)
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
