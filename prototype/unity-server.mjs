import http from 'node:http';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {inferV3, envelope} from '../training/v3-inference.mjs';
import {compileDirective} from '../eval/host.mjs';
import {CONTRACT} from '../training/schema.mjs';

const sourceRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const MAX_BODY = 2_400_000;
const MAX_AUDIO = 1_600_000;
const ID = /^[a-zA-Z0-9-]{1,80}$/;
const validId = value => typeof value === 'string' && ID.test(value);
const validHandle = value => typeof value === 'string' && /^[a-zA-Z0-9_-]{1,80}$/.test(value);
const fault = (message, status = 400) => Object.assign(new Error(message), {status});
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const text = (value, max = 200) => typeof value === 'string' && value.length > 0 && value.length <= max;
const finite = value => typeof value === 'number' && Number.isFinite(value);

function reply(res, status, value) {
  if (res.writableEnded || res.destroyed) return;
  res.writeHead(status, {'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store',
    'X-Content-Type-Options': 'nosniff', 'Connection': status >= 400 ? 'close' : 'keep-alive'});
  res.end(JSON.stringify(value));
}

function readBody(req, signal, max = MAX_BODY) {
  return new Promise((resolve, reject) => {
    let size = 0, settled = false;
    const chunks = [];
    const finish = (error, result) => {
      if (settled) return;
      settled = true;
      req.off('data', data); req.off('end', end); req.off('error', errorHandler); req.off('aborted', aborted);
      signal?.removeEventListener('abort', aborted);
      if (error) { req.resume(); reject(error); } else resolve(result);
    };
    const data = chunk => { size += chunk.length; if (size > max) finish(fault('Request exceeds the input limit.', 413)); else chunks.push(chunk); };
    const end = () => finish(null, Buffer.concat(chunks));
    const errorHandler = error => finish(error);
    const aborted = () => finish(signal?.reason instanceof Error ? signal.reason : fault('Request cancelled.', 499));
    req.on('data', data); req.on('end', end); req.on('error', errorHandler); req.on('aborted', aborted);
    signal?.addEventListener('abort', aborted, {once: true});
    if (signal?.aborted) aborted();
  });
}

// The host snapshot comes from Unity. Validate its bounds before question builders
// use labels/candidate handles; this service never samples a separate game world.
export function validateScene(input) {
  if (!object(input) || Buffer.byteLength(JSON.stringify(input)) > 128_000) throw fault('A bounded scene snapshot is required.');
  const a = input.actor;
  if (!object(a) || !validHandle(a.handle) || !text(a.species, 40) || !Array.isArray(a.skills) || a.skills.length > 16 ||
      !a.skills.every(s => text(s, 40)) || !Array.isArray(a.cooldown) || a.cooldown.length > 16 || !a.cooldown.every(s => text(s, 40))) {
    throw fault('Scene actor requires handle, species, skills and cooldown.');
  }
  if (input.status !== 'Thinking' || input.leaseValid !== true || input.controlSeconds !== 5) {
    throw fault('Scene must represent an owned Thinking lease with a five-second control window.');
  }
  if (!object(input.speaker) || !text(input.speaker.label) || !text(input.speaker.color, 40)) throw fault('Scene speaker is required.');
  for (const list of ['world', 'perceived']) {
    if (!Array.isArray(input[list]) || input[list].length > 64) throw fault('Scene world and perceived lists must contain at most 64 entities.');
    const handles = new Set();
    for (const e of input[list]) {
      if (!object(e) || !validHandle(e.handle) || handles.has(e.handle) || !text(e.label) ||
          !['player', 'monster', 'item', 'object', 'remains'].includes(e.kind) ||
          !finite(e.distanceWidths) || e.distanceWidths < 0 || e.distanceWidths > 10000 ||
          typeof e.visible !== 'boolean' || typeof e.los !== 'boolean') throw fault('Scene contains an invalid or duplicate entity.');
      handles.add(e.handle);
    }
  }
  const worldHandles = new Set(input.world.map(e => e.handle));
  if (input.perceived.some(e => !worldHandles.has(e.handle))) throw fault('Perceived handles must belong to the supplied world snapshot.');
  if (a.inventory !== undefined && (!Array.isArray(a.inventory) || a.inventory.length > 16 ||
      a.inventory.some(e => !object(e) || !validHandle(e.handle) || !text(e.itemKind, 40)))) throw fault('Scene inventory is invalid.');
  // Only these records enter the runtime. Strip nested Unity component/debug data,
  // preserving ordinary numeric positions and the registry's observable facts.
  const entityFields = ['handle','kind','label','visible','los','distanceWidths','color','alive','species','size','health','held','itemKind','objectKind','destructible','axis','x','y','z'];
  const pick = (value, fields) => Object.fromEntries(fields.filter(k => value[k] !== undefined).map(k => [k, value[k]]));
  const actor = pick(a, ['handle','species','skills','cooldown','held','inventory','canFly','canCarry','canEat','triggerReady']);
  for (const list of ['world','perceived']) for (const e of input[list]) {
    for (const key of entityFields) if (e[key] !== undefined && !['string','boolean','number'].includes(typeof e[key]) && e[key] !== null) throw fault('Scene entity facts must be scalar values.');
  }
  for (const flag of ['canFly','canCarry','canEat','triggerReady']) if (actor[flag] !== undefined && typeof actor[flag] !== 'boolean') throw fault('Actor capability flags must be booleans.');
  if (actor.held !== undefined && actor.held !== null && !text(actor.held, 40)) throw fault('Actor held item must be a kind or null.');
  actor.inventory = (actor.inventory ?? []).map(item => pick(item, ['handle','itemKind','transferable','usable','throwable']));
  return structuredClone({id: text(input.id, 80) ? input.id : 'unity-scene', actor,
    speaker: pick(input.speaker, ['color','label']), world: input.world.map(e => pick(e, entityFields)),
    perceived: input.perceived.map(e => pick(e, entityFields)), controlSeconds: 5, status: 'Thinking', leaseValid: true});
}

