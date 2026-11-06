import test from 'node:test';
import assert from 'node:assert/strict';
import {once} from 'node:events';
import {request as httpRequest} from 'node:http';
import {createUnityService} from './unity-server.mjs';
import {fixture, Stop} from '../eval/registry.mjs';

// Request isolation tests use injected workers. They load no model or microphone.
const deferred = () => {let resolve; const promise = new Promise(done => {resolve = done;}); return {promise, resolve};};
function recording(seconds, marker = 0) {
  const pcmBytes = seconds * 16000 * 2, bytes = Buffer.alloc(44 + pcmBytes);
  bytes.write('RIFF'); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22);
  bytes.writeUInt32LE(16000, 24); bytes.writeUInt32LE(32000, 28); bytes.writeUInt16LE(2, 32);
  bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(pcmBytes, 40);
  bytes.writeInt16LE(marker, 44); return bytes;
}
const audioRequest = (id, bytes = recording(3)) => ({requestId: id, scene: fixture('imp:empty'), audioBase64: bytes.toString('base64')});
const accepted = () => ({outcome: {kind: 'order', steps: [Stop()]}, actions: [{template: 'stop'}]});
async function start(t, transcribe, interpret = async () => accepted()) {
  const service = createUnityService({workers: {available: true, metadata: {laya: {}, stt: {}}, transcribe, close() {}}, interpret});
  service.server.listen(0, '127.0.0.1'); await once(service.server, 'listening');
  t.after(() => service.close());
  const url = 'http://127.0.0.1:' + service.server.address().port + '/api/command';
  const send = async value => {
    const response = await fetch(url, {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(value)});
    return {status: response.status, data: await response.json()};
  };
  return {...service, url, send};
}

test('released three-second and eight-second recordings reach STT intact with distinct leases', async t => {
  const received = [], interpreted = [];
  const app = await start(t, async bytes => {
    received.push(bytes); return {text: bytes.readInt16LE(44) === 12 ? 'stop' : 'stop now', sttMs: 1};
  }, async text => {interpreted.push(text); return accepted();});
  const three = recording(3, 12), eight = recording(8, 34);
  const first = await app.send(audioRequest('three-second-release', three));
  const second = await app.send(audioRequest('eight-second-auto-release', eight));
  assert.equal(first.status, 200); assert.equal(second.status, 200);
  assert.deepEqual(received, [three, eight]); assert.deepEqual(interpreted, ['stop', 'stop now']);
  assert.equal(first.data.compiled.directive.lease_id, 'three-second-release');
  assert.equal(second.data.compiled.directive.lease_id, 'eight-second-auto-release');
});

test('empty recognition after a text command cannot reuse its text and the next voice cast remains independent', async t => {
  const interpreted = []; let capture = 0;
  const app = await start(t, async () => ({text: ++capture === 1 ? '   ' : 'stop now', sttMs: 1}),
    async text => {interpreted.push(text); return accepted();});
  const typed = await app.send({requestId: 'old-text', scene: fixture('imp:empty'), text: 'old typed command'});
  assert.equal(typed.status, 200);
  const empty = await app.send(audioRequest('silent-release'));
  assert.equal(empty.data.requestId, 'silent-release'); assert.equal(empty.data.transcript, '');
  assert.notEqual(empty.data.compiled?.disposition, 'execute');
  assert.deepEqual(interpreted, ['old typed command']);
  const next = await app.send(audioRequest('next-voice'));
  assert.equal(next.status, 200); assert.equal(next.data.transcript, 'stop now');
  assert.equal(next.data.compiled.directive.lease_id, 'next-voice');
  assert.deepEqual(interpreted, ['old typed command', 'stop now']);
});

test('capture decoding failure cannot execute a previous voice command or poison a later cast', async t => {
  const interpreted = []; let capture = 0;
  const app = await start(t, async () => {
    if (++capture === 2) throw new Error('Fixture capture decoding failed');
    return {text: capture === 1 ? 'stop' : 'stop again', sttMs: 1};
  }, async text => {interpreted.push(text); return accepted();});
  assert.equal((await app.send(audioRequest('first-voice'))).status, 200);
  const broken = await app.send(audioRequest('broken-voice'));
  assert.equal(broken.data.requestId, 'broken-voice'); assert.equal(broken.data.transcript, '');
  assert.equal(broken.data.compiled, null); assert.match(broken.data.error, /decoding failed/);
  const next = await app.send(audioRequest('recovered-voice'));
  assert.equal(next.status, 200); assert.equal(next.data.transcript, 'stop again');
  assert.deepEqual(interpreted, ['stop', 'stop again']);
});

test('cancelled STT drains without interpreting stale speech or contaminating the next release', async t => {
  const entered = deferred(), finish = deferred(), interpreted = [];
  let captures = 0;
  const app = await start(t, async () => {
    if (++captures === 1) {entered.resolve(); return finish.promise;}
    return {text: 'new recording', sttMs: 1};
  }, async text => {interpreted.push(text); return accepted();});
  const connected = once(app.server, 'connection');
  const request = httpRequest(app.url, {method: 'POST', headers: {'Content-Type': 'application/json'}});
  request.on('error', () => {}); request.end(JSON.stringify(audioRequest('cancelled-release')));
  const [socket] = await connected; await entered.promise;
  const disconnected = once(socket, 'close'); request.destroy(); await disconnected;
  assert.equal(app.health().busy, true);
  // The next release waits for the draining recognition instead of being refused.
  const queued = app.send(audioRequest('next-release'));
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.equal(captures, 1); assert.deepEqual(interpreted, []);
  finish.resolve({text: 'discard this old speech', sttMs: 1});
  const next = await queued;
  assert.equal(next.status, 200); assert.equal(next.data.transcript, 'new recording');
  assert.equal(next.data.compiled.directive.lease_id, 'next-release');
  assert.deepEqual(interpreted, ['new recording']); assert.equal(app.health().busy, false);
});
