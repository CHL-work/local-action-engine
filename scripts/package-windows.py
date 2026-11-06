"""Package the Windows Player and an explicit public runtime file allowlist.

Uses only the Python standard library. It never copies .local, a virtual
environment, model weights, credentials, or files merely because Git tracks them.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import zipfile


ROOT = Path(__file__).resolve().parents[1]
FORBIDDEN_PARTS = {".git", ".local", ".worktrees", "node_modules", "library", "temp", "logs"}
FORBIDDEN_SUFFIXES = {".ulf", ".pem", ".key", ".p12", ".pfx", ".safetensors", ".gguf", ".pt",
                      ".onnx", ".weights", ".pth", ".ckpt"}
NON_DISTRIBUTABLE_PLAYER_SUFFIXES = (
    "_burstdebuginformation_donotship",
    "_backupthisfolder_butdontshipitwithyourgame",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def public_file(root: Path, name: str) -> Path:
    relative = PurePosixPath(name)
    if not name or "\\" in name or relative.is_absolute() or ":" in name or ".." in relative.parts:
        raise ValueError(f"Invalid relative public path: {name!r}")
    if any(part.casefold() in FORBIDDEN_PARTS or part.casefold().startswith(".env") for part in relative.parts):
        raise ValueError(f"Private/cache path cannot be packaged: {name}")
    if relative.suffix.lower() in FORBIDDEN_SUFFIXES:
        raise ValueError(f"Model or credential file cannot be packaged: {name}")
    candidate = root.joinpath(*relative.parts)
    # Reject links at every level, including links resolving inside the checkout.
    current = root
    for part in relative.parts:
        current /= part
        if current.is_symlink() or (hasattr(current, "is_junction") and current.is_junction()):
            raise ValueError(f"Links cannot be packaged: {name}")
    if not candidate.resolve().is_relative_to(root.resolve()) or not candidate.is_file():
        raise ValueError(f"Required public file missing or outside checkout: {name}")
    return candidate


def package(root: Path, build: Path, allowlist: Path, output: Path, version: str, commit: str) -> Path:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}", version):
        raise ValueError("Version must be a short filename-safe identifier")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("A full 40-character source commit is required")
    root, build = root.resolve(), build.resolve()
    required = ["BrainwashSandbox.exe", "UnityPlayer.dll", "BrainwashSandbox_Data/globalgamemanagers"]
    for name in required:
        if not (build / name).is_file():
            raise ValueError(f"Incomplete Unity Windows Player: {name}")
    if not (build / "MonoBleedingEdge").is_dir():
        raise ValueError("Incomplete Unity Windows Player: MonoBleedingEdge")
    specification = json.loads(allowlist.read_text(encoding="utf-8"))
    if specification.get("schemaVersion") != 1 or not isinstance(specification.get("files"), list):
        raise ValueError("Runtime allowlist must have schemaVersion 1 and a files array")
    names = specification["files"]
    if not names or not all(isinstance(name, str) for name in names) or len(set(names)) != len(names):
        raise ValueError("Runtime allowlist must contain unique file paths")
    files = {name: public_file(root, name) for name in names}
    excluded = set()
    for item in sorted(build.rglob("*")):
        relative_path = item.relative_to(build)
        relative = relative_path.as_posix()
        excluded_directory = next((index for index, part in enumerate(relative_path.parts)
                                   if part.casefold().endswith(NON_DISTRIBUTABLE_PLAYER_SUFFIXES)), None)
        if excluded_directory is not None:
            excluded.add("unity/Builds/Windows/" + "/".join(relative_path.parts[:excluded_directory + 1]) + "/")
            continue
        if item.is_symlink() or (hasattr(item, "is_junction") and item.is_junction()):
            raise ValueError(f"Links cannot be packaged from the Player: {relative}")
        if item.is_file():
            # Validate Player paths with the same private-file rules as source.
            files[f"unity/Builds/Windows/{relative}"] = public_file(build, relative)
    manifest = {
        "schemaVersion": 1,
        "version": version,
        "sourceCommit": commit,
        "platform": "windows-x86_64",
        "modelWeightsIncluded": False,
        "excludedPaths": sorted(excluded),
        "files": [{"path": name, "bytes": source.stat().st_size, "sha256": sha256(source)}
                  for name, source in sorted(files.items())],
    }
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"local-action-engine-{version}-windows-x64.zip"
    if archive.exists():
        raise FileExistsError(f"Refusing to replace release artifact: {archive}")
    prefix = f"local-action-engine-{version}"
    temporary = archive.with_suffix(".zip.partial")
    if temporary.exists():
        raise FileExistsError(f"Remove the previous incomplete package explicitly: {temporary}")
    with zipfile.ZipFile(temporary, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as package_zip:
        for name, source in sorted(files.items()):
            package_zip.write(source, f"{prefix}/{name}")
        package_zip.writestr(f"{prefix}/release-manifest.json", json.dumps(manifest, indent=2) + "\n")
    temporary.replace(archive)
    (output / f"{archive.name}.sha256").write_text(f"{sha256(archive)}  {archive.name}\n", encoding="ascii")
    (output / f"{archive.stem}.manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return archive


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--build", type=Path)
    parser.add_argument("--allowlist", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit")
    args = parser.parse_args()
    root = args.root.resolve()
    commit = args.commit or subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    result = package(root, args.build or root / "unity/Builds/Windows",
                     args.allowlist or root / "config/windows-runtime-files.json",
                     args.output or root / "dist", args.version, commit)
    print(result)


if __name__ == "__main__":
    main()