export function decodeAudio(encoded) {
  if (typeof encoded !== 'string' || encoded.length > Math.ceil(MAX_AUDIO / 3) * 4 || encoded.length % 4 ||
      !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)) throw fault('audioBase64 must contain one bounded WAV recording.');
  const bytes = Buffer.from(encoded, 'base64');
  if (bytes.length < 44 || bytes.length > MAX_AUDIO || bytes.toString('base64') !== encoded ||
      bytes.toString('ascii', 0, 4) !== 'RIFF' || bytes.toString('ascii', 8, 12) !== 'WAVE' || bytes.readUInt32LE(4) + 8 !== bytes.length) {
    throw fault('Audio must be a complete PCM WAV recording.');
  }
  let format = null, samples = null, count = 0;
  for (let offset = 12; offset < bytes.length;) {
    if (++count > 128 || offset + 8 > bytes.length) throw fault('Malformed WAV chunks.');
    const name = bytes.toString('ascii', offset, offset + 4), length = bytes.readUInt32LE(offset + 4), start = offset + 8;
    if (start + length + length % 2 > bytes.length) throw fault('Truncated WAV chunk.');
    if (name === 'fmt ') {
      if (format || length < 16) throw fault('Invalid WAV format chunk.');
      format = {codec: bytes.readUInt16LE(start), channels: bytes.readUInt16LE(start + 2), rate: bytes.readUInt32LE(start + 4),
        byteRate: bytes.readUInt32LE(start + 8), alignment: bytes.readUInt16LE(start + 12), bits: bytes.readUInt16LE(start + 14)};
    }
    if (name === 'data') { if (samples !== null) throw fault('Multiple WAV recordings are not supported.'); samples = length; }
    offset = start + length + length % 2;
  }
  if (!format || format.codec !== 1 || format.channels !== 1 || format.bits !== 16 || format.alignment !== 2 ||
      format.rate < 8000 || format.rate > 96000 || format.byteRate !== format.rate * 2 || !samples || samples % 2 || samples / 2 / format.rate > 8) {
    throw fault('Expected mono PCM16 WAV at 8–96 kHz, at most eight seconds.');
  }
  return bytes;
}

