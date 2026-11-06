# Fine-tuning and evaluation case study

Local Action Engine extracts the engineering behind a working voice-command prototype. The English model specializes Laya's typed classifier in commands containing up to three actions. The host validates identity, capabilities, references and execution deadlines; the model never executes application code.

## What is included

- `training/contract.mjs` validates command annotations and exact argument evidence.
- `training/compile.mjs` expands each command into bounded classification decisions. Action families and grouped references keep each choice question at eight options or fewer.
- `training/v3-inference.mjs` asks the corresponding questions with predicted parent answers. It does not receive expected labels at runtime.
- `training/v3-decoding.mjs` reconciles action counts and word membership without assuming that spoken order equals execution order.
- `research/training/train-approved.py` preserves the original trainer, including resumable optimizer/RNG state, file identity checks, active compute accounting and validation-only checkpoint selection.
- `research/training/results.json` contains a compact, derived historical summary with source commit and blob identities.

The full private training archive, teacher transcripts, campaign supervisors and unrelated game documents are not part of this extraction. This repository does not claim that its public files alone reproduce the historical training run.

## Recorded experiment

The original dataset contained 4,800 synthetic, AI-reviewed commands: 3,000 training, 450 validation, 450 calibration and 900 test. Its training commands expanded into 100,119 decision items. Family identities kept related examples in the same split. The trainer checked source hashes, split membership, tokenizer audits and teacher provenance before loading the model.

The saved run used Laya 0.4.6, Python 3.12 and PyTorch 2.10.0 with CUDA 12.8 on an RTX 4090. Four epochs used microbatch 4, gradient accumulation 8, encoder learning rate 0.000025 and head learning rate 0.0001. The input budgets were 1,536 total tokens and 1,024 head tokens. Model selection used validation stage-macro accuracy, then validation loss; calibration and test did not select checkpoints.

| Historical candidate | Validation final-order agreement | Wrong executable orders | Role |
| --- | ---: | ---: | --- |
| Original English epoch 4 | 430 / 450 (95.6%) | 12 | Existing prototype checkpoint |
| Filtered80 English epoch 4 | 436 / 450 (96.9%) | 8 | Retained for further study, not selected for this demo |
| Filtered80 count-weighted | Not measured for final orders | Not measured for final orders | Stopped incomplete; not eligible for use |

These are agreement scores against existing synthetic labels, not measured player-intent accuracy or task completion in Unity. The selected decoder for both completed runs was `joint_preserve_count`. The weighted run stopped at 10,101 actual optimizer updates, while its completed control had 12,179; the incomplete comparison does not establish a benefit from weighting.

The compiler supplies annotated parent actions and spans during supervised training. Conditional validation of those decisions is different from final-order evaluation, which uses predicted parents throughout. The historical notes also record annotation-related exposure to some held-out rejection rationales. Do not describe the data as untouched human gold or claim confidence calibration: this checkpoint is uncalibrated.

## Prepare an explicit public dataset

The portable preparation command accepts your own separately labeled training and validation JSONL files. Each line follows `training/schema.mjs`: command text, semantic family, scene, ordered actions, spans, slots, variants and an expected outcome. `training/contract.test.mjs` contains small worked examples.

```powershell
npm ci
node training/prepare-dataset.mjs --train my-data/train.jsonl --validation my-data/validation.jsonl --check-only
node training/prepare-dataset.mjs --train my-data/train.jsonl --validation my-data/validation.jsonl --output .local/datasets/example-v1
```

Preparation validates annotations, rejects duplicate command IDs/transcripts and families crossing splits, and writes command/decision exports with SHA-256 manifests. It refuses to overwrite an existing output directory. These commands never load a model, contact a provider, or start training. They do not verify token budgets or human label quality.

The archived trainer's launch adapter is tied to the original campaign quotas, readiness files and base checkpoint. A new public dataset needs its own reviewed readiness/tokenization adapter before that training loop can be reused. The portable export above is not a replacement for those gates. No new fine-tuning run or model evaluation was performed for this extraction; the original campaign remains paused.

## Offline checks

From the repository root, after installing Node dependencies:

```powershell
node --test training/contract.test.mjs training/v3-decoding.test.mjs training/prepare-dataset.test.mjs
python -m unittest discover -s research/training -p "test_*.py"
```

The Python tests use temporary fake weights and exercise readiness, split isolation, resume identity and checkpoint selection without importing PyTorch or Laya. The Node tests exercise contracts, compilation and decoding, not recognition or model accuracy.
