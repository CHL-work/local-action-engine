import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {startJsonWorker, startWorkers, ORIGINAL_WEIGHTS_SHA256} from './workers.mjs';

const python = process.env.BRAINWASH_TEST_PYTHON || 'python';

async function fixture(t, source, options = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'brainwash-worker-test-'));
  const script = path.join(directory, 'fixture.py');
  await writeFile(script, source);
  const worker = startJsonWorker({python, script, cwd: directory, name: 'fixture', startupMs: 5000, requestMs: 2000, ...options});
  t.after(async () => { await worker.close(); await rm(directory, {recursive: true, force: true}); });
  return worker;
}

const echo = `import json,sys,time
print(json.dumps({'ready': {'fixture': True}}), flush=True)
for line in sys.stdin:
    request=json.loads(line)
    time.sleep(request.get('delay', 0))
    print(json.dumps({'id':request['id'],'value':request.get('value')}), flush=True)
`;

test('persistent worker rejects overlapping requests and stays usable after completion', async t => {
  const worker = await fixture(t, echo);
  assert.deepEqual(await worker.readiness, {fixture: true});
  assert.equal(worker.available, true);
  const first = worker.request({value: 'first', delay: 0.15});
  await assert.rejects(worker.request({value: 'overlap'}), /busy/);
  assert.equal((await first).value, 'first');
  assert.equal((await worker.request({value: 'second'})).value, 'second');
});

test('request timeout permanently disables the worker instead of accepting a late response', async t => {
  const worker = await fixture(t, echo, {requestMs: 25});
  await worker.readiness;
  await assert.rejects(worker.request({delay: 0.25}), /timed out/);
  assert.equal(worker.available, false);
  assert.match(worker.error, /timed out/);
  await assert.rejects(worker.request({value: 'later'}), /timed out/);
});

test('worker exit rejects the outstanding request', async t => {
  const worker = await fixture(t, "import json,sys\nprint(json.dumps({'ready':True}),flush=True)\nsys.stdin.readline()\nsys.exit(4)\n");
  await worker.readiness;
  await assert.rejects(worker.request({}), /exited \(4\)/);
});

test('a non-original checkpoint is rejected before any worker is launched', async t => {
  const checkpoint = await mkdtemp(path.join(os.tmpdir(), 'brainwash-checkpoint-test-'));
  t.after(() => rm(checkpoint, {recursive: true, force: true}));
  await writeFile(path.join(checkpoint, 'brainwash-training.json'), JSON.stringify({epoch: 4, modelSha256: 'other'}));
  await writeFile(path.join(checkpoint, 'rl_agent_config.json'), JSON.stringify({training: {protocol: 3, language: 'en', epoch: 4}}));
  await assert.rejects(startWorkers({root: checkpoint, checkpoint}), /preserved original/);
});

test('cancelling startup closes both child processes before rejecting', async t => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'brainwash-startup-test-'));
  t.after(() => rm(root, {recursive: true, force: true}));
  await mkdir(path.join(root, 'training'));
  await mkdir(path.join(root, 'prototype'));
  await writeFile(path.join(root, 'brainwash-training.json'), JSON.stringify({epoch: 4, modelSha256: ORIGINAL_WEIGHTS_SHA256}));
  await writeFile(path.join(root, 'rl_agent_config.json'), JSON.stringify({training: {protocol: 3, language: 'en', epoch: 4}}));
  // Disposable sleepers never load a model. On Windows, deleting their cwd
  // succeeds only after shutdown has actually released the child processes.
  for (const script of ['training/v3-worker.py', 'prototype/stt-worker.py']) {
    await writeFile(path.join(root, script), 'import time\ntime.sleep(60)\n');
  }
  const controller = new AbortController();
  const loading = startWorkers({root, checkpoint: root, python, sttPython: python, sttModel: root, signal: controller.signal});
  setTimeout(() => controller.abort(), 100);
  await assert.rejects(loading, /closed|abort/i);
});
