# Generated demo audio

`generate_sfx.py` synthesizes the demo sound effects with oscillators, filtered noise, modal resonators, formant voices, and synthetic reverberation. Its committed outputs live in `unity/Assets/Brainwash/Resources/Audio`, including `sfx.json`. Building or playing the demo uses those outputs and does not run the generator.

The imported manifest contains 104 sound IDs and 148 Ogg clips. Unity's `GameAudio` loads them, applies per-sound gain/pitch settings, and ducks playback during microphone recording. `ModelImport` configures compressed loops and predecoded short effects.

Optional regeneration requires Python, numpy, scipy, ffmpeg with libvorbis, and espeak-ng's built-in formant voices. `soundfile` is optional. The original documented toolchain was Python 3.13, numpy 2.5.4, scipy 1.18.1, ffmpeg 6.1.1, and espeak-ng 1.51; this extraction has not rerun it.

```powershell
python art/audio/generate_sfx.py --list
python art/audio/generate_sfx.py --out .local/audio-preview --jobs 4 --report .local/audio-preview/stats.json
```

Without `--out`, generation writes the Unity Resources outputs. Use a separate environment and review changes before replacing the committed assets. The script seeds each clip by identity and checks decoded peaks and loop boundaries; byte reproducibility depends on the exact toolchain.

No downloaded sound library or recorded voice sample is used by these recipes. Two texture layers use nonsense phonemes from espeak-ng's built-in synthesizer before further filtering. This preserves the source generation method; it is not a new listening assessment or an audio-quality claim.
