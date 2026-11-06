"""Readiness/selection tests use tiny fake checkpoints; never load/train Laya."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("train_approved", Path(__file__).with_name("train-approved.py"))
training = importlib.util.module_from_spec(spec)
spec.loader.exec_module(training)


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.data = self.root / "data"
        self.data.mkdir()
        (self.root / "training").mkdir()
        base = self.root / ".local/models/laya-english"
        base.mkdir(parents=True)
        (base / "model.safetensors").write_bytes(b"fake weights used only for gate tests")
        training.write_json(base / "rl_agent_config.json", {"encoder": "answerdotai/ModernBERT-large"})
        self.base_patch = patch.object(training, "BASE_SHA256", training.sha256(base / "model.safetensors"))
        self.base_patch.start()
        training.write_json(self.root / "training/plan.json", {
            "baseCheckpoint": ".local/models/laya-english", "contract": {"protocol": 3, "maxActions": 3},
            "languageStrategy": {"currentLanguage": "en"}, "inputFormat": training.FORMAT,
            "plannedTraining": training.PARAMETERS})
        audit = {"complete": True, "labelsComplete": True, "tokenizationComplete": True,
                 "language": "en", "commands": 4800, "curatedBlindReviewed": 4800,
                 "errors": [], "duplicates": [], "original300Overlap": [], "splits": {},
                 "teacher": {"passed": True, "errors": [], "model": "claude-opus-5-5", "requestedEffort": "xhigh"}}
        artifacts, tokens = [], {"maxLen": 1536, "headMaxLen": 1024, "splits": {}}
        for split, count in training.QUOTAS.items():
            commands, decisions = [], []
            for index in range(count):
                cid = f"{split}-{index}"
                family = split + "-family-" + str(index // 10)
                commands.append({"id": cid, "transcript": f"Example {split} {index}", "family": family,
                    "intent_count": "one", "provenance": {"locale": "en", "split": split}})
                decisions.append({"state": "state", "questions": {"q": {"type": "choice", "criteria": ["a", "b"]}},
                    "expected": {"q": "a"}, "provenance": {"caseId": cid, "family": family, "stage": "structure",
                    "split": split, "inputFormat": training.FORMAT}})
            for kind, rows in [("commands", commands), ("decisions", decisions)]:
                name = f"{kind}.{split}.jsonl"
                (self.data / name).write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
                artifacts.append({"file": name, "rows": count, "sha256": training.sha256(self.data / name)})
            audit["splits"][split] = {"commands": count, "decisions": count}
            tokens["splits"][split] = {"sourceSha256": artifacts[-1]["sha256"], "rows": count,
                "truncatedStates": 0, "truncatedInstructions": 0, "longOptions": 0, "collapsedOptions": 0}
        training.write_json(self.data / "audit.json", audit)
        training.write_json(self.data / "compilation.json", {"complete": True, "inputFormat": training.FORMAT, "splits": audit["splits"]})
        training.write_json(self.data / "artifacts.json", artifacts)
        training.write_json(self.data / "token-audit.json", tokens)
        training.write_json(self.data / "preflight.json", {"sourceSha256": tokens["splits"]["train"]["sourceSha256"],
            "valid_items": 3000, "rows_read": 3000, "skipped": {}})

    def tearDown(self):
        self.base_patch.stop()
        self.tmp.cleanup()

    def change_json(self, name, operation):
        value = training.read_json(self.data / name)
        operation(value)
        training.write_json(self.data / name, value)

    def test_exact_ready_files_pass_without_loading_model(self):
        result = training.ready_dataset(self.data, self.root)
        self.assertEqual(result["commandCounts"], training.QUOTAS)
        self.assertEqual(len(result["commandMeta"]), 4800)

    def test_incomplete_audit_refuses(self):
        self.change_json("audit.json", lambda v: v.update(complete=False))
        with self.assertRaisesRegex(ValueError, "not complete"):
            training.ready_dataset(self.data, self.root)

    def test_file_changed_after_audit_refuses(self):
        with (self.data / "decisions.train.jsonl").open("a", encoding="utf-8") as f:
            f.write("{}\n")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            training.ready_dataset(self.data, self.root)

    def test_split_count_lie_refuses(self):
        self.change_json("audit.json", lambda v: v["splits"]["train"].update(commands=2999))
        with self.assertRaisesRegex(ValueError, "Wrong split quota"):
            training.ready_dataset(self.data, self.root)

    def test_stale_token_hash_refuses_even_if_audit_says_complete(self):
        self.change_json("token-audit.json", lambda v: v["splits"]["calibration"].update(sourceSha256="stale"))
        with self.assertRaisesRegex(ValueError, "Stale token audit"):
            training.ready_dataset(self.data, self.root)

    def test_stale_preflight_refuses(self):
        self.change_json("preflight.json", lambda v: v.update(sourceSha256="stale"))
        with self.assertRaisesRegex(ValueError, "Stale or incomplete training preflight"):
            training.ready_dataset(self.data, self.root)

    def test_wrong_teacher_refuses(self):
        self.change_json("audit.json", lambda v: v["teacher"].update(model="claude-sonnet"))
        with self.assertRaisesRegex(ValueError, "Teacher provenance"):
            training.ready_dataset(self.data, self.root)

    def test_wrong_base_refuses(self):
        (self.root / ".local/models/laya-english/model.safetensors").write_bytes(b"different base")
        with self.assertRaisesRegex(ValueError, "base weights hash"):
            training.ready_dataset(self.data, self.root)

    def test_cross_split_family_refuses_even_with_rehashed_artifact(self):
        path = self.data / "commands.validation.jsonl"
        lines = path.read_text(encoding="utf-8").splitlines()
        row = json.loads(lines[0])
        row["family"] = "train-family-0"
        lines[0] = json.dumps(row)
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        self.change_json("artifacts.json", lambda values: next(x for x in values if x["file"] == path.name).update(sha256=training.sha256(path)))
        with self.assertRaisesRegex(ValueError, "Family crosses splits"):
            training.ready_dataset(self.data, self.root)


class SelectionTests(unittest.TestCase):
    def test_macro_stage_accuracy_precedes_micro_accuracy(self):
        best = {"macroStageAccuracy": .8, "accuracy": .95, "loss": .3}
        challenger = {"macroStageAccuracy": .81, "accuracy": .94, "loss": .4}
        self.assertTrue(training.choose_checkpoint(challenger, best))

    def test_validation_loss_breaks_accuracy_tie(self):
        self.assertTrue(training.choose_checkpoint({"macroStageAccuracy": .8, "loss": .2},
                                                 {"macroStageAccuracy": .8, "loss": .3}))
        self.assertFalse(training.choose_checkpoint({"macroStageAccuracy": .8, "loss": .3},
                                                  {"macroStageAccuracy": .8, "loss": .2}))

    def test_resume_allows_refreshed_audits_but_not_data_or_parameters(self):
        original = {"artifacts": [{"sha256": "same"}], "parameters": {"epochs": 4},
                    "readinessHashes": {"audit.json": "old timestamp"}}
        current = dict(original, readinessHashes={"audit.json": "new timestamp"})
        self.assertTrue(training.same_resume_identity(original, current))
        self.assertFalse(training.same_resume_identity(original, dict(current, artifacts=[{"sha256": "changed"}])))
        self.assertFalse(training.same_resume_identity(original, dict(current, parameters={"epochs": 5})))

    def test_baseline_comparison_reports_regression_without_selecting_base(self):
        baseline = {"accuracy": .8, "macroStageAccuracy": .7, "loss": .3}
        trained = {"accuracy": .75, "macroStageAccuracy": .65, "loss": .4}
        comparison = training.baseline_comparison(trained, baseline)
        self.assertFalse(comparison["improvedPrimaryMetric"])
        self.assertLess(comparison["macroStageAccuracyDelta"], 0)
        self.assertGreater(comparison["lossDelta"], 0)
        self.assertTrue(training.choose_checkpoint(trained, None))

    def test_completed_baseline_reused_only_with_matching_identity_and_predictions(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            self.assertIsNone(training.existing_baseline(output, "m", "b", "v"))
            predictions = output / "validation-00.jsonl.gz"
            predictions.write_bytes(b"test predictions")
            baseline = {"manifestSha256": "m", "baseSha256": "b", "validationSha256": "v",
                        "trained": False, "predictionsSha256": training.sha256(predictions)}
            training.write_json(output / "baseline-validation.json", baseline)
            self.assertEqual(training.existing_baseline(output, "m", "b", "v"), baseline)
            with self.assertRaisesRegex(ValueError, "identity mismatch"):
                training.existing_baseline(output, "different run", "b", "v")
            predictions.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "prediction file changed"):
                training.existing_baseline(output, "m", "b", "v")


if __name__ == "__main__":
    unittest.main()
