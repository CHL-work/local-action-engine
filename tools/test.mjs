import {readdirSync, existsSync} from 'node:fs';
import {spawnSync} from 'node:child_process';

// Enumerate explicitly: shell glob expansion differs on Windows and Linux.
const files = ['prototype', 'training', 'tests'].flatMap(dir => existsSync(dir)
  ? readdirSync(dir).filter(name => name.endsWith('.test.mjs')).map(name => `${dir}/${name}`) : []);
if (!files.length) throw new Error('No offline tests found');
const result = spawnSync(process.execPath, ['--test', ...files], {stdio: 'inherit'});
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
