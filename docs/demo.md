# Windows engineering demo

This repository extracts the working Unity prototype from Project Brainwash at source commit `fa39d20`. It keeps the existing five playable scenarios, original test room, key arena, generated models, sound effects, and diagnostic tools. These are demonstrations of the local command pipeline and game integration, not a complete commercial game.

The executable is built with Unity 6000.3.26f1 for Windows x64. The live command path is Unity → loopback Node service → Parakeet speech recognition → fine-tuned Laya → deterministic host validation → Unity execution. Typed commands and preset buttons use the same Laya interpreter as voice after transcription. The source retains its `Brainwash` namespaces and protocol identifiers to preserve the working integration.

Follow the repository [README](../README.md) for release installation and model setup. The Player can display its menu, gallery, and physical world without models; interpreting voice or typed commands requires a ready local service and the original approved checkpoint. Opening a game window alone does not establish that inference is working.

## Try the demo

1. Launch the Windows demo and wait for the local AI service to report ready.
2. Open **Classic test room** for the smallest example. Meet a creature's eyes, hold **B**, say “Dance”, and release **B**. The test panel also offers typed commands and presets.
3. Press **Tab** to inspect the transcript, model proposal, validation/execution outcome, timings, and sight diagnostics. A rejected or expired command should remain visible as a failure instead of silently selecting another target.
4. Use **M** to return to the selection menu and choose a scenario.

| Scenario | ID | Engineering behavior exercised |
| --- | --- | --- |
| Pit Fighter | `pit_arena` | Physics impacts, combat, hazards, carried/thrown bodies, wave state |
| Puppet War | `puppet_war` | Faction relationships, grudges, capability checks, indirect combat |
| The Ledge Key | `ledge_key` | Item identity, dependent actions, carrying, flight, alternate puzzle solutions |
| Rite of Return | `rite_of_return` | Exclusive microphone ownership, STT-only transcription, text matching, interruption |
| Eye Contact Heist | `eye_heist` | Visibility rules, per-actor perception, voice/noise interactions, alarms |

The menu also offers the original test room, original key arena, and **Monster gallery**. The gallery shows the generated models and procedural motion without combat.

## Controls

| Input | Action |
| --- | --- |
| WASD / Shift / Space | Move / run / jump |
| Hold right mouse, or J/L and I/K | Look horizontally and vertically |
| Hold B, then release | Record and submit a voice command |
| V | Toggle voice recording |
| F, or left click while holding right mouse | Staff attack |
| Q | Kick in the five scenarios |
| G / Shift+G | Grab or throw a prop / set it down |
| E | Use an object, door, lever, or item |
| Hold C, then release | Chant in Rite of Return |
| Tab | Toggle the full diagnostic panel |
| M / R | Selection menu / restart |
| N | Toggle sound mute |
| Escape | Cancel and release the mouse cursor |

Input while editing the command text box is reserved for typing. If voice acquisition fails, check service readiness, microphone selection, MP, and the sight diagnostic before interpreting it as a model error.

## Command lifecycle

Every eligible creature meeting the player's eyes can listen during the same recording. A creature joining later receives only its listening interval; losing eye contact submits that interval. Releasing B submits remaining listeners. Each creature receives a separate scene snapshot and request identity.

Listening, thinking, and obeying are distinct states. The service deadline includes queueing, speech recognition, and interpretation. Unity rejects a response after its lease or deadline expires and rechecks live entities before execution. Up to three actions share the demo's fixed five-second control window. These are the preserved prototype rules; this extraction does not implement later private design proposals.

The Node service listens on `127.0.0.1:4174` by default. Microphone audio stays in memory and travels only to the local recognizer. Exported diagnostics and local logs can contain command transcripts. The persistent model service remains running after the Player closes; use the repository's service stop command when finished.

## Automated acceptance

The existing model-free acceptance mode injects already-compiled directives. It exercises the five scenarios, model/audio loading, gallery, and return to the menu:

```powershell
$report = Join-Path $PWD '.local\verification\demo-smoke.json'
& '.\unity\Builds\Windows\BrainwashSandbox.exe' `
    -screen-fullscreen 0 -brainwash-demo-smoke $report
```

The report contains `passed` and individual `checks` with scenario, name, pass/fail, and detail. Screenshots are written beside it. The Player exits with code 0 on success and 1 on failure; a watchdog bounds the run. Run with a graphics device and keep the Player active. This mode does not load Laya, recognize speech, or test a physical microphone.

To open a particular scenario, pass `-brainwash-demo <id>`. Use the IDs in the table above.

The source also retains separate `-brainwash-smoke`, `-brainwash-play-smoke`, and `-brainwash-voice-smoke` integration modes. Those require a live model service; speech modes also require explicit WAV fixtures. Their existence and historical private results are not new validation of this extracted release. Release evidence should identify the build, platform, command, report, and whether real models were involved.

## Scope and limitations

The live Laya worker currently requires NVIDIA CUDA and retains the original 8 GiB free-memory startup and 4 GiB ongoing guards. It has no CPU fallback. Those guards are not a measured minimum GPU specification. Speech recognition runs on CPU after recording ends; this is not streaming recognition.

The model can misunderstand commands. A representative failure is “Pick up the key then drop it” being interpreted with a current-inventory reference instead of the pickup's output, causing deterministic rejection. A correctly transcribed sentence, a valid proposal, and successful physical execution are separate outcomes.

The scenarios are single-player prototypes with a simple navigation planner and provisional timing/balance. They do not establish multiplayer behavior, general language accuracy, noise/accent robustness, or performance on other hardware.
