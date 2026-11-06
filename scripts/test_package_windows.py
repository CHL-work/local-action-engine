"""Release-boundary tests; no Unity, model, or network access."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("package_windows", Path(__file__).with_name("package-windows.py"))
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.build = self.root / "build"
        for name in ["BrainwashSandbox.exe", "UnityPlayer.dll", "BrainwashSandbox_Data/globalgamemanagers", "MonoBleedingEdge/EmbedRuntime/mono.dll"]:
            target = self.build / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b"fixture-only")
        (self.root / "README.md").write_text("Public instructions", encoding="utf-8")
        self.allowlist = self.root / "files.json"
        self.set_files(["README.md"])

    def set_files(self, files):
        self.allowlist.write_text(json.dumps({"schemaVersion": 1, "files": files}), encoding="utf-8")

    def package(self):
        return packager.package(self.root, self.build, self.allowlist, self.root / "out", "v1.0.0", "a" * 40)

    def test_allowlist_excludes_private_files_and_hashes_every_payload(self):
        (self.root / ".local").mkdir()
        (self.root / ".local/private.txt").write_text("never release")
        archive = self.package()
        with zipfile.ZipFile(archive) as zipped:
            names = zipped.namelist()
            self.assertFalse(any("private" in name for name in names))
            manifest = json.loads(zipped.read("local-action-engine-v1.0.0/release-manifest.json"))
            self.assertEqual(len(manifest["files"]), len(names) - 1)
            self.assertFalse(manifest["modelWeightsIncluded"])
            for entry in manifest["files"]:
                payload = zipped.read("local-action-engine-v1.0.0/" + entry["path"])
                self.assertEqual(packager.hashlib.sha256(payload).hexdigest(), entry["sha256"])

    def test_incomplete_player_is_not_a_release(self):
        (self.build / "UnityPlayer.dll").unlink()
        with self.assertRaisesRegex(ValueError, "Incomplete Unity Windows Player"):
            self.package()

    def test_missing_required_runtime_is_not_a_release(self):
        self.set_files(["missing-setup.ps1"])
        with self.assertRaisesRegex(ValueError, "Required public file missing"):
            self.package()

    def test_traversal_private_and_model_paths_are_rejected(self):
        for name in ["../secret", ".local/file", "C:/secret", "config/.env", "model.safetensors", "Unity_lic.ulf"]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.set_files([name])
                self.package()

    def test_existing_release_is_not_overwritten(self):
        self.package()
        with self.assertRaises(FileExistsError):
            self.package()

    def test_private_paths_are_case_insensitive(self):
        for name in [".LOCAL/secret.txt", "config/.ENV", "NODE_MODULES/pkg.js", "LIBRARY/cache.asset"]:
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "Private/cache path"):
                self.set_files([name])
                self.package()

    def test_all_model_formats_are_rejected_in_runtime_and_player(self):
        for suffix in [".safetensors", ".gguf", ".pt", ".onnx", ".weights", ".pth", ".ckpt", ".ONNX"]:
            name = "model" + suffix
            with self.subTest(source="runtime", suffix=suffix), self.assertRaisesRegex(ValueError, "Model or credential"):
                self.set_files([name])
                self.package()
            self.set_files(["README.md"])
            model = self.build / name
            model.write_bytes(b"must not be packaged")
            try:
                with self.subTest(source="player", suffix=suffix), self.assertRaisesRegex(ValueError, "Model or credential"):
                    self.package()
            finally:
                model.unlink()

    def test_unity_debug_and_backup_folders_are_excluded_without_deletion(self):
        private_outputs = [
            "Brainwash 3D Sandbox_BurstDebugInformation_DoNotShip/generated-symbols.txt",
            "BrainwashSandbox_BackUpThisFolder_ButDontShipItWithYourGame/il2cppOutput/generated.cpp",
        ]
        for name in private_outputs:
            target = self.build / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("Unity marks this as not distributable", encoding="utf-8")
        archive = self.package()
        with zipfile.ZipFile(archive) as zipped:
            self.assertFalse(any("DoNotShip" in name or "ButDontShipItWithYourGame" in name for name in zipped.namelist()))
            manifest = json.loads(zipped.read("local-action-engine-v1.0.0/release-manifest.json"))
            self.assertEqual(manifest["excludedPaths"], [
                "unity/Builds/Windows/Brainwash 3D Sandbox_BurstDebugInformation_DoNotShip/",
                "unity/Builds/Windows/BrainwashSandbox_BackUpThisFolder_ButDontShipItWithYourGame/",
            ])
        self.assertTrue(all((self.build / name).is_file() for name in private_outputs))


if __name__ == "__main__":
    unittest.main()
