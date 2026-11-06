# Original English action classifier

## Intended use

This is the preserved original English epoch-4 Laya checkpoint used by the Windows prototype. It classifies bounded questions about action count, word membership, action type and arguments. The interpreter builds a typed proposal; the host validates that proposal before Unity executes it.

It supports the existing prototype action vocabulary and English text. The action catalog includes prototype creatures and abilities because those identifiers form part of the trained contract. It is not a general chat model, a code executor, or a replacement for live world validation.

## Identity

| Field | Value |
| --- | --- |
| Base | `convaiinnovations/laya` |
| Pinned base revision | `7b928d828b7b0e022f929d9bd2e44165aa270148` |
| Encoder | `answerdotai/ModernBERT-large` |
| Fine-tune | Original English protocol 3, epoch 4 |
| Weight SHA-256 | `dc8cd7f6928a278b8297e918e9ca79791ffb406eeab3c48183441b9ba29a32cb` |
| Input format | `marked_word_and_focus_text_v1` |
| Decoder used by the prototype | `joint_preserve_count` |
| Confidence calibration | Not fitted |

The [pinned upstream model](https://huggingface.co/convaiinnovations/laya/tree/7b928d828b7b0e022f929d9bd2e44165aa270148) and `eval/laya-checkpoint.json` identify the Apache-2.0 base. The model archive carries `LICENSE`, `NOTICE` and `MODEL_CARD.md`; dependency licenses remain applicable separately.

## Inference package

The checkpoint directory requires these six runtime files:

```text
brainwash-training.json
rl_agent_config.json
model.safetensors
encoder/config.json
tokenizer/tokenizer.json
tokenizer/tokenizer_config.json
```

The weights are 842,609,220 bytes; the six runtime files total approximately 807 MiB. The distribution also includes the three licensing/documentation files above. Optimizer states, training datasets and other epoch checkpoints are unnecessary for running the demo. Model assets are distributed separately from Git source and are verified during setup/loading. `models/laya-v3.json` records the public archive URL and identities.

`prototype/workers.mjs` checks protocol, language, epoch and checkpoint identity. `training/v3-worker.py` computes the weight hash when loading. The saved `brainwash-training.json` includes `promotedToGame: false`: that field records the earlier training-stop handoff. Subsequent owner-authorized prototype work used this exact original checkpoint. Its historical metadata is preserved rather than rewritten.

## Runtime requirements

The current worker requires a CUDA-capable NVIDIA GPU with **at least 8 GiB free GPU memory before loading**. It has no CPU fallback. It uses fp16 weights/autocast, float32 logits and one classification decision per request, with a 4 GiB allocator budget; it stops if free GPU memory falls below 4 GiB during requests. Available memory is the requirement, not merely the card's advertised capacity.

The saved environment uses Python 3.12, Laya 0.4.6, PyTorch 2.10.0+cu128 and Transformers 5.19.0. `eval/laya-requirements.lock.txt` preserves the original dependency snapshot. The separate Parakeet speech recognizer runs on CPU; typed commands still require the Laya GPU worker.

## Evidence and limits

The saved original epoch-4 result was **430/450 final-order agreement**, with **12 wrong executable orders**, against synthetic validation labels. This is neither a human player-intent study nor a measured success rate for the Unity demo. See [the training case study](training.md) and the source identities in `research/training/results.json`.

Model probabilities are uncalibrated and must not be treated as trustworthy execution confidence. Predicted references can be wrong even when command structure is valid. The host checks contracts, entity identity, ownership and capabilities; Unity additionally enforces live status, collision and expiry. These checks constrain execution but do not guarantee correct intent interpretation.

Non-English runtime support, CPU parity, broad GPU compatibility, accent/noise robustness and target-machine latency percentiles are unverified. The public extraction does not promote a different trained candidate or claim a new model benchmark.
