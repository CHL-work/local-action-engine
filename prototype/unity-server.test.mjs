import test from 'node:test';
import assert from 'node:assert/strict';
import {once} from 'node:events';
import {request as httpRequest} from 'node:http';
import {createUnityService, decodeAudio, validateScene} from './unity-server.mjs';
import {fixture, Stop} from '../eval/registry.mjs';
import {inferV3} from '../training/v3-inference.mjs';

const order = () => ({outcome: {kind: 'order', steps: [Stop()]}, actions: [{template: 'stop'}]});
const deferred = () => {let resolve; const promise = new Promise(done => {resolve = done;}); return {promise, resolve};};
const scene = () => fixture('imp:empty');
const payload = (extra = {}) => ({requestId: 'unity-request-1', text: 'stop', scene: scene(), ...extra});

function wav(seconds = 0.1, rate = 16000) {
  const size = Math.round(seconds * rate) * 2, bytes = Buffer.alloc(44 + size);
  bytes.write('RIFF'); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22);
  bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32);
  bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(size, 40);
  return bytes;
}

async function app(t, options = {}) {
  const workers = {available: true, metadata: {laya: {model: 'offline fixture'}, stt: {model: 'offline STT fixture'}},
    ask: async () => ({}), transcribe: async () => ({text: 'stop', tokens: [], timestamps: [], sttMs: 12, audioSeconds: 0.1}), close: async () => {}, ...options.workers};
  const service = createUnityService({workers, interpret: async () => order(), ...options});
  service.server.listen(0, '127.0.0.1'); await once(service.server, 'listening');
  t.after(() => service.close());
  const base = 'http://127.0.0.1:' + service.server.address().port;
  const request = async (route = '/api/command', {method = 'POST', value = payload(), headers = {}, body} = {}) => {
    const response = await fetch(base + route, {method, headers: {'Content-Type': 'application/json', ...headers},
      body: method === 'GET' ? undefined : body ?? JSON.stringify(value)});
    return {status: response.status, data: await response.json()};
  };
  return {...service, base, request};
}

test('health exposes pinned-runtime metadata and unavailable workers cannot interpret', async t => {
  const a = await app(t, {workers: null});
  const health = await a.request('/api/health', {method: 'GET'});
  assert.equal(health.data.service, 'brainwash-unity-inference-v1');
  assert.equal(health.data.ready, false); assert.equal(health.data.thinkingDeadlineMs, 5000);
  assert.equal(health.data.maxAudioSeconds, 8);
  assert.equal((await a.request()).status, 503);
});

test('typed commands use Unity snapshot facts, protocol3 validation and request-specific IDs', async t => {
  let received;
  const a = await app(t, {interpret: async (text, snapshot, ask) => {
    received = {text, snapshot}; await ask({text}, {q: {}}, 'fixture'); return order();
  }});
  const snapshot = scene(); snapshot.world.find(e => e.handle === 'k1').distanceWidths = 0.25;
  snapshot.perceived.find(e => e.handle === 'k1').distanceWidths = 0.25;
  const result = await a.request('/api/command', {value: payload({text: '  stop  ', scene: snapshot})});
  assert.equal(result.status, 200); assert.equal(result.data.requestId, 'unity-request-1');
  assert.equal(received.text, 'stop'); assert.equal(received.snapshot.world.find(e => e.handle === 'k1').distanceWidths, 0.25);
  assert.equal(result.data.compiled.processable, true); assert.equal(result.data.compiled.disposition, 'execute');
  assert.equal(result.data.compiled.directive.lease_id, 'unity-request-1');
  assert.deepEqual(result.data.compiled.directive.steps, [Stop()]);
  assert.equal(result.data.compiled.directive.tasks[0].sharesAbsoluteExpiry, true);
  assert.equal(result.data.timings.questionCount, 1); assert.equal(result.data.stt, null);
});

test('voice decoding and typed input share the identical interpreter/compiler path', async t => {
  const stages = []; let decoded;
  const a = await app(t, {workers: {metadata: {laya: {}, stt: {engine: 'fixture'}},
    transcribe: async bytes => {decoded = bytes; stages.push('STT'); return {text: 'stop', sttMs: 17};}, close() {}},
    interpret: async text => {stages.push(text); return order();}});
  const typed = await a.request();
  const audio = wav();
  const voice = await a.request('/api/command', {value: payload({text: '', audioBase64: audio.toString('base64')})});
  assert.equal(voice.status, 200); assert.deepEqual(decoded, audio);
  assert.deepEqual(stages, ['stop', 'STT', 'stop']);
  assert.deepEqual(typed.data.outcome, voice.data.outcome);
  assert.deepEqual(typed.data.compiled, voice.data.compiled);
  assert.equal(voice.data.timings.sttMs, 17); assert.equal(voice.data.stt.engine, 'fixture');
});

