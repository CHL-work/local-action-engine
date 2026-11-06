// Portable public dataset preparation. No model, provider, or trainer is loaded.
import {readFile, mkdir, writeFile, stat} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import {validateCase} from './contract.mjs';
import {compileCase} from './compile.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const jsonl = rows => rows.map(row => JSON.stringify(row)).join('\n') + '\n';

export async function prepareDataset({train, validation, output, checkOnly = false}) {
  if (!train || !validation || (!checkOnly && !output)) {
    throw new Error('Supply explicit train and validation files, plus a fresh output directory unless checking only.');
  }
  if (path.resolve(train) === path.resolve(validation)) throw new Error('Train and validation must be separate files.');
  const ids = new Set(), families = new Map(), transcripts = new Map(), inputs = {}, compiled = {};
  for (const [split, filename] of Object.entries({train, validation})) {
    const bytes = await readFile(filename);
    const lines = bytes.toString('utf8').replace(/^\uFEFF/, '').trim().split(/\r?\n/);
    if (!lines[0]) throw new Error(`${split}: empty dataset`);
    const commands = lines.map((line, index) => {
      let item;
      try { item = JSON.parse(line); } catch { throw new Error(`${split}:${index + 1}: invalid JSON`); }
      const errors = validateCase(item);
      if (errors.length) throw new Error(`${split}:${index + 1}: ${errors.join('; ')}`);
      if (!item.id.trim() || !item.family.trim()) throw new Error(`${split}:${index + 1}: nonempty id and family required`);
      if (ids.has(item.id)) throw new Error(`Duplicate command id: ${item.id}`);
      ids.add(item.id);
      if (families.has(item.family) && families.get(item.family) !== split) {
        throw new Error(`Semantic family crosses splits: ${item.family}`);
      }
      families.set(item.family, split);
      const normalized = item.transcript.normalize('NFKC').toLowerCase().replace(/\s+/g, ' ').trim();
      if (transcripts.has(normalized)) throw new Error(`Duplicate normalized transcript: ${item.id}`);
      transcripts.set(normalized, split);
      if (item.provenance?.split && item.provenance.split !== split) throw new Error(`Conflicting split provenance: ${item.id}`);
      return {...item, provenance: {...item.provenance, split}};
    });
    const decisions = commands.flatMap(compileCase);
    if (!decisions.length) throw new Error(`${split}: no compiled decisions`);
    inputs[split] = {file: path.basename(filename), sha256: hash(bytes), commands: commands.length, decisions: decisions.length};
    compiled[split] = {commands, decisions};
  }
  const files = {};
  for (const [split, contents] of Object.entries(compiled)) {
    for (const [kind, rows] of Object.entries(contents)) files[`${kind}.${split}.jsonl`] = jsonl(rows);
  }
  const manifest = {
    version: 1, inputFormat: 'marked_word_and_focus_text_v1', protocol: 3,
    inputs, artifacts: Object.entries(files).map(([file, contents]) => ({file, sha256: hash(contents)})),
    modelLoaded: false, trainingStarted: false, tokenizerAudited: false,
    limitations: [
      'Structural and split checks do not establish human label correctness.',
      'Downstream training decisions use annotated parent actions and spans; this is not end-to-end accuracy.',
      'Token budget checks against the chosen checkpoint are still required before training.',
      'This export does not satisfy the archived campaign readiness contract or authorize its launch.'
    ]
  };
  if (!checkOnly) {
    const destination = path.resolve(output);
    if (await stat(destination).then(() => true, error => { if (error.code === 'ENOENT') return false; throw error; })) {
      throw new Error('Output already exists; select a new versioned dataset directory.');
    }
    await mkdir(path.dirname(destination), {recursive: true});
    await mkdir(destination);
    for (const [filename, contents] of Object.entries(files)) await writeFile(path.join(destination, filename), contents, {flag: 'wx'});
    await writeFile(path.join(destination, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', {flag: 'wx'});
  }
  return manifest;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = {};
    for (let index = 2; index < process.argv.length; index++) {
      const arg = process.argv[index];
      if (arg === '--check-only') options.checkOnly = true;
      else if (['--train', '--validation', '--output'].includes(arg) && process.argv[index + 1] && !process.argv[index + 1].startsWith('--')) options[arg.slice(2)] = process.argv[++index];
      else throw new Error('Usage: node training/prepare-dataset.mjs --train <commands.jsonl> --validation <commands.jsonl> [--output <fresh-directory> | --check-only]');
    }
    console.log(JSON.stringify(await prepareDataset(options), null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
