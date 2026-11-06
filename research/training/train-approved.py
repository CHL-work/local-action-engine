"""Start the owner-authorized English v3 run only after exact-file readiness checks.

Uses Laya 0.4.6's encoders, loss and forward path. The loop adds resumable
optimizer/RNG checkpoints, a time budget, and validation-only epoch selection.
Importing this module or passing --check-only never imports torch or loads weights.
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import math
import os
from pathlib import Path
import random
import shutil
import signal
import sys
import time
import traceback
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
BASE_SHA256 = "891102d372688fc2a094dac56a384bc537b87c63f21f9f3dac0be2b7cbc8d86c"
FORMAT = "marked_word_and_focus_text_v1"
QUOTAS = {"train": 3000, "validation": 450, "calibration": 450, "test": 900}
PARAMETERS = dict(epochs=4, micro_batch=4, grad_accum=8, loss="soft-ce",
                  encoder_lr=2.5e-5, head_lr=1e-4, shuffle_options=["choice"],
                  max_len=1536, head_max_len=1024, freeze_encoder=False,
                  calib_frac=0, calib_max=0)


def now():
    return datetime.now(timezone.utc).isoformat()


def sha256(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def ready_dataset(folder: Path, root: Path = ROOT):
    """Fail closed; checks hashes/quotas without constructing a model.

    Calibration/test bytes are checked for integrity but are never passed to a
    training or scoring routine. This gate cannot certify human label accuracy.
    """
    folder, root = Path(folder).resolve(), Path(root).resolve()
    plan = read_json(root / "training/plan.json")
    require(plan["baseCheckpoint"] == ".local/models/laya-english", "English base path changed")
    require(plan["contract"]["protocol"] == 3 and plan["contract"]["maxActions"] == 3,
            "Training requires the three-action contract")
    require(plan.get("inputFormat") == FORMAT, "Training input format changed")
    require(plan["languageStrategy"]["currentLanguage"] == "en", "English launch only")
    for key, value in PARAMETERS.items():
        require(plan["plannedTraining"].get(key) == value, "Planned parameter changed: " + key)
    base = root / ".local/models/laya-english"
    require(sha256(base / "model.safetensors") == BASE_SHA256, "English base weights hash mismatch")
    require(read_json(base / "rl_agent_config.json").get("encoder") == "answerdotai/ModernBERT-large",
            "Checkpoint is not the English ModernBERT-large base")

    audit = read_json(folder / "audit.json")
    compilation = read_json(folder / "compilation.json")
    require(all(audit.get(k) is True for k in ["complete", "labelsComplete", "tokenizationComplete"]),
            "Dataset is not complete; generation, reviews and tokenizer checks must finish first")
    require(audit.get("commands") == 4800 and audit.get("language") == "en", "Wrong command quota or language")
    require(compilation.get("complete") is True and compilation.get("inputFormat") == FORMAT,
            "Final compilation is incomplete or uses an obsolete compiler")
    require(not audit.get("errors") and not audit.get("duplicates"), "Dataset has audit errors or duplicates")
    require(not any(x["split"] != "train" for x in audit.get("original300Overlap", [])),
            "Original development commands overlap a held-out split")
    require(audit.get("curatedBlindReviewed") == 4800, "Missing independent blind action review")
    teacher = audit.get("teacher", {})
    require(teacher.get("passed") is True and not teacher.get("errors") and
            teacher.get("model") == "claude-opus-5-5" and teacher.get("requestedEffort") == "xhigh",
            "Teacher provenance does not confirm Opus 5.5 xhigh")

    artifacts = read_json(folder / "artifacts.json")
    artifact_by_file = {x["file"]: x for x in artifacts}
    wanted_files = {f"{kind}.{split}.jsonl" for kind in ["commands", "decisions"] for split in QUOTAS}
    require(len(artifacts) == 8 and set(artifact_by_file) == wanted_files, "Incomplete or duplicate artifact manifest")
    preflight = read_json(folder / "preflight.json")
    tokens = read_json(folder / "token-audit.json")
    require(tokens.get("maxLen") == 1536 and tokens.get("headMaxLen") == 1024, "Tokenizer budget mismatch")
    require(not preflight.get("skipped"), "Tokenizer preflight skipped decisions")
    families, ids, transcripts = {}, set(), set()
    command_meta = {}
    for split, quota in QUOTAS.items():
        info = audit["splits"][split]
        require(info["commands"] == quota and compilation["splits"][split]["commands"] == quota,
                "Wrong split quota: " + split)
        for kind in ["commands", "decisions"]:
            name = f"{kind}.{split}.jsonl"
            artifact = artifact_by_file[name]
            source = folder / name
            require(sha256(source) == artifact["sha256"], "Artifact hash mismatch: " + name)
            rows = 0
            with source.open(encoding="utf-8") as handle:
                for line in handle:
                    require(bool(line.strip()), "Blank row: " + name)
                    row = json.loads(line)
                    rows += 1
                    if kind == "commands":
                        require(row["provenance"]["split"] == split and row["provenance"]["locale"] == "en",
                                "Incorrect command split or locale: " + row["id"])
                        require(row["id"] not in ids, "Duplicate command ID")
                        text = " ".join("".join(c.lower() if c.isalnum() else " " for c in row["transcript"]).split())
                        require(text not in transcripts, "Duplicate normalized transcript")
                        transcripts.add(text)
                        ids.add(row["id"])
                        family = row["family"]
                        require(families.get(family, split) == split, "Family crosses splits")
                        families[family] = split
                        command_meta[row["id"]] = {"family": family, "split": split, "count": row["intent_count"]}
                    else:
                        meta = row["provenance"]
                        command = command_meta.get(meta["caseId"])
                        require(command and command["split"] == split and command["family"] == meta["family"],
                                "Decision points to an unknown/wrong-split command")
                        require(meta["split"] == split and meta["inputFormat"] == FORMAT, "Decision input format/split changed")
                        require(len(row.get("questions", {})) == 1, "Expected one question per decision")
                        require(set(row.get("expected", {})) == set(row["questions"]), "Missing decision label")
            require(rows == artifact["rows"] == info[kind], "Artifact row count mismatch: " + name)
            if kind == "commands":
                require(rows == quota, "Actual command quota mismatch: " + split)
            else:
                token = tokens["splits"][split]
                require(token.get("sourceSha256") == artifact["sha256"] and token.get("rows") == rows,
                        "Stale token audit: " + split)
                require(all(token.get(key) == 0 for key in ["truncatedStates", "truncatedInstructions", "longOptions", "collapsedOptions"]),
                        "Token truncation/collapse: " + split)
                if split == "train":
                    require(preflight.get("sourceSha256") == artifact["sha256"] and
                            preflight.get("valid_items") == rows == preflight.get("rows_read"),
                            "Stale or incomplete training preflight")
    return {"dataset": str(folder), "base": str(base), "baseSha256": BASE_SHA256,
            "inputFormat": FORMAT, "parameters": PARAMETERS, "artifacts": artifacts,
            "readinessHashes": {name: sha256(folder / name) for name in
                                ["audit.json", "compilation.json", "preflight.json", "token-audit.json", "artifacts.json"]},
            "commandCounts": QUOTAS, "commandMeta": command_meta,
            "limitations": ["Synthetic AI-reviewed labels are not native human gold.",
                            "Validation uses gold parent decisions and spans; these are not end-to-end game metrics.",
                            "The retained gameplay interpreter has no complete predicted-span protocol-3 evaluation yet.",
                            "Calibration and independent test are excluded from checkpoint selection."]}


@contextlib.contextmanager
def exclusive_run_lock(output):
    """An OS lock prevents duplicate GPU launches and releases on process death."""
    lock = (output / "run.lock").open("a+b")
    lock.seek(0)
    lock.write(b"1")
    lock.flush()
    lock.seek(0)
    try:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        lock.close()


class Tee:
    def __init__(self, primary, log):
        self.primary, self.log = primary, log

    def write(self, message):
        self.primary.write(message)
        self.log.write(message)
        self.flush()

    def flush(self):
        self.primary.flush()
        self.log.flush()


def choose_checkpoint(metrics, best):
    """Stage-macro accuracy avoids letting word-membership volume dominate."""
    return best is None or (metrics["macroStageAccuracy"], -metrics["loss"]) > (
        best["macroStageAccuracy"], -best["loss"])


def same_resume_identity(original, current):
    """Fresh audit timestamps may differ; inputs and training identity may not.

    The full readiness gate is rerun before this comparison. Preserve the original
    manifest bytes because optimizer checkpoints bind to that exact manifest.
    """
    ignored = {"readinessHashes"}
    return {k: v for k, v in original.items() if k not in ignored} == {
        k: v for k, v in current.items() if k not in ignored}


def baseline_comparison(metrics, baseline):
    return {"reference": "pinned untrained English base; not a selection candidate",
            "accuracyDelta": metrics["accuracy"] - baseline["accuracy"],
            "macroStageAccuracyDelta": metrics["macroStageAccuracy"] - baseline["macroStageAccuracy"],
            "lossDelta": metrics["loss"] - baseline["loss"],
            "improvedPrimaryMetric": metrics["macroStageAccuracy"] > baseline["macroStageAccuracy"]}


def existing_baseline(output, manifest_hash, base_hash, validation_hash):
    path = output / "baseline-validation.json"
    if not path.exists():
        return None
    baseline = read_json(path)
    require(baseline.get("manifestSha256") == manifest_hash and baseline.get("baseSha256") == base_hash
            and baseline.get("validationSha256") == validation_hash and baseline.get("trained") is False,
            "Cached baseline identity mismatch")
    require(sha256(output / "validation-00.jsonl.gz") == baseline.get("predictionsSha256"),
            "Cached baseline prediction file changed")
    return baseline


def train(output, readiness, resume, max_hours):
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", USE_TF="0")
    import importlib.metadata
    import torch
    from laya import train as laya
    from laya.common import collate_items, uses_parallel_layout

    require(importlib.metadata.version("laya") == "0.4.6", "Review this wrapper before changing Laya version")
    require(torch.cuda.is_available(), "CUDA is required; no silent CPU training fallback")
    torch.set_num_threads(4)
    device = torch.device("cuda", 0)
    cfg_values = dict(PARAMETERS, seed=0, eval_data=None, log_every=50)
    cfg_values["shuffle_options"] = tuple(cfg_values["shuffle_options"])
    config = laya.TrainConfig(**cfg_values)
    config.validate()
    session_start = time.monotonic()
    state = {"epoch": 0, "nextBatch": 0, "phase": "train", "epochLoss": 0.0,
             "optimizerUpdates": 0, "activeSeconds": 0.0, "history": [], "best": None, "baseline": None}
    saved = None
    if resume:
        require((output / "resume.pt").is_file(), "No safe optimizer checkpoint exists; use a new output run")
        # Only this locally generated checkpoint is accepted; never load an untrusted pickle.
        saved = torch.load(output / "resume.pt", map_location="cpu", weights_only=False)
        require(saved["manifestSha256"] == sha256(output / "manifest.json"), "Resume manifest mismatch")
        state = saved["state"]
    # Count work since the last safe checkpoint too if the prior process failed.
    # The model rolls back, but those GPU minutes must not reset the time budget.
    prior_progress = read_json(output / "progress.json") if resume and (output / "progress.json").exists() else {}
    elapsed_before = max(state["activeSeconds"], prior_progress.get("activeSeconds", 0))
    requested_stop = []
    def stop_handler(signum, frame):
        requested_stop.append("signal " + str(signum))
    for sig in [signal.SIGINT, signal.SIGTERM]:
        signal.signal(sig, stop_handler)

    def elapsed():
        return elapsed_before + time.monotonic() - session_start

    def stop_reason():
        if requested_stop:
            return requested_stop[0]
        if (output / "stop.request").exists():
            return "stop.request file"
        # Reserve two minutes to commit a full safe checkpoint within the budget.
        if elapsed() >= max_hours * 3600 - 120:
            return "active training/validation time budget reached"
        return None

    def progress(status, **extra):
        write_json(output / "progress.json", dict(status=status, updatedAt=now(), pid=os.getpid(),
            language="en", protocol=3, trainingStarted=state["optimizerUpdates"] > 0, epoch=state["epoch"],
            nextBatch=state["nextBatch"], optimizerUpdates=state["optimizerUpdates"],
            activeSeconds=elapsed(), maxHours=max_hours, history=state["history"], best=state["best"],
            baseline=state.get("baseline"), **extra))

    progress("loading")
    model, tok, model_cfg = laya.load_checkpoint(readiness["base"])
    model.to(device)
    if saved:
        model.load_state_dict(saved["model"], strict=True)
    model.encoder.gradient_checkpointing_enable(gradient_checkpointing_kwargs={"use_reentrant": False})
    model.head_checkpointing = True
    parallel = uses_parallel_layout(model_cfg)
    groups = [
        {"params": [p for n, p in model.named_parameters() if n.startswith("encoder.") and p.requires_grad], "lr": config.encoder_lr},
        {"params": [p for n, p in model.named_parameters() if not n.startswith("encoder.") and p.requires_grad], "lr": config.head_lr}]
    optimizer = torch.optim.AdamW(groups, weight_decay=config.weight_decay)
    rows = laya.read_data(str(output / "inputs/decisions.train.jsonl"))
    items, skipped = laya.items_from_rows(tok, rows, config.max_len, config.head_max_len)
    require(not skipped and len(items) == len(rows), "Training preprocessing differs from exact-file preflight")
    del rows
    validation_rows = laya.read_data(str(output / "inputs/decisions.validation.jsonl"))
    validation_items, skipped = laya.items_from_rows(tok, validation_rows, config.max_len, config.head_max_len)
    require(not skipped and len(validation_items) == len(validation_rows), "Validation preprocessing skipped decisions")
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer,
        T_max=laya.optimizer_updates(len(items), config.micro_batch, config.grad_accum, config.epochs), eta_min=config.min_lr)
    scaler = torch.amp.GradScaler("cuda")
    params = [p for group in groups for p in group["params"]]
    order_rng = random.Random(config.seed)
    torch.manual_seed(config.seed)
    if saved:
        optimizer.load_state_dict(saved["optimizer"])
        scheduler.load_state_dict(saved["scheduler"])
        scaler.load_state_dict(saved["scaler"])
        order_rng.setstate(saved["optionRng"])
        torch.set_rng_state(saved["torchRng"])
        torch.cuda.set_rng_state_all(saved["cudaRng"])
        del saved
    print(laya.update_budget_message(len(items), config), flush=True)
    print("Only explicit validation selects checkpoints; calibration/test are sealed.", flush=True)
    write_json(output / "runtime.json", {"startedAt": now(), "python": sys.version,
        "torch": torch.__version__, "laya": importlib.metadata.version("laya"),
        "gpu": torch.cuda.get_device_name(), "cuda": torch.version.cuda,
        "trainDecisions": len(items), "validationDecisions": len(validation_items),
        "precision": "FP32 parameters/Adam state, FP16 autocast with GradScaler; saved inference weights FP16"})
    last_save = time.monotonic()

    def save_resume():
        nonlocal last_save
        state["activeSeconds"] = elapsed()
        temporary = output / "resume.pt.tmp"
        torch.save({"manifestSha256": sha256(output / "manifest.json"), "state": state,
            "model": model.state_dict(), "optimizer": optimizer.state_dict(), "scheduler": scheduler.state_dict(),
            "scaler": scaler.state_dict(), "optionRng": order_rng.getstate(), "torchRng": torch.get_rng_state(),
            "cudaRng": torch.cuda.get_rng_state_all()}, temporary)
        os.replace(temporary, output / "resume.pt")
        last_save = time.monotonic()

    def inference_checkpoint(epoch):
        path = output / ("epoch-%02d" % epoch)
        if path.exists():
            require(read_json(path / "brainwash-training.json")["manifestSha256"] == sha256(output / "manifest.json"),
                    "Existing epoch checkpoint belongs to a different run")
            return path
        temporary = output / ("epoch-%02d.pending" % epoch)
        # An interrupted write is resumed only inside this run-owned temporary directory.
        temporary.mkdir(exist_ok=True)
        out_cfg = dict(model_cfg, max_len=config.max_len, head_max_len=config.head_max_len,
                       fine_tuned=True, temperature=[1.0, 1.0, 1.0])
        out_cfg.pop("temperature_by_options", None)
        out_cfg["training"] = {"protocol": 3, "language": "en", "epoch": epoch,
            "calibrationStatus": "not fitted; do not use confidence for automatic acceptance",
            "inputFormat": FORMAT, "parameters": PARAMETERS}
        laya.save_checkpoint(model, tok, out_cfg, str(temporary))
        write_json(temporary / "brainwash-training.json", {"manifestSha256": sha256(output / "manifest.json"),
            "epoch": epoch, "modelSha256": sha256(temporary / "model.safetensors"),
            "promotedToGame": False, "calibrated": False})
        os.rename(temporary, path)
        return path

    @torch.no_grad()
    def validate(epoch):
        import gzip
        model.eval()
        sums, by_case, loss_sum, total = {}, {}, 0.0, 0
        output_file = output / ("validation-%02d.jsonl.gz" % epoch)
        temporary = output_file.with_name(output_file.name + ".tmp")
        with gzip.open(temporary, "wt", encoding="utf-8") as predictions:
            for start in range(0, len(validation_items), config.micro_batch):
                if stop_reason():
                    return None
                chunk = validation_items[start:start + config.micro_batch]
                batch = collate_items([[laya.encode_item(tok, item, config.max_len, config.head_max_len, parallel=parallel)
                                       for item in chunk]], tok.pad_token_id)
                logits = laya._forward(model, batch, device, True, False)
                loss = laya.soft_ce_loss(logits, batch["target"].to(device), batch["marker_mask"].to(device))
                require(torch.isfinite(loss).item(), "Non-finite validation loss")
                loss_sum += loss.item() * len(chunk)
                for offset, (logit, item) in enumerate(zip(logits, chunk)):
                    row = validation_rows[start + offset]
                    probabilities = torch.softmax(logit[:item["k"]], -1)
                    predicted = int(probabilities.argmax().item())
                    expected = max(range(item["k"]), key=lambda i: item["target"][i])
                    correct = predicted == expected
                    meta = row["provenance"]
                    bucket = sums.setdefault(meta["stage"], {"correct": 0, "total": 0})
                    bucket["correct"] += int(correct)
                    bucket["total"] += 1
                    case = by_case.setdefault(meta["caseId"], {"correct": True, "decisions": 0})
                    case["correct"] &= correct
                    case["decisions"] += 1
                    predictions.write(json.dumps({"caseId": meta["caseId"], "stage": meta["stage"],
                        "question": next(iter(row["questions"])), "predictedIndex": predicted,
                        "expectedIndex": expected, "correct": correct, "probabilities": probabilities.tolist()}) + "\n")
                    total += 1
                if start % 200 == 0:
                    progress("validating_baseline" if epoch == 0 else "validating", validationEpoch=epoch, validationDecisions=total)
        os.replace(temporary, output_file)
        by_count = {}
        for case_id, case in by_case.items():
            count = readiness["commandMeta"][case_id]["count"]
            bucket = by_count.setdefault(count, {"correct": 0, "commands": 0})
            bucket["correct"] += int(case["correct"])
            bucket["commands"] += 1
        for bucket in sums.values():
            bucket["accuracy"] = bucket["correct"] / bucket["total"]
        return {"epoch": epoch, "decisions": total, "loss": loss_sum / total,
                "accuracy": sum(b["correct"] for b in sums.values()) / total,
                "macroStageAccuracy": sum(b["accuracy"] for b in sums.values()) / len(sums),
                "byStage": sums, "teacherForcedCommandExactByCount": by_count,
                "endToEndGameAccuracy": None, "calibrationUsed": False, "testUsed": False}

    try:
        # Commit a full zero-update recovery point before baseline inference or
        # the first minibatch; an early OOM/error must not strand a fresh run.
        if not resume:
            save_resume()
            progress("initial_checkpoint_saved")
        validation_hash = sha256(output / "inputs/decisions.validation.jsonl")
        baseline = existing_baseline(output, sha256(output / "manifest.json"), readiness["baseSha256"], validation_hash)
        if baseline is None:
            require(state["epoch"] == 0 and state["nextBatch"] == 0 and state["optimizerUpdates"] == 0,
                    "Missing untrained baseline after training began; do not score trained weights as the base")
            baseline = validate(0)
            if baseline is None:
                save_resume()
                progress("stopped", reason=stop_reason())
                return
            baseline.update(manifestSha256=sha256(output / "manifest.json"), baseSha256=readiness["baseSha256"],
                            validationSha256=validation_hash, predictionsSha256=sha256(output / "validation-00.jsonl.gz"),
                            trained=False, checkpointRole="untrained reference only; excluded from selection")
            write_json(output / "baseline-validation.json", baseline)
        state["baseline"] = baseline
        # If a process died after writing baseline JSON but before this snapshot,
        # the cached identity/hash checks above avoid running baseline again.
        save_resume()
        progress("baseline_ready")
        batches_per_epoch = math.ceil(len(items) / config.micro_batch)
        while state["epoch"] < config.epochs:
            epoch = state["epoch"]
            if state["phase"] == "train":
                model.train()
                epoch_items = list(items)
                random.Random(config.seed + epoch).shuffle(epoch_items)
                optimizer.zero_grad(set_to_none=True)
                for batch_no in range(state["nextBatch"], batches_per_epoch):
                    # Only stop/checkpoint at a completed accumulation boundary.
                    if batch_no % config.grad_accum == 0 and stop_reason():
                        save_resume()
                        progress("stopped", reason=stop_reason())
                        return
                    start = batch_no * config.micro_batch
                    chunk = [laya.encode_item(tok, item, config.max_len, config.head_max_len,
                        laya.draw_option_order(item, order_rng, config.shuffle_options), parallel)
                        for item in epoch_items[start:start + config.micro_batch]]
                    batch = collate_items([chunk], tok.pad_token_id)
                    logits = laya._forward(model, batch, device, True, False)
                    loss = laya.soft_ce_loss(logits, batch["target"].to(device), batch["marker_mask"].to(device))
                    require(torch.isfinite(loss).item(), "Non-finite training loss; preserve last safe checkpoint")
                    window_start = batch_no // config.grad_accum * config.grad_accum
                    window_size = min(config.grad_accum, batches_per_epoch - window_start)
                    scaler.scale(loss / window_size).backward()
                    state["epochLoss"] += loss.item()
                    state["nextBatch"] = batch_no + 1
                    boundary = (batch_no + 1) % config.grad_accum == 0 or batch_no + 1 == batches_per_epoch
                    if boundary:
                        scaler.unscale_(optimizer)
                        torch.nn.utils.clip_grad_norm_(params, config.grad_clip)
                        previous_scale = scaler.get_scale()
                        scaler.step(optimizer)
                        scaler.update()
                        # A skipped overflow update must not advance the LR schedule.
                        if scaler.get_scale() >= previous_scale:
                            scheduler.step()
                            state["optimizerUpdates"] += 1
                        optimizer.zero_grad(set_to_none=True)
                        if time.monotonic() - last_save > 900:
                            save_resume()
                    if (batch_no + 1) % config.log_every == 0:
                        progress("training", batchesPerEpoch=batches_per_epoch, latestLoss=loss.item())
                        print(json.dumps({"epoch": epoch + 1, "batch": batch_no + 1,
                            "loss": loss.item(), "activeHours": elapsed() / 3600}), flush=True)
                state["phase"] = "validate"
                save_resume()
            checkpoint = inference_checkpoint(epoch + 1)
            metrics = validate(epoch + 1)
            if metrics is None:
                save_resume()
                progress("stopped", reason=stop_reason())
                return
            metrics["trainingMeanLoss"] = state["epochLoss"] / batches_per_epoch
            metrics["checkpoint"] = checkpoint.name
            metrics["baselineComparison"] = baseline_comparison(metrics, state["baseline"])
            state["history"].append(metrics)
            write_json(output / ("validation-%02d.json" % (epoch + 1)), metrics)
            if choose_checkpoint(metrics, state["best"]):
                state["best"] = metrics
                write_json(output / "best-checkpoint.json", dict(metrics, selection="validation macro-stage accuracy, then validation CE",
                    calibrated=False, promotedToGame=False, limitations=readiness["limitations"]))
            state.update(epoch=epoch + 1, nextBatch=0, phase="train", epochLoss=0.0)
            save_resume()
            progress("epoch_complete")
        progress("complete")
        write_json(output / "training-result.json", {"completedAt": now(), "epochs": state["epoch"],
            "activeSeconds": elapsed(), "baseline": state["baseline"], "best": state["best"], "history": state["history"],
            "calibrationUsed": False, "testUsed": False, "limitations": readiness["limitations"]})
    except BaseException as exc:
        # Do not checkpoint partial accumulated gradients or a corrupt/nonfinite update.
        progress("failed", error=str(exc), resumeFrom="last complete atomic resume.pt")
        write_json(output / "error.json", {"time": now(), "error": str(exc), "traceback": traceback.format_exc()})
        raise


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--start", action="store_true", help="Exercise the owner's authorization after readiness passes")
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--max-hours", type=float, default=16)
    args = parser.parse_args(argv)
    require(0.1 <= args.max_hours <= 16, "Run budget must be at most 16 active hours")
    readiness = ready_dataset(args.run_dir)
    if args.check_only:
        print(json.dumps({"ready": True, "trainingStarted": False, "commandCounts": QUOTAS,
                          "parameters": PARAMETERS, "baseSha256": readiness["baseSha256"]}, indent=2))
        return 0
    require(args.start and args.output, "Pass --start and a fresh --output to launch the authorized run")
    output = args.output.resolve()
    models = (ROOT / ".local/models").resolve()
    require(output.parent == models and output.name.startswith("brainwash-en-v3-"),
            "Output must be a new .local/models/brainwash-en-v3-* directory")
    require(args.resume or not output.exists(), "Output exists; use --resume or a fresh run name")
    output.mkdir(exist_ok=True)
    with exclusive_run_lock(output):
        manifest = {k: v for k, v in readiness.items() if k != "commandMeta"}
        manifest.update(wrapperSha256=sha256(__file__), maxHours=args.max_hours,
                        authorization="Owner authorized automatic English training once data is ready.")
        if args.resume:
            require(same_resume_identity(read_json(output / "manifest.json"), manifest),
                    "Resume inputs, parameters, base or trainer source changed")
            write_json(output / "resume-readiness.json", {"checkedAt": now(), "current": manifest["readinessHashes"],
                "note": "All readiness gates rerun; original manifest preserved. Audit timestamps may change without changing exported inputs."})
        else:
            write_json(output / "manifest.json", manifest)
            (output / "inputs").mkdir()
            for split in ["train", "validation"]:
                name = f"decisions.{split}.jsonl"
                shutil.copyfile(args.run_dir / name, output / "inputs" / name)
            for name in ["audit.json", "preflight.json", "token-audit.json", "artifacts.json"]:
                shutil.copyfile(args.run_dir / name, output / "inputs" / name)
        for artifact in readiness["artifacts"]:
            if artifact["file"] in ["decisions.train.jsonl", "decisions.validation.jsonl"]:
                require(sha256(output / "inputs" / artifact["file"]) == artifact["sha256"], "Training input snapshot changed")
        with (output / "training.log").open("a", encoding="utf-8", buffering=1) as log:
            with contextlib.redirect_stdout(Tee(sys.stdout, log)), contextlib.redirect_stderr(Tee(sys.stderr, log)):
                try:
                    train(output, readiness, args.resume, args.max_hours)
                except BaseException as error:
                    # Also record loading/preprocessing failures before the loop starts.
                    write_json(output / "error.json", {"time": now(), "error": str(error), "traceback": traceback.format_exc()})
                    progress = read_json(output / "progress.json") if (output / "progress.json").exists() else {}
                    progress.update(status="failed", updatedAt=now(), error=str(error))
                    write_json(output / "progress.json", progress)
                    traceback.print_exc()
                    raise
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, FileNotFoundError, KeyError) as error:
        print("Training refused: " + str(error), file=sys.stderr)
        raise SystemExit(2)
