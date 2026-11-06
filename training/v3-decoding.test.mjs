import test from 'node:test';
import assert from 'node:assert/strict';
import {decodeStructure,focusTexts} from './v3-decoding.mjs';
test('joint decoding preserves reverse surface order and uses no monotonic shortcut',()=>{
  const output=decodeStructure([.001,.001,.995,.001,.001,.001],[[.01,.96,.01,.01,.01],[.96,.01,.01,.01,.01]],'joint_coverage');
  assert.equal(output.count,'two');assert.deepEqual(output.roles,['action_1','action_0']);
});
test('joint coverage resolves count/span inconsistency without gold',()=>{
  const output=decodeStructure([.001,.55,.447,.001,.0005,.0005],[[.99,.002,.002,.002,.004],[.002,.99,.002,.002,.004]],'joint_coverage');
  assert.equal(output.count,'two');assert.deepEqual(output.roles,['action_0','action_1']);
});
test('zero action decoding ignores every word',()=>{
  const output=decodeStructure([.99,.002,.002,.002,.002,.002],[[.01,.01,.01,.01,.96]],'count_mask');
  assert.deepEqual(output.roles,['ignored']);
});
test('focus fragments preserve nonadjacent argument text without inventing connectors',()=>{
  const words=[{start:0,end:4},{start:5,end:8},{start:9,end:12}];
  assert.deepEqual(focusTexts('Give red key',words,['action_0','ignored','action_0'],0),['Give','key']);
});
test('conservative decoding never deletes a independently predicted action',()=>{
  const probabilities=[.001,.05,.548,.4,.0005,.0005],roles=[[.99,.002,.002,.002,.004]];
  const baseline=decodeStructure(probabilities,roles,'independent');
  const joint=decodeStructure(probabilities,roles,'joint_coverage');
  assert.equal(baseline.count,'two');assert.equal(joint.count,'one');
  const conservative=decodeStructure(probabilities,roles,'joint_preserve_count');
  assert.equal(conservative.count,'two');assert.equal(conservative.selectedRuntimeArm,'independent');
});
