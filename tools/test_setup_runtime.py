import hashlib
import importlib.metadata
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location('setup_runtime', Path(__file__).with_name('setup_runtime.py'))
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class SetupTests(unittest.TestCase):
    def archive(self, directory, files):
        archive = directory / 'model.zip'
        with zipfile.ZipFile(archive, 'w') as output:
            for name, content in files.items():
                output.writestr(name, content)
        return archive, {'archive': {'sha256': runtime.sha256(archive)}, 'files': {
            name: {'bytes': len(content), 'sha256': hashlib.sha256(content).hexdigest()}
            for name, content in files.items()}}

    def test_verified_install_and_idempotent_reuse(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, manifest = self.archive(root, {'model.bin': b'abc', 'tokenizer/config.json': b'{}'})
            destination = root / 'checkpoint'
            runtime.install_model(archive, destination, manifest)
            runtime.install_model(archive, destination, manifest)
            self.assertEqual((destination / 'model.bin').read_bytes(), b'abc')

    def test_existing_mismatch_is_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, manifest = self.archive(root, {'model.bin': b'abc'})
            destination = root / 'checkpoint'
            destination.mkdir()
            (destination / 'model.bin').write_bytes(b'owner-data')
            with self.assertRaisesRegex(RuntimeError, 'mismatched'):
                runtime.install_model(archive, destination, manifest)
            self.assertEqual((destination / 'model.bin').read_bytes(), b'owner-data')

    def test_archive_hash_checked_before_any_install(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, manifest = self.archive(root, {'model.bin': b'abc'})
            manifest['archive']['sha256'] = '0' * 64
            with self.assertRaisesRegex(RuntimeError, 'checksum'):
                runtime.install_model(archive, root / 'checkpoint', manifest)
            self.assertFalse((root / 'checkpoint').exists())

    def test_file_hash_failure_leaves_no_partial_checkpoint(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, manifest = self.archive(root, {'model.bin': b'abc'})
            manifest['files']['model.bin']['sha256'] = '0' * 64
            with self.assertRaisesRegex(RuntimeError, 'mismatched'):
                runtime.install_model(archive, root / 'checkpoint', manifest)
            self.assertFalse((root / 'checkpoint').exists())

    def test_rejects_unlisted_entries(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive, manifest = self.archive(root, {'model.bin': b'abc', 'private.txt': b'excluded'})
            del manifest['files']['private.txt']
            with self.assertRaisesRegex(RuntimeError, 'allowlist'):
                runtime.install_model(archive, root / 'checkpoint', manifest)

    def test_path_escape_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            for relative in ('../private', '/root', 'C:/private', 'a\\..\\private', '.'):
                with self.subTest(relative=relative), self.assertRaises(ValueError):
                    runtime.safe_path(temp, relative)

    def test_installed_package_versions_must_match_exactly(self):
        # Exercise a real child interpreter's metadata without model imports.
        installed = importlib.metadata.version('pip')
        runtime.verify_package_versions(sys.executable, {'pip': installed})
        with self.assertRaises(subprocess.CalledProcessError):
            runtime.verify_package_versions(sys.executable, {'pip': '0.0.0-invalid'})

    def test_missing_package_fails_metadata_verification(self):
        with self.assertRaises(subprocess.CalledProcessError):
            runtime.verify_package_versions(sys.executable, {'local-action-engine-missing-test-package': '1'})

    def test_release_manifest_identity(self):
        root = Path(__file__).resolve().parents[1]
        manifest = json.loads((root / 'models/laya-v3.json').read_text())
        self.assertEqual(manifest['files']['model.safetensors']['sha256'],
                         'dc8cd7f6928a278b8297e918e9ca79791ffb406eeab3c48183441b9ba29a32cb')
        self.assertEqual(manifest['archive']['url'],
                         'https://github.com/CHL-work/local-action-engine/releases/download/models-v1/laya-english-v3-epoch04.zip')
        for name in manifest['files']:
            runtime.safe_path(root / '.local', name)


if __name__ == '__main__':
    unittest.main()
