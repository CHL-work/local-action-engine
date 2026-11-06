import test from 'node:test';
import assert from 'node:assert/strict';
import {inferV3} from '../training/v3-inference.mjs';
import {fixture} from '../eval/registry.mjs';
import {ACTION_GROUPS} from '../eval/experiments/structure.mjs';

test('interactive inference uses a copied host scene rather than a fixture or later mutation', async () => {
  const scene = fixture('imp');
  const family = Object.entries(ACTION_GROUPS).find(([, value]) => value.keys.includes('trigger'))[0];
  let sawSpecies = false;
  const ask = async (state, questions) => {
    const [id, question] = Object.entries(questions)[0];
    if (id === 'action_count') scene.actor.species = 'mouse';
    if (id === 's0_trigger') {
      assert.equal(state.your_species, 'imp');
      sawSpecies = true;
    }
    const choice = {action_count: 'one', word_role: 'action_0', meaning: 'order',
      action_family: family, action_kind: 'trigger', s0_trigger: 'dragon_delusion'}[id];
    assert.ok(choice in question.criteria, id);
    return {response: {answers: {[id]: {choice,
      probabilities: Object.fromEntries(Object.keys(question.criteria).map(key => [key, Number(key === choice)]))}}}};
  };
  const result = await inferV3('you are a dragon', 'not-a-fixture', ask, {scene});
  assert.equal(sawSpecies, true);
  assert.deepEqual(result.outcome, {kind: 'order', steps: [{template: 'trigger', args: {trigger: 'dragon_delusion'}}]});
});
