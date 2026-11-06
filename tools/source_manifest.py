"""Record selected imported file identities against an explicit source snapshot."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--source-commit', required=True)
    args = parser.parse_args()
    if len(args.source_commit) != 40 or any(c not in '0123456789abcdef' for c in args.source_commit):
        raise ValueError('Supply the full original commit SHA')
    paths = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '-co', '--exclude-standard', '-z'])
    source_paths = {'requirements/laya-win-cu128.txt': 'eval/laya-requirements.lock.txt',
                    'research/training/train-approved.py': 'training/train-approved.py',
                    'research/training/test_train_approved.py': 'training/test_train_approved.py'}
    imported, authored = [], []
    for name in sorted(set(paths.decode().split('\0')) - {''}):
        if name.startswith('provenance/'):
            continue
        destination = ROOT / name
        original_name = source_paths.get(name, name)
        source = args.snapshot / original_name
        if not destination.is_file():
            continue
        if source.is_file():
            original = source.read_bytes()
            imported.append({'path': name, 'sourcePath': original_name,
                             'sourceGitBlob': hashlib.sha1(b'blob ' + str(len(original)).encode() + b'\0' + original).hexdigest(),
                             'sourceSha256': hashlib.sha256(original).hexdigest(),
                             'publishedSha256': sha256(destination),
                             'adapted': original != destination.read_bytes()})
        else:
            authored.append(name)
    manifest = {'schemaVersion': 1, 'sourceRepository': 'https://github.com/CHL-work/project-brainwash',
                'sourceCommit': args.source_commit,
                'description': 'Selected engineering extraction with fresh Git history. Source repository remains private. Adapted files are identified explicitly.',
                'excluded': ['private Git history', 'story/worldbuilding/character design documents',
                             'raw training datasets and teacher transcripts', 'campaign supervisors and provider/account state',
                             '.local caches, environments, optimizer states and unrelated checkpoints',
                             'Chappy persona/lore fixtures and unrelated model experiments'],
                'imported': imported, 'newFiles': authored}
    output = ROOT / 'provenance/source-manifest.json'
    output.parent.mkdir(exist_ok=True)
    output.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(f'Recorded {len(imported)} imported files and {len(authored)} new files')


if __name__ == '__main__':
    main()