test('a timed-out ask drains before the next request starts and its late answer is never compiled', async t => {
  const entered = deferred(), finish = deferred(), calls = [];
  const a = await app(t, {deadlineMs: 60, workers: {metadata: {laya: {}, stt: {}},
    ask: async state => {calls.push(state.stage); if (state.stage === 'slow') {entered.resolve(); return finish.promise;} return {};}, close() {}},
    interpret: async (text, snapshot, ask) => {
      await ask({stage: text === 'stop' ? 'slow' : 'quick'}, {}, 'first'); await ask({stage: 'after ' + text}, {}, 'second'); return order();
    }});
  const pending = a.request(); await entered.promise;
  const timedOut = await pending;
  assert.equal(timedOut.status, 504); assert.equal(timedOut.data.compiled, null);
  assert.equal(timedOut.data.outcome.kind, 'technical_failure'); assert.equal(a.health().busy, true);
  // Another listener's request waits for the worker instead of being refused.
  const next = a.request('/api/command', {value: payload({requestId: 'unity-request-2', text: 'dance'})});
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.deepEqual(calls, ['slow']); assert.equal(a.health().queued, 1);
  finish.resolve({});
  const result = await next;
  assert.equal(result.status, 200); assert.equal(result.data.compiled.directive.lease_id, 'unity-request-2');
  assert.deepEqual(calls, ['slow', 'quick', 'after dance']); assert.equal(a.health().busy, false);
});

test('client disconnect drains an in-flight ask, skips its next stage and then serves the waiting request', async t => {
  const entered = deferred(), finish = deferred(); let questions = 0, completed = 0;
  const a = await app(t, {deadlineMs: 1000, workers: {metadata: {},
    ask: async () => {questions++; entered.resolve(); return finish.promise;}, close() {}},
    interpret: async (text, snapshot, ask) => {await ask({}, {}, 'first'); await ask({next: true}, {}, 'second'); completed++; return order();}});
  const connected = once(a.server, 'connection');
  const req = httpRequest(a.base + '/api/command', {method: 'POST', headers: {'Content-Type': 'application/json'}});
  req.on('error', () => {}); req.end(JSON.stringify(payload()));
  const [serverSocket] = await connected;
  await entered.promise;
  const disconnected = once(serverSocket, 'close');
  req.destroy(); await disconnected;
  assert.equal(a.health().busy, true);
  const next = a.request('/api/command', {value: payload({requestId: 'unity-request-2'})});
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(questions, 1);
  finish.resolve({});
  assert.equal((await next).status, 200);
  assert.equal(questions, 3); assert.equal(completed, 1); assert.equal(a.health().busy, false);
});

test('work whose budget runs out while it waits for the worker never starts', async t => {
  const entered = deferred(), finish = deferred(); let calls = 0;
  const a = await app(t, {deadlineMs: 1000, workers: {metadata: {}, ask: async () => {calls++; entered.resolve(); return finish.promise;}, close() {}},
    interpret: async (text, snapshot, ask) => {await ask({text}, {}, 'only'); return order();}});
  const first = a.request(); await entered.promise;
  const late = await a.request('/api/command', {value: payload({requestId: 'unity-request-2', text: 'dance', budgetMs: 30})});
  assert.equal(late.status, 504); assert.equal(late.data.compiled, null); assert.equal(calls, 1);
  finish.resolve({});
  assert.equal((await first).status, 200); assert.equal(calls, 1); assert.equal(a.health().busy, false);
  const invalid = await a.request('/api/command', {value: payload({requestId: 'unity-request-3', budgetMs: -5})});
  assert.equal(invalid.status, 400); assert.equal(calls, 1);
});

test('listeners that heard the same words share identical questions but each asks about its own perception', async t => {
  const asked = [];
  const a = await app(t, {workers: {metadata: {}, ask: async state => {asked.push(JSON.stringify(state)); return {};}, close() {}},
    interpret: async (text, snapshot, ask) => {
      await ask({player_transcript: text}, {action_count: {}}, 'structure');
      await ask({player_transcript: text, actor: snapshot.actor.handle}, {target: {}}, 'reference');
      return order();
    }});
  const second = scene(); second.actor.handle = 'imp-two';
  const [x, y] = await Promise.all([
    a.request('/api/command', {value: payload({requestId: 'listener-a', text: 'attack the goblin'})}),
    a.request('/api/command', {value: payload({requestId: 'listener-b', text: 'attack the goblin', scene: second})})]);
  assert.equal(x.status, 200); assert.equal(y.status, 200);
  assert.equal(x.data.timings.questionCount + y.data.timings.questionCount, 4);
  assert.equal(x.data.timings.cachedQuestions + y.data.timings.cachedQuestions, 1);
  assert.equal(asked.length, 3); assert.equal(new Set(asked).size, 3);
  // Different words are different questions.
  await a.request('/api/command', {value: payload({requestId: 'listener-c', text: 'dance'})});
  assert.equal(asked.length, 5);
});

