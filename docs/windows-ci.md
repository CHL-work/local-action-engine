# Windows build and release

The pipelines reuse the imported Unity demo, including its original C# executor,
generated models/audio, and five scenario tests. They do not train models, invoke
a teacher service, or evaluate model quality.

## Workflows

| Workflow | Trigger | Result |
| --- | --- | --- |
| `CI` | Pull request, push to `main`, manual dispatch, or release workflow | Locked dependency install and offline tests on Windows 2022 and Ubuntu 24.04; no Unity license or model required |
| `Windows demo release` | A `v*` version tag or manual dispatch | Offline CI, Unity Windows x64 Mono build, actual Windows Player smoke, allowlisted ZIP, SHA-256, and file manifest |
| Draft publication job | Successful version-tag run only | A draft GitHub Release containing the tested ZIP and manifests |

Manual dispatch builds an artifact for the selected branch without creating a
release. A tag run creates a draft for review; publishing the draft is a separate
action. Existing releases/assets are not overwritten on rerun.

All external Actions are pinned to commit SHAs, and the Unity editor image is
pinned to its registry digest. The test/build/package jobs have
read-only repository access. Only the draft-publication job has `contents: write`.
Pull requests never receive Unity credentials or run on a persistent self-hosted
machine.

## One-time Unity activation setup

The hosted builder needs an eligible Unity license. Configure these repository
Actions secrets under **Settings → Secrets and variables → Actions**:

| Secret | Required |
| --- | --- |
| `UNITY_EMAIL` | Unity account used for CI activation |
| `UNITY_PASSWORD` | That account's password |
| `UNITY_LICENSE` | License-file contents for a supported Personal-license activation |
| `UNITY_SERIAL` | Alternative to `UNITY_LICENSE` for a supported paid serial license |

Use the activation method appropriate to the license. See the maintained
[GameCI activation instructions](https://game.ci/docs/github/activation/).
Credentials and license files must remain in Actions secrets, outside source and
release artifacts. The workflow checks required secret presence before starting
the editor and fails explicitly when activation has not been configured.

No credentials are needed to run the packaged Unity Player. The Unity Editor and
its license are build-time requirements only.

The hosted build uses Unity **6000.3.26f1**, the version recorded in
`unity/ProjectSettings/ProjectVersion.txt`, and
`Brainwash.Editor.SandboxBuild.BuildWindows`. That method prepares the scene and
sets the Mono backend. GameCI runs the Linux editor container and cross-builds a
Windows Player; a separate Windows runner executes the result. This is supported
for Mono targets by the [GameCI builder](https://game.ci/docs/github/builder/).
The image `unityci/editor:ubuntu-6000.3.26f1-windows-mono-3` was verified in the
registry on November 6, 2026; its pinned manifest digest is
`sha256:fa196dadbe6f2496beffb8ee3997b9f34e3238579732ee763671a18131784657`.
GameCI streams the editor log into the build step's Actions log. Any additional
files emitted under `unity/Logs` are collected as artifacts if they exist.

## What is tested

The Windows job starts the actual built executable with
`-brainwash-demo-smoke <report.json>`. This mode injects already compiled
directives and exercises all five demo scenarios, the generated-model gallery,
and returning to the menu. It needs neither model weights nor a microphone.

The runner uses Direct3D 11 WARP software rendering, preserving screenshots and
rendering checks instead of using `-nographics`. Automated captures explicitly
render the world camera to a texture through URP; they do not depend on a hidden
Player window presenting a frame. These scene images omit the IMGUI HUD. See the official
[Unity Player command-line reference](https://docs.unity.cn/2023.3/Documentation/Manual/PlayerCommandLineArguments.html).
The wrapper requires an empty output directory and rejects a missing/stale
report, process failure, timeout, any failed check, an omitted scenario, or a
missing/invalid/blank PNG among all ten expected screenshots. Pixel sampling
records color variation in an additional render-evidence JSON file. Logs, JSON checks, and screenshots are uploaded even
when a smoke run fails. A failed smoke run blocks packaging and publication.

These checks establish packaged-game behavior with fixture directives. They do
not establish microphone accuracy, Laya interpretation quality, live GPU inference,
or frame-rate performance on a player's computer.

## Package contents and model assets

`scripts/package-windows.py` copies the full Unity Player directory and only the
runtime files named in `config/windows-runtime-files.json`. Every named file is
required. Source caches, private archives, environments, credentials, and model
weights are excluded; the packager rejects links and paths outside its input
roots. Unity-generated folders ending in `_BurstDebugInformation_DoNotShip` or
`_BackUpThisFolder_ButDontShipItWithYourGame` are excluded and recorded in the
manifest's `excludedPaths`; their original build files remain intact. The ZIP
keeps the relative layout expected by the existing launcher.

The adjacent `*.manifest.json` records the source commit and SHA-256/size of every
payload file. A matching manifest is inside the ZIP. The `*.zip.sha256` file
records the hash of the final ZIP. Packaging does not promise byte-identical Unity
builds across machines.

The release includes `Setup Demo.cmd`, `Play Demo.cmd`, `Stop Demo.cmd`, the
runtime setup scripts, and model manifests. First-run setup retrieves the
separately versioned model assets; it does not train a replacement. Publish and
verify the model artifact named by `models/laya-v3.json` before publishing a demo
release intended for a clean machine. An unavailable model artifact is a setup
failure, not a replay fallback. Runtime prerequisites are documented in the root
README.

## Local equivalent

From the repository root on Windows, after activating the pinned Editor:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
npm test
python -m unittest discover -s tools -p 'test_*.py' -v
python -m unittest discover -s prototype -p 'test_*.py' -v
python -m unittest discover -s research/training -p 'test_*.py' -v
python -m unittest discover -s scripts -p 'test_*.py' -v
./unity/build.ps1 -UnityPath 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe'
./scripts/test-windows-demo.ps1 -OutputDirectory '.local\ci\local-smoke-001'
python scripts/package-windows.py --version dev-local
```

Use a fresh smoke output directory for each run. Add `-SoftwareRendering` to
exercise the hosted runner's WARP configuration. The packager refuses to replace
an existing release archive. No historical checkpoint or report is overwritten.

## Release procedure

1. Merge a reviewed PR after offline CI passes.
2. Confirm the model manifest points to a published, downloadable model archive.
3. Manually run **Windows demo release** on that commit and inspect the Windows
   smoke artifact and ZIP.
4. Create a version tag on the reviewed commit, for example `v0.1.0`, and push it.
5. After the tag workflow passes, inspect and publish its draft GitHub Release.

The GitHub Actions UI only exposes manual workflows after their workflow files
exist on the default branch. A missing activation secret, unavailable editor
image, unsuccessful build, failed Windows smoke, or missing runtime file prevents
a release from being presented as successful.
