"""Install pinned local inference assets; never trains or evaluates a model."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import platform
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import venv
import zipfile


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def safe_path(root, relative):
    # Manifests and ZIPs use portable POSIX paths, never drives or backslashes.
    value = PurePosixPath(relative)
    if not relative or '\\' in relative or ':' in relative or value.is_absolute() or '..' in value.parts:
        raise ValueError(f'Unsafe relative path: {relative}')
    resolved = (Path(root) / value).resolve()
    if not resolved.is_relative_to(Path(root).resolve()) or resolved == Path(root).resolve():
        raise ValueError(f'Path escapes its root: {relative}')
    return resolved


def verify_files(directory, manifest):
    for name, expected in manifest['files'].items():
        file = safe_path(directory, name)
        if not file.is_file() or file.stat().st_size != expected['bytes'] or sha256(file) != expected['sha256']:
            raise RuntimeError(f'Missing or mismatched model file: {name}')


def download(url, target, expected):
    target = Path(target)
    if target.exists():
        if sha256(target) != expected:
            raise RuntimeError(f'Existing download has different bytes; left untouched: {target}')
        return
    if not url.startswith('https://'):
        raise ValueError('Downloads must use HTTPS')
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + f'.download-{os.getpid()}')
    try:
        request = urllib.request.Request(url, headers={'User-Agent': 'Local-Action-Engine/0.1'})
        with urllib.request.urlopen(request, timeout=60) as source, temporary.open('xb') as output:
            shutil.copyfileobj(source, output, 1024 * 1024)
        if sha256(temporary) != expected:
            raise RuntimeError(f'Download checksum mismatch: {target.name}')
        if target.exists():
            raise RuntimeError(f'Destination appeared during download: {target}')
        temporary.rename(target)
    finally:
        if temporary.exists():
            temporary.unlink()


def install_model(archive, destination, manifest):
    archive, destination = Path(archive), Path(destination)
    if sha256(archive) != manifest['archive']['sha256']:
        raise RuntimeError('Model archive checksum mismatch')
    if destination.exists():
        verify_files(destination, manifest)
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='model-verify-', dir=destination.parent) as staging:
        stage = Path(staging) / 'checkpoint'
        stage.mkdir()
        with zipfile.ZipFile(archive) as package:
            entries = package.infolist()
            names = [entry.filename for entry in entries]
            if len(names) != len(set(names)) or set(names) != set(manifest['files']):
                raise RuntimeError('Model archive does not match the exact file allowlist')
            for entry in entries:
                target = safe_path(stage, entry.filename)
                expected = manifest['files'][entry.filename]
                if entry.is_dir() or entry.file_size != expected['bytes']:
                    raise RuntimeError(f'Unexpected archive entry: {entry.filename}')
                target.parent.mkdir(parents=True, exist_ok=True)
                with package.open(entry) as source, target.open('xb') as output:
                    shutil.copyfileobj(source, output, 1024 * 1024)
        verify_files(stage, manifest)
        stage.rename(destination)


def run(*arguments):
    subprocess.run([str(value) for value in arguments], check=True)


def verify_package_versions(python, pins):
    # Metadata only: do not initialize CUDA, load weights, or import model code.
    script = '''import importlib.metadata as metadata
import json
import sys
pins = json.loads(sys.argv[1])
bad = {}
for package, expected in pins.items():
    try:
        actual = metadata.version(package)
    except metadata.PackageNotFoundError:
        actual = None
    if actual != expected:
        bad[package] = {"expected": expected, "actual": actual}
print(json.dumps(bad))
sys.exit(bool(bad))
'''
    run(python, '-c', script, json.dumps(pins))


def verify_environment(python, requirements):
    pins = dict(line.strip().split('==', 1) for line in requirements.read_text().splitlines()
                if line.strip() and not line.lstrip().startswith('#'))
    verify_package_versions(python, pins)
    run(python, '-m', 'pip', 'check')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--model-archive', type=Path)
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    if sys.version_info[:2] != (3, 12) or sys.platform != 'win32' or platform.machine().lower() not in ('amd64', 'x86_64'):
        raise RuntimeError('Setup requires Python 3.12 x64 on Windows')
    root = args.root.resolve()
    manifest = json.loads((root / 'models/laya-v3.json').read_text(encoding='utf-8'))
    destination = safe_path(root, manifest['localDirectory'])
    local = root / '.local'
    local.mkdir(exist_ok=True)
    lock = local / 'runtime-setup.lock'
    # Do not silently break another setup's lock or modify its virtual environment.
    with lock.open('x') as output:
        output.write(str(os.getpid()))
    try:
        python = local / 'laya-env/Scripts/python.exe'
        requirements = root / 'requirements/laya-win-cu128.txt'
        if not args.verify_only:
            if destination.exists():
                verify_files(destination, manifest)
            else:
                archive = args.model_archive
                if archive is None:
                    archive = local / 'downloads' / manifest['archive']['filename']
                    print('Downloading the original English checkpoint (~846 MB)...', flush=True)
                    download(manifest['archive']['url'], archive, manifest['archive']['sha256'])
                install_model(archive, destination, manifest)
            if not python.exists():
                venv.EnvBuilder(with_pip=True).create(python.parent.parent)
            print('Installing the pinned Laya/CUDA Python environment (several GB)...', flush=True)
            run(python, '-m', 'pip', 'install', '--disable-pip-version-check',
                '--extra-index-url', 'https://download.pytorch.org/whl/cu128', '-r', requirements)
            run(sys.executable, root / 'prototype/setup-stt-english.py', '--local-root', root)
        verify_files(destination, manifest)
        verify_environment(python, requirements)
        stt_setup = root / 'prototype/setup-stt-english.py'
        spec = importlib.util.spec_from_file_location('stt_setup', stt_setup)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        stt = local / 'models/stt' / module.MODEL_DIRECTORY
        for name, expected in module.MODEL_FILES.items():
            if not (stt / name).is_file() or sha256(stt / name) != expected:
                raise RuntimeError(f'STT model mismatch: {name}')
        if json.loads((stt / 'prototype-model.json').read_text(encoding='utf-8')) != module.model_manifest():
            raise RuntimeError('STT installation manifest differs from the pin')
        stt_python = local / 'prototype-english-stt-env/Scripts/python.exe'
        verify_package_versions(stt_python, module.PACKAGES)
        run(stt_python, '-m', 'pip', 'check')
        run(stt_python, '-c', 'import sherpa_onnx,numpy; print("STT imports verified")')
        print('Runtime files and package versions verified. No model inference was performed.', flush=True)
    finally:
        lock.unlink()


if __name__ == '__main__':
    main()
