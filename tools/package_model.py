"""Package only the preserved demo checkpoint and its notices, without training."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]
WEIGHTS = 'dc8cd7f6928a278b8297e918e9ca79791ffb406eeab3c48183441b9ba29a32cb'
NAMES = ('brainwash-training.json', 'rl_agent_config.json', 'model.safetensors',
         'encoder/config.json', 'tokenizer/tokenizer.json', 'tokenizer/tokenizer_config.json')


def sha256(file):
    with file.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--manifest', type=Path, default=ROOT / 'models/laya-v3.json')
    args = parser.parse_args()
    files = {name: args.checkpoint / name for name in NAMES}
    files.update({'LICENSE': ROOT / 'LICENSE', 'NOTICE': ROOT / 'models/NOTICE',
                  'MODEL_CARD.md': ROOT / 'docs/model-card.md'})
    identities = {name: {'bytes': path.stat().st_size, 'sha256': sha256(path)} for name, path in files.items()}
    if identities['model.safetensors']['sha256'] != WEIGHTS:
        raise RuntimeError('Only the preserved original English checkpoint may be packaged')
    args.output.mkdir(parents=True, exist_ok=True)
    archive = args.output / 'laya-english-v3-epoch04.zip'
    temporary = archive.with_suffix('.zip.partial')
    if archive.exists() or temporary.exists() or args.manifest.exists():
        raise FileExistsError('Archive/manifest exists; create a separately versioned destination')
    with zipfile.ZipFile(temporary, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        for name, file in sorted(files.items()):
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 13, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with file.open('rb') as source, output.open(info, 'w', force_zip64=True) as destination:
                shutil.copyfileobj(source, destination, 1024 * 1024)
    temporary.rename(archive)
    digest = sha256(archive)
    manifest = {
        'schemaVersion': 1, 'model': 'original-english-protocol3-epoch04',
        'base': {'repository': 'convaiinnovations/laya', 'revision': '7b928d828b7b0e022f929d9bd2e44165aa270148'},
        'license': 'Apache-2.0',
        'localDirectory': '.local/models/brainwash-en-v3-opus55-2026-10-13/epoch-04',
        'archive': {'filename': archive.name, 'bytes': archive.stat().st_size, 'sha256': digest,
                    'url': 'https://github.com/CHL-work/local-action-engine/releases/download/models-v1/' + archive.name},
        'files': identities,
    }
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    with args.manifest.open('x', encoding='utf-8') as output:
        json.dump(manifest, output, indent=2)
        output.write('\n')
    (args.output / (archive.name + '.sha256')).write_text(f'{digest}  {archive.name}\n', encoding='ascii')
    print(json.dumps({'archive': str(archive), 'bytes': archive.stat().st_size, 'sha256': digest}))


if __name__ == '__main__':
    main()