test('a late STT result cannot enter interpretation after the shared deadline', async t => {
  const entered = deferred(), finish = deferred(); let interpreted = 0;
  const a = await app(t, {deadlineMs: 50, workers: {metadata: {},
    transcribe: async () => {entered.resolve(); return finish.promise;}, close() {}},
    interpret: async () => {interpreted++; return order();}});
  const pending = a.request('/api/command', {value: payload({text: null, audioBase64: wav().toString('base64')})});
  await entered.promise;
  const result = await pending;
  assert.equal(result.status, 504); assert.equal(result.data.compiled, null);
  assert.equal(result.data.outcome.reason, 'stt_error'); assert.equal(a.health().busy, true);
  finish.resolve({text: 'stop', sttMs: 70});
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(interpreted, 0); assert.equal(a.health().busy, false);
});

test('five-second budget starts before body reading and rejects a stalled upload', async t => {
  let interpreted = false;
  const a = await app(t, {deadlineMs: 30, interpret: async () => {interpreted = true; return order();}});
  const result = await new Promise((resolve, reject) => {
    const req = httpRequest(a.base + '/api/command', {method: 'POST', headers: {'Content-Type': 'application/json', 'Content-Length': 100}}, res => {
      let data = ''; res.on('data', chunk => {data += chunk;}); res.on('end', () => {resolve({status: res.statusCode, data: JSON.parse(data)}); req.destroy();});
    });
    req.on('error', reject); req.flushHeaders();
  });
  assert.equal(result.status, 504); assert.equal(result.data.compiled, null); assert.equal(interpreted, false);
});

test('loopback service rejects external Origin, rebinding Host, wrong content type and unguarded stop', async t => {
  let interpreted = false;
  const a = await app(t, {interpret: async () => {interpreted = true; return order();}});
  assert.equal((await a.request('/api/command', {headers: {Origin: 'https://example.com'}})).status, 403);
  const status = await new Promise((resolve, reject) => {
    const req = httpRequest(a.base + '/api/health', {headers: {Host: 'attacker.example:4174'}}, res => {res.resume(); res.on('end', () => resolve(res.statusCode));});
    req.on('error', reject); req.end();
  });
  assert.equal(status, 403);
  assert.equal((await a.request('/api/command', {headers: {'Content-Type': 'text/plain'}})).status, 415);
  assert.equal((await a.request('/api/shutdown')).status, 403);
  assert.equal(interpreted, false);
});

test('invalid scene, missing ID, dual inputs and invalid WAV never reach a model', async t => {
  let calls = 0;
  const a = await app(t, {workers: {metadata: {}, transcribe: async () => {calls++;}, close() {}}, interpret: async () => {calls++; return order();}});
  const invalid = scene(); invalid.perceived.push({...invalid.perceived[0], handle: 'absent-in-world'});
  for (const value of [payload({scene: invalid}), payload({requestId: undefined}), payload({text: 'stop', audioBase64: wav().toString('base64')}),
    payload({text: null, audioBase64: wav(8.01).toString('base64')}), payload({text: null, audioBase64: 'not base64'})]) {
    assert.equal((await a.request('/api/command', {value})).status, 400);
  }
  assert.equal(calls, 0);
});

test('STT errors stay technical failures and empty recognition never executes a default', async t => {
  let calls = 0, fail = true;
  const a = await app(t, {workers: {metadata: {}, transcribe: async () => {if (fail) throw new Error('Decoder failed'); return {text: '', sttMs: 1};}, close() {}},
    interpret: async () => {calls++; return order();}});
  const value = payload({text: null, audioBase64: wav().toString('base64')});
  const error = await a.request('/api/command', {value});
  assert.equal(error.status, 500); assert.deepEqual(error.data.outcome, {kind: 'technical_failure', reason: 'stt_error'});
  assert.equal(error.data.compiled, null);
  fail = false;
  const empty = await a.request('/api/command', {value});
  assert.equal(empty.status, 400); assert.match(empty.data.error, /not proof of silence/); assert.equal(calls, 0);
});