// Each model worker answers one call at a time. Requests wait for it in arrival
// order instead of being refused, so every listener of a cast can be sent at
// once. A request whose deadline passes or whose client leaves while waiting
// never starts; one already running finishes its current worker call first.
export function serialGate() {
  let tail = Promise.resolve(), waiting = 0, running = false;
  return {
    get busy() { return running || waiting > 0; },
    get waiting() { return waiting; },
    acquire(signal) {
      let pass;
      const done = new Promise(resolve => { pass = resolve; });
      const previous = tail;
      tail = previous.then(() => done);
      waiting++;
      return new Promise((resolve, reject) => {
        let settled = false;
        const abandon = () => {
          if (settled) return;
          settled = true; waiting--;
          void previous.then(pass); // keep the line moving for later requests
          reject(signal?.reason instanceof Error ? signal.reason : fault('Request cancelled.', 499));
        };
        if (signal?.aborted) { abandon(); return; }
        signal?.addEventListener('abort', abandon, {once: true});
        void previous.then(() => {
          if (settled) return;
          settled = true; waiting--; running = true;
          signal?.removeEventListener('abort', abandon);
          let released = false;
          resolve(() => { if (released) return; released = true; running = false; pass(); });
        });
      });
    },
  };
}

export function createUnityService({workers = null, interpret = (transcript, scene, ask) => inferV3(transcript, 'unity', ask, {scene}),
  deadlineMs = 5000, now = () => performance.now(), onShutdown, memoEntries = 512, memoMs = 120_000} = {}) {
  let runtime = workers, startupError = null, stopping = false, closed = null;
  const active = new Set(), speech = serialGate(), laya = serialGate();
  // Several listeners of one cast usually hear the same words. A question whose
  // complete input (state and choices) was already answered by this model is
  // not asked again; questions that include a listener's own perception differ
  // and are always asked. Bounded in size and age, never written to disk.
  let memo = new Map();
  const recall = key => {
    const hit = memo.get(key);
    if (!hit) return null;
    memo.delete(key);
    if (now() - hit.at > memoMs) return null;
    memo.set(key, hit); return hit.value;
  };
  const remember = (key, value) => {
    memo.set(key, {value, at: now()});
    while (memo.size > memoEntries) memo.delete(memo.keys().next().value);
  };
  const health = () => ({service: 'brainwash-unity-inference-v1', ready: !stopping && !!runtime && runtime.available !== false && !startupError,
    busy: speech.busy || laya.busy, queued: speech.waiting + laya.waiting, error: startupError || runtime?.error || null, model: runtime?.metadata?.laya ?? null,
    stt: runtime?.metadata?.stt ?? null, thinkingDeadlineMs: deadlineMs, maxAudioSeconds: 8,
    // Games check these before sending several listeners of one cast at once.
    features: ['queue', 'shared-questions', 'budget']});
  const server = http.createServer(async (req, res) => {
    const received = now();
    let requestId = null, transcript = '', phase = 'input', sttDetail = null;
    const timings = {sttMs: 0, interpretMs: 0, totalMs: 0, questionCount: 0, cachedQuestions: 0, queueMs: 0};
    const response = (error, extras = {}) => ({requestId, transcript, outcome: null, compiled: null, actions: [],
      timings: {...timings, totalMs: now() - received}, model: runtime?.metadata?.laya ?? null,
      stt: sttDetail ? runtime?.metadata?.stt ?? null : null, ...(error ? {error} : {}), ...extras});
    try {
      const port = req.socket.localPort, host = req.headers.host ?? '';
      if (!['127.0.0.1','::1','::ffff:127.0.0.1'].includes(req.socket.remoteAddress) ||
          ![`127.0.0.1:${port}`, `localhost:${port}`].includes(host)) throw fault('Only loopback requests with the local Host are accepted.', 403);
      if (req.headers.origin && req.headers.origin !== `http://${host}`) throw fault('Cross-origin requests are disabled.', 403);
      const url = new URL(req.url, `http://${host}`);
      if (req.method === 'GET' && url.pathname === '/api/health') { reply(res, 200, health()); return; }
      if (req.method !== 'POST' || !['/api/command','/api/shutdown','/api/transcribe'].includes(url.pathname)) throw fault('Not found.', 404);
      if (req.headers['content-type']?.split(';')[0].trim().toLowerCase() !== 'application/json') throw fault('Content-Type must be application/json.', 415);
      if (url.pathname === '/api/shutdown') {
        if (req.headers['x-brainwash-unity'] !== '1') throw fault('The Unity service shutdown header is required.', 403);
        stopping = true;
        reply(res, 200, {stopping: true});
        setImmediate(() => { void Promise.resolve(onShutdown ? onShutdown() : close()).catch(error => console.error(error.message)); });
        return;
      }
      if (!health().ready) throw fault(startupError || runtime?.error || 'Models are not ready. Start the Unity inference service with the original local checkpoint.', 503);
      if (Number(req.headers['content-length'] ?? 0) > MAX_BODY) throw fault('Request exceeds the input limit.', 413);
      const controller = new AbortController(); active.add(controller);
      const deadlineError = fault('Thinking deadline exceeded. Unity must discard this command.', 504);
      let deadlineAt = received + deadlineMs, timer;
      const arm = () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
          controller.abort(deadlineError);
          reply(res, 504, response(deadlineError.message, {outcome: {kind: 'technical_failure', reason: phase === 'stt' ? 'stt_error' : 'inference_error'}}));
        }, Math.max(0, deadlineAt - now()));
      };
      arm();
      const ensure = () => {
        controller.signal.throwIfAborted();
        if (stopping || res.destroyed || now() >= deadlineAt) throw deadlineError;
      };
      // Waits its turn for one worker; the time spent waiting counts against the deadline.
      const turn = async gate => {
        const waited = now(), release = await gate.acquire(controller.signal);
        timings.queueMs += now() - waited;
        try { ensure(); } catch (error) { release(); throw error; }
        return release;
      };
      const transcribe = async wav => {
        phase = 'stt';
        const release = await turn(speech);
        try { sttDetail = await runtime.transcribe(wav); } finally { release(); }
        ensure(); timings.sttMs = sttDetail.sttMs;
        return sttDetail.text;
      };
      res.on('close', () => { if (!res.writableEnded) controller.abort(fault('Unity disconnected before the command completed.', 499)); });
      try {
        const bytes = await readBody(req, controller.signal); ensure();
        let payload;
        try { payload = JSON.parse(bytes.toString('utf8')); } catch { throw fault('Malformed JSON.'); }
        if (!object(payload) || !validId(payload.requestId)) throw fault('requestId must contain 1–80 letters, digits or hyphens.');
        requestId = payload.requestId;
        // Unity may send what is left of a listener's Thinking time; work that
        // cannot finish inside it is dropped instead of delaying later listeners.
        if (payload.budgetMs !== undefined && payload.budgetMs !== null) {
          if (!finite(payload.budgetMs) || payload.budgetMs <= 0) throw fault('budgetMs must be a positive number of milliseconds.');
          deadlineAt = Math.min(deadlineAt, received + payload.budgetMs); arm(); ensure();
        }
        if (url.pathname === '/api/transcribe') {
          // Speech only: chanting and the listeners of a cast share one
          // recognition per recorded window. No command interpretation runs here.
          if (typeof payload.audioBase64 !== 'string' || !payload.audioBase64.length) throw fault('audioBase64 is required.');
          const wav = decodeAudio(payload.audioBase64); ensure();
          const text = await transcribe(wav);
          transcript = typeof text === 'string' ? text.trim() : '';
          reply(res, 200, response(null, {transcript}));
          return;
        }
        const scene = validateScene(payload.scene);
        if (payload.text !== undefined && payload.text !== null && typeof payload.text !== 'string') throw fault('text must be a string.');
        if (payload.audioBase64 !== undefined && payload.audioBase64 !== null && typeof payload.audioBase64 !== 'string') throw fault('audioBase64 must be a string.');
        const hasText = typeof payload.text === 'string' && payload.text.trim().length > 0;
        const hasAudio = typeof payload.audioBase64 === 'string' && payload.audioBase64.length > 0;
        if (hasText === hasAudio) throw fault('Supply exactly one nonempty text or audioBase64 value.');
        if (hasAudio) {
          const wav = decodeAudio(payload.audioBase64); ensure();
          transcript = await transcribe(wav);
        } else transcript = payload.text;
        if (typeof transcript !== 'string' || transcript.length > 300) throw fault('Transcript must contain at most 300 characters.');
        transcript = transcript.trim();
        if (!transcript) throw fault('No words recognized. Empty transcription is not proof of silence.');
        phase = 'inference';
        const release = await turn(laya), began = now();
        let result;
        try {
          const ask = async (state, questions, stage) => {
            ensure();
            const key = JSON.stringify([state, questions]), cached = recall(key);
            timings.questionCount++;
            if (cached) { timings.cachedQuestions++; return {...cached, phase: stage, latencyMs: 0, cacheHit: true}; }
            const answer = await runtime.ask(state, questions, stage); ensure();
            remember(key, answer); return answer;
          };
          result = await interpret(transcript, scene, ask);
        } finally { release(); }
        ensure();
        timings.interpretMs = now() - began;
        const proposal = {...envelope(result.outcome), session_id: requestId, lease_id: requestId, snapshot_id: requestId, request_id: requestId};
        const compiled = compileDirective(proposal, scene, {contract: CONTRACT}); ensure();
        reply(res, 200, response(null, {outcome: result.outcome, compiled, actions: result.actions ?? [], overflow: !!result.overflow}));
      } catch (error) {
        reply(res, error.status ?? 500, response(error.message, {outcome: phase === 'input' ? null :
          {kind: 'technical_failure', reason: phase === 'stt' ? 'stt_error' : 'inference_error'}}));
      } finally {
        clearTimeout(timer); active.delete(controller);
      }
    } catch (error) { reply(res, error.status ?? 500, response(error.message)); }
  });
  server.requestTimeout = 10000; server.headersTimeout = 10000;
  server.on('clientError', (_, socket) => socket.end('HTTP/1.1 400 Bad Request\r\n\r\n'));
  const close = () => closed ??= (async () => {
    stopping = true; for (const controller of active) controller.abort(fault('Unity inference service is stopping.', 503));
    await runtime?.close(); server.closeAllConnections();
    if (server.listening) await new Promise(resolve => server.close(resolve));
  })();
  return {server, health, close, setWorkers(value) {runtime = value; startupError = null; memo = new Map();}, setError(error) {startupError = String(error);}};
}

