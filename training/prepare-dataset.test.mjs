import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, readFile, writeFile, rm, access} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {prepareDataset} from './prepare-dataset.mjs';

const command = (id, transcript, pose) => ({id, family: id, transcript, scene: 'imp', intent_count: 'one', outcome: 'order',
  actions: [{template: 'pose', spans: [{text: transcript, occurrence: 0}], slots: [], variants: [{name: 'pose', value: pose, evidence: [{text: transcript, occurrence: 0}]}]}],
  excluded_spans: [], tags: ['public-fixture'], design_refs: [], rationale: 'An explicit gesture for a contract test.'});

async function fixture(t, mutate = () => {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'local-action-data-'));
  t.after(() => rm(root, {recursive: true, force: true}));
  const train = command('train-dance', 'Dance', 'dance'), validation = command('validation-bow', 'Bow', 'bow');
  mutate(train, validation);
  for (const [name, row] of Object.entries({train, validation})) await writeFile(path.join(root, name + '.jsonl'), JSON.stringify(row) + '\n');
  return {train: path.join(root, 'train.jsonl'), validation: path.join(root, 'validation.jsonl'), output: path.join(root, 'prepared')};
}

test('exports explicit splits with hashes and no model or tokenizer claim', async t => {
  const options = await fixture(t);
  const manifest = await prepareDataset(options);
  assert.equal(manifest.trainingStarted, false);
  assert.equal(manifest.tokenizerAudited, false);
  assert.equal(manifest.artifacts.length, 4);
  const rows = (await readFile(path.join(options.output, 'decisions.validation.jsonl'), 'utf8')).trim().split('\n').map(JSON.parse);
  assert(rows.every(row => row.provenance.split === 'validation'));
  assert.equal(manifest.inputs.train.commands, 1);
});

test('read-only check does not create output', async t => {
  const options = await fixture(t);
  await prepareDataset({...options, checkOnly: true});
  await assert.rejects(access(options.output));
});

test('rejects cross-split semantic families before writing', async t => {
  const options = await fixture(t, (train, validation) => { validation.family = train.family; });
  await assert.rejects(prepareDataset(options), /family crosses splits/);
  await assert.rejects(access(options.output));
});

test('rejects duplicate transcripts and conflicting provenance', async t => {
  const options = await fixture(t, (train, validation) => { Object.assign(validation, train, {id: 'different', family: 'different'}); });
  await assert.rejects(prepareDataset(options), /Duplicate normalized transcript/);
  const other = await fixture(t, (train, validation) => { validation.provenance = {split: 'train'}; });
  await assert.rejects(prepareDataset(other), /Conflicting split provenance/);
});

test('never replaces an existing output', async t => {
  const options = await fixture(t);
  await prepareDataset(options);
  await assert.rejects(prepareDataset(options), /Output already exists/);
});
