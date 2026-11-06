import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {readFile} from 'node:fs/promises';
import path from 'node:path';

export const ORIGINAL_WEIGHTS_SHA256 = 'dc8cd7f6928a278b8297e918e9ca79791ffb406eeab3c48183441b9ba29a32cb';
const MAX_AUDIO_BYTES = 2_000_000;

// Private, persistent JSONL workers. A timed-out subprocess is never reused;
// application deadlines belong to the server, including its before/after checks.
export function startJsonWorker({python, script, args = [], cwd, name, startupMs = 180_000, requestMs = 30_000}) {
  const child = spawn(python, ['-u', script, ...args], {
    cwd, windowsHide: true, stdio: ['pipe', 'pipe', 'inherit'],
    env: {...process.env, PYTHONUTF8: '1', HF_HUB_OFFLINE: '1', TRANSFORMERS_OFFLINE: '1'},
  });
  const exited = new Promise(resolve => child.once('close', resolve));
  let sequence = 0, pending = null, ready = false, stopped = false, terminalError = null;
  let resolveReady, rejectReady;
  const readiness = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
  const stop = () => {
    if (stopped) return;
    stopped = true;
    child.stdin.end();
    const timer = setTimeout(() => { if (child.exitCode === null) child.kill(); }, 1000);
    timer.unref();
  };
  const fail = error => {
    terminalError ??= error;
    clearTimeout(startTimer);
    rejectReady(terminalError);
    if (pending) {
      clearTimeout(pending.timer);
      pending.reject(terminalError);
      pending = null;
    }
    stop();
  };
  const startTimer = setTimeout(() => fail(new Error(`${name} startup timed out`)), startupMs);
  child.on('error', fail);
  child.stdin.on('error', fail);
  child.on('exit', (code, signal) => fail(new Error(`${name} worker exited (${code ?? signal})`)));
  const lines = createInterface({input: child.stdout});
  lines.on('line', line => {
    try {
      if (line.length > 2_000_000) throw new Error(`${name} response exceeded its size limit`);
      const message = JSON.parse(line);
      if (!ready && message.ready) {
        ready = true;
        clearTimeout(startTimer);
        resolveReady(message.ready);
        return;
      }
      if (!pending || message.id !== pending.id) throw new Error(`${name} returned an unexpected response ID`);
      const current = pending;
      pending = null;
      clearTimeout(current.timer);
      if (message.error) current.reject(new Error(`${name}: ${message.error}`));
      else current.resolve(message);
    } catch (error) { fail(error); }
  });
  return {
    readiness,
    pid: child.pid,
    get available() { return ready && !stopped && !terminalError; },
    get error() { return terminalError?.message ?? null; },
    request(payload) {
      if (!ready || stopped || terminalError) return Promise.reject(terminalError || new Error(`${name} worker is unavailable`));
      if (pending) return Promise.reject(new Error(`${name} worker is busy`));
      const id = ++sequence;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => fail(new Error(`${name} request timed out`)), requestMs);
        pending = {id, resolve, reject, timer};
        child.stdin.write(JSON.stringify({...payload, id}) + '\n', error => { if (error) fail(error); });
      });
    },
    close() { fail(new Error(`${name} worker closed`)); return exited; },
  };
}

export async function startWorkers({root, checkpoint, python, sttPython, sttModel, signal}) {
  signal?.throwIfAborted();
  const sourceRoot = path.resolve(root);
  const modelRoot = path.resolve(checkpoint);
  const stamp = JSON.parse(await readFile(path.join(modelRoot, 'brainwash-training.json'), 'utf8'));
  const config = JSON.parse(await readFile(path.join(modelRoot, 'rl_agent_config.json'), 'utf8'));
  if (stamp.epoch !== 4 || stamp.modelSha256 !== ORIGINAL_WEIGHTS_SHA256 ||
      config.training?.protocol !== 3 || config.training?.language !== 'en' || config.training?.epoch !== 4) {
    throw new Error('Prototype requires the preserved original English protocol-3 epoch-04 checkpoint');
  }
  signal?.throwIfAborted();
  const laya = startJsonWorker({python, script: path.join(sourceRoot, 'training/v3-worker.py'), args: [modelRoot], cwd: sourceRoot, name: 'Laya'});
  const stt = startJsonWorker({python: sttPython, script: path.join(sourceRoot, 'prototype/stt-worker.py'), args: [path.resolve(sttModel)], cwd: sourceRoot, name: 'STT'});
  const close = () => {
    signal?.removeEventListener('abort', abort);
    return Promise.all([laya.close(), stt.close()]);
  };
  const abort = () => { void close(); };
  signal?.addEventListener('abort', abort, {once: true});
  try {
    const [layaMetadata, sttMetadata] = await Promise.all([laya.readiness, stt.readiness]);
    signal?.throwIfAborted();
    if (layaMetadata.weightsSha256 !== ORIGINAL_WEIGHTS_SHA256) throw new Error('Original Laya checkpoint byte hash mismatch');
    return {
      metadata: {laya: {...layaMetadata, model: 'original English epoch 4'}, stt: sttMetadata},
      get available() { return laya.available && stt.available; },
      get error() { return laya.error || stt.error; },
      async ask(state, questions, phase) {
        const started = performance.now();
        const response = await laya.request({state, questions});
        return {
          phase,
          request: {model: layaMetadata.checkpoint + '@' + layaMetadata.weightsSha256, state, questions,
            runtime: {max_len: layaMetadata.max_len, head_max_len: layaMetadata.head_max_len}},
          response: response.response, responseText: response.responseText,
          inputAudit: response.inputAudit, inferenceMs: response.inferenceMs,
          latencyMs: performance.now() - started, cacheHit: false,
        };
      },
      async transcribe(wavBuffer) {
        if (!Buffer.isBuffer(wavBuffer) || wavBuffer.length < 44 || wavBuffer.length > MAX_AUDIO_BYTES) {
          throw new Error('Audio must be a WAV buffer between 44 and 2,000,000 bytes');
        }
        const result = await stt.request({audio: wavBuffer.toString('base64')});
        const {text, tokens, timestamps, sttMs, audioSeconds} = result;
        return {text, tokens, timestamps, sttMs, audioSeconds};
      },
      close,
    };
  } catch (error) { await close(); throw error; }
}
