# Local Action Engine

This public engineering extraction contains the existing Unity Windows demo,
the local inference stack, and selected training research. The original source
repository remains separate. See `provenance/source-manifest.json`.

- Inspect the branch, working tree and worktrees before editing. Use a task
  branch and explicit file staging; changes to main go through a pull request.
- `npm ci --ignore-scripts` and `npm test` run offline Node tests with injected
  workers. Python tests use the standard library. See `docs/windows-ci.md`.
- Do not silently replace the original checkpoint or change its contract.
  Historical results are synthetic-label agreement, not player-intent accuracy.
- Training and new model evaluations are not part of build/setup/CI. The saved
  trainer is an archival engineering example; existing owner pause continues.
- Runtime setup downloads explicitly pinned dependencies and model files.
  Keep credentials, personal paths, virtual environments and training data out
  of source and release packages. Do not copy `.local` wholesale.