test('scene copy isolates Unity mutations and keeps only bounded facts', () => {
  const original = scene(); original.actor.debug = {deep: ['never included']};
  const copy = validateScene(original); original.actor.skills.length = 0; original.world[0].label = 'changed';
  assert.deepEqual(copy.actor.skills, ['fireball']); assert.notEqual(copy.world[0].label, 'changed'); assert.equal(copy.actor.debug, undefined);
  assert.throws(() => validateScene({...original, status: 'Normal'}), /Thinking/);
});

test('real question builders expose perceived labels but never hidden world labels', async t => {
  const requests = [];
  const ask = async (state, questions) => {
    requests.push({state, questions});
    const [id, q] = Object.entries(questions)[0];
    let choice = {action_count: 'one', word_role: 'action_0', meaning: 'order', action_family: 'combat_magic',
      action_kind: 'attack', s0_mode: 'single', s0_duration: 'unspecified', s0_target_presence: 'supplied', s0_target: 'e:g1'}[id];
    if (id.startsWith('s0_duration_group')) choice = 'unstated';
    if (id.startsWith('s0_target_group')) choice = 'observed_entity' in q.criteria ? 'observed_entity' : 'monster';
    assert.ok(choice in q.criteria, id + ': ' + choice);
    return {response: {answers: {[id]: {choice,
      probabilities: Object.fromEntries(Object.keys(q.criteria).map(key => [key, Number(key === choice)]))}}}};
  };
  const a = await app(t, {workers: {metadata: {}, ask, close() {}},
    interpret: (text, snapshot, decision) => inferV3(text, 'unity', decision, {scene: snapshot})});
  const snapshot = scene();
  snapshot.world.push({handle: 'hidden-secret', kind: 'monster', species: 'goblin', label: 'SECRET-HIDDEN-LABEL',
    visible: false, los: false, distanceWidths: 20});
  const result = await a.request('/api/command', {value: payload({text: 'hit the green goblin once', scene: snapshot})});
  assert.equal(result.status, 200);
  assert.equal(result.data.compiled.directive.tasks[0].bindings.target.handle, 'g1');
  assert.doesNotMatch(JSON.stringify(requests), /SECRET-HIDDEN-LABEL|hidden-secret/);
  assert.match(JSON.stringify(requests), /small green goblin/);
});

test('WAV validation enforces the duration using PCM data, not caller metadata', () => {
  assert.deepEqual(decodeAudio(wav(8).toString('base64')), wav(8));
  assert.throws(() => decodeAudio(wav(8.01).toString('base64')), /eight seconds/);
  const stereo = wav(); stereo.writeUInt16LE(2, 22);
  assert.throws(() => decodeAudio(stereo.toString('base64')), /mono PCM16/);
  assert.throws(() => decodeAudio(wav().subarray(0, 80).toString('base64')), /complete PCM WAV/);
});

test('oversized body is rejected before reading or inference', async t => {
  const a = await app(t);
  const status = await new Promise((resolve, reject) => {
    const req = httpRequest(a.base + '/api/command', {method: 'POST', headers: {'Content-Type': 'application/json', 'Content-Length': 2_400_001}}, res => {
      res.resume(); res.on('end', () => {resolve(res.statusCode); req.destroy();});
    });
    req.on('error', reject); req.flushHeaders();
  });
  assert.equal(status, 413); assert.equal(a.health().busy, false);
});

test('guarded shutdown returns success and stops accepting requests', async t => {
  let stopped = false;
  const a = await app(t, {onShutdown: async () => {stopped = true;}});
  const result = await a.request('/api/shutdown', {headers: {'X-Brainwash-Unity': '1'}});
  assert.equal(result.status, 200); await new Promise(resolve => setImmediate(resolve));
  assert.equal(stopped, true); assert.equal(a.health().ready, false);
});

test('chant transcription returns words only and never interprets them', async t => {
  let interpreted = false;
  const a = await app(t, {workers: {metadata: {laya: {}, stt: {engine: 'fixture'}},
    transcribe: async () => ({text: '  By the eye of the faithful  ', sttMs: 9}), close() {}},
    interpret: async () => {interpreted = true; return order();}});
  const result = await a.request('/api/transcribe', {value: {requestId: 'chant-1', audioBase64: wav().toString('base64')}});
  assert.equal(result.status, 200); assert.equal(result.data.transcript, 'By the eye of the faithful');
  assert.equal(result.data.compiled, null); assert.equal(interpreted, false);
  assert.equal(result.data.timings.sttMs, 9); assert.equal(a.health().busy, false);
  const missing = await a.request('/api/transcribe', {value: {requestId: 'chant-2'}});
  assert.equal(missing.status, 400);
});
