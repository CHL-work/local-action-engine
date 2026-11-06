# Windows runtime setup

Keep the complete extracted Windows ZIP together; moving only the executable
breaks its dependencies. Source checkouts require a Unity build before launch.

## Requirements

- Windows x64 and an NVIDIA driver compatible with PyTorch CUDA 12.8.
- An NVIDIA GPU with **8 GiB free memory** when Laya starts. The original worker
  retains a 4 GiB ongoing guard and has no CPU fallback. These inherited guards
  are not measured minimum card specifications.
- [Node.js](https://nodejs.org/) 22.13+, including npm.
- [Python 3.12 x64](https://www.python.org/downloads/windows/) with pip/venv.
  Setup tries `py -3.12`, then PATH; an explicit path also works.
- Internet for first setup and a writable folder. Allow roughly 15 GB of free
  disk for environments, model files and caches; exact use varies.

The Laya ZIP is approximately 742 MiB; Parakeet files total about 2.5 GB and the
CUDA PyTorch wheel about 2.9 GB. Setup uses `.local` and the normal pip cache.
It installs isolated environments in this folder, never copies a virtualenv
from another checkout, and does not start training or inference.

## First run

Double-click `Setup Demo.cmd`, or:

```powershell
./setup.ps1
# For an unregistered Python installation:
./setup.ps1 -PythonPath 'C:\path\to\Python312\python.exe'
```

Setup installs locked npm dependencies, verifies the original model archive and
every file, creates `.local/laya-env`, and installs pinned Python dependencies.
Speech recognition has a separate CPU environment and byte-pinned wheels/models.
Existing matching downloads are reused; a mismatch stops and preserves the file.

To use a separately transferred Laya archive:

```powershell
./setup.ps1 -ModelArchive 'D:\downloads\laya-english-v3-epoch04.zip'
```

Only the Laya download source changes. Its checksum remains the value in
`models/laya-v3.json`; Python and speech assets still need upstream downloads
or previously verified caches.

```powershell
./setup.ps1 -VerifyOnly
./unity/play.ps1
# Stop persistent workers after closing the Player:
./prototype/start-unity-service.ps1 -Stop
```

Verification checks files and package versions without loading Laya. Live
readiness is a separate condition shown in the Player. Typed commands also use
Laya and require the GPU.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Python/Node missing | Install the required version and reopen the terminal; use `-PythonPath` if needed |
| Model download 404 | The maintainer must publish the `models-v1` release asset; source cannot recreate the trained weights |
| File hash mismatch | Preserve the file for diagnosis; move that specific artifact aside before retrying. Setup never substitutes a checkpoint |
| Setup lock exists | Check for an active setup. Remove `.local/runtime-setup.lock` only after confirming an interrupted setup exited |
| CUDA unavailable or memory guard fails | Update the NVIDIA driver/free GPU memory; do not lower guards to claim hardware support |
| Port 4174 occupied | Stop the other verified service or use `./unity/play.ps1 -Port 4175` |
| Service fails | Inspect `.local/unity/service.log`, `service-error.log`, or `service-from-game.log`; run `./setup.ps1 -VerifyOnly` |
| Confused command | Inspect transcript, proposal and execution separately in the diagnostic panel |

The service retains loopback Host/Origin checks. It is a desktop demo, not an
internet-facing inference service. Logs can contain spoken text; the exported
event log does not include audio.

## Maintainer model artifact

The ZIP contains six unchanged checkpoint files plus LICENSE, NOTICE and
MODEL_CARD.md. `tools/package_model.py` packages an explicitly supplied original
checkpoint and refuses different weights or output overwrite. The manifest
records file and archive hashes. Once the model asset is public, setup needs
neither a private checkout nor GitHub authentication.
