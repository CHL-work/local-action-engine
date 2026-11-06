# Preserved training engineering

`train-approved.py` and `test_train_approved.py` are copied unchanged from the original project at commit `fa39d2003be98db710a3ce3f3ff12669fbaaac25`. `source-manifest.json` records their original paths, Git blob identities and SHA-256 values.

The source demonstrates exact input readiness checks, train/validation split isolation, atomic optimizer/RNG checkpoints, compute accounting across resumes, and validation-only checkpoint selection. Its historical launch CLI expects the original private campaign's complete data, plan and base checkpoint. Those inputs and campaign authorizations are not imported here. Older launch permissions in its comments are historical; the original campaign remains paused.

Run the portable CPU tests from the repository root:

```powershell
python -m unittest discover -s research/training -p "test_*.py"
```

These tests need only the Python standard library. They construct fake weights and readiness files and never train or evaluate a model. Importing the archived module also does not import PyTorch or Laya.

For public dataset structure/compilation, use `training/prepare-dataset.mjs` as described in [the training guide](../../docs/training.md). A fresh, reviewed readiness/tokenization adapter is required to reuse the archived training loop with a different dataset. `results.json` is a selective historical summary, not a full reproducibility archive or a newly run benchmark.