async function main() {
  const port = Number(process.env.BRAINWASH_UNITY_PORT ?? 4174);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('BRAINWASH_UNITY_PORT must be between 1024 and 65535.');
  let loading, stopped = false;
  const startup = new AbortController();
  const stop = async () => {
    if (stopped) return;
    stopped = true; startup.abort(); await loading?.catch(() => {}); await app.close();
  };
  const app = createUnityService({onShutdown: stop});
  await new Promise((resolve, reject) => { app.server.once('error', reject); app.server.listen(port, '127.0.0.1', resolve); });
  console.log(`Unity inference service: http://127.0.0.1:${port} (models loading)`);
  process.on('SIGINT', stop); process.on('SIGTERM', stop);
  try {
    const {startWorkers} = await import('./workers.mjs');
    const local = process.env.BRAINWASH_LOCAL_ROOT ?? sourceRoot;
    loading = startWorkers({root: sourceRoot, signal: startup.signal,
      checkpoint: process.env.BRAINWASH_CHECKPOINT ?? path.join(local, '.local/models/brainwash-en-v3-opus55-2026-10-13/epoch-04'),
      python: process.env.BRAINWASH_PYTHON ?? path.join(local, '.local/laya-env/Scripts/python.exe'),
      sttPython: process.env.BRAINWASH_STT_PYTHON ?? path.join(local, '.local/prototype-english-stt-env/Scripts/python.exe'),
      sttModel: process.env.BRAINWASH_STT_MODEL ?? path.join(local, '.local/models/stt/parakeet-unified-en-0.6b-fp32-e69c9fb')});
    const workers = await loading;
    if (stopped) {await workers.close(); return;}
    app.setWorkers(workers); console.log('Unity service ready: preserved English epoch-04 + local CPU STT.');
  } catch (error) { if (!stopped) {app.setError(error.message); console.error('Unity service startup failed: ' + error.message);} }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => {console.error(error); process.exitCode = 1;});
}
