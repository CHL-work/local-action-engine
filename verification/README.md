# Extraction verification

This directory records checks performed for the public Windows extraction on
November 6, 2026. It is separate from historical model training measurements.

- Unity 6000.3.26f1 built the Windows x64 Mono Player from this checkout.
- `windows-demo-smoke.json` records **54 passing packaged-game checks**, covering
  all five scenarios, the model/audio gallery and return to the menu.
- `windows-render-evidence.json` records **10 nonblank 960×640 images** captured
  with Direct3D 11 WARP software rendering. Example images are in `docs/images/`.
- **46 Node tests**, **36 Python tests**, and **39 C# offline probe checks** passed.
- Setup created separate Laya and STT environments, installed the pinned packages,
  and verified model file hashes. Existing verified STT downloads were copied as
  an installation cache; this was not a new internet download of every asset.
- The local service loaded the original Laya checkpoint and Parakeet. A single
  typed `Dance` functional request returned HTTP 200 and a validated `pose/dance`
  directive. This establishes the live interpreter/compiler path on the test PC;
  it is not a model-quality benchmark, microphone test, or new training run.
- An isolated copy of exactly the runtime allowlist passed a clean npm install
  and loaded the service, worker manager and inference modules.
- Workflow syntax passed actionlint. Hosted Unity activation/build remains a
  separate check requiring repository Actions secrets.

## Changes needed for automated Windows execution

The inherited hidden-window smoke initially ran at approximately 3,528 FPS.
Three scenario checks failed, and screen-capture images were black. The final
smoke requests 60 FPS with VSync disabled, preserving its original assertions,
control durations and deadlines. It averaged approximately 56 FPS on this PC.

Flight obstruction detection used a fixed minimum movement per frame, which
falsely treated clear movement at very high frame rates as blocked. It now scales
the movement threshold with requested speed and frame time, matching the other
movement paths. The nurse scenario passes at normal cadence; its exact low-level
failure at extreme frame rates has not been independently established.

An automation-only URP camera render request captures each scene into a texture
without requiring a presented window frame. These images omit the IMGUI HUD.
The wrapper requires all ten PNGs and checks pixel variation to reject black or
uniform captures. Normal gameplay rendering is unchanged.

The reports use injected compiled directives and do not claim recognition/model
accuracy. See `docs/model-card.md` for the historical classifier evidence and
remaining hardware/language limitations.
