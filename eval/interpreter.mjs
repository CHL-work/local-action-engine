import { TEMPLATES, SKILLS, TRIGGERS, BUNDLE, referenceOptions, omittedReference } from './registry.mjs';
import { CLASSIFY_FIRST, CLASSIFY_SECOND, REFERENCE_RULES, INVENTORY_RULES, classificationState, actionState } from './prompts.mjs';
import { separateActions, choiceGroups } from './experiments/structure.mjs';
import { choiceGate } from './experiments/gating.mjs';

export const POLICY = Object.freeze({ version: 'bw-4-focused-action-prompts', minProbability: 0.65, minMargin: 0.15, maxRefinements: 1, thinkingDeadlineMs: 5000 });
const outcomes = new Set(['chat', 'unclear', 'unsupported']);
const choices = (instructions, criteria) => ({ type: 'choice', instructions, criteria });
function questionPrefix(index, template) {
  return 'For your action ' + (index + 1) + ' (' + template + '), in the requested order after corrections: ';
}
const durationOptions = {
  unspecified: 'No explicit duration for this action.',
  ...Object.fromEntries(Array.from({ length: 7 }, (_, i) => [(i + 1) + 's', 'Explicitly ' + (i + 1) + ' seconds.'])),
  until_expiry: 'Explicitly until this brainwash/control ends.',
  vague: 'Vague duration such as a moment, shortly, a while.',
  unsupported: 'Other duration: fractional, >7 seconds, forever, or unsupported end condition.'
};

export function variants(template, i) {
  const q = {}, prefix = questionPrefix(i, template), key = 's' + i + '_';
  if (['move', 'carry_to'].includes(template)) q[key + 'movement'] = choices(prefix + 'How are you being asked to travel?', {
    walk: 'Walk or ordinary unspecified travel.', run: 'Run or sprint.', jump: 'Jump/leap.', fly: 'Fly/soar.', unsupported: 'Teleport or other unsupported movement.'
  });
  if (template === 'use_skill') q[key + 'skill'] = choices(prefix + 'Which ability are you being asked to use?', {
    ...Object.fromEntries(Object.entries(SKILLS).map(([id, spec]) => [id, spec.description])), unclear: 'Missing or ambiguous ability.', unsupported: 'Uncatalogued ability.'
  });
  if (template === 'trigger') q[key + 'trigger'] = choices(prefix + 'Which special belief, accusation or secret request is addressed to you?', {
    ...Object.fromEntries(Object.entries(TRIGGERS).map(([id, spec]) => [id, spec.description])), unclear: 'No clear matching species trigger.'
  });
  if (template === 'pose') q[key + 'pose'] = choices(prefix + 'Which gesture?', {
    dance: 'Dance.', bow: 'Bow politely.', sit: 'Sit down.', wave: 'Wave your hand.', turn_around: 'Turn around in place.', unclear: 'Unknown gesture.'
  });
  if (template === 'interact') q[key + 'operation'] = choices(prefix + 'Which operation?', {
    pull: 'Pull a lever.', open: 'Open a door.', close: 'Close a door.', push: 'Push a crate.', unclear: 'Missing/unknown operation.'
  });
  if (['move', 'follow', 'flee', 'stop', 'guard', 'attack'].includes(template)) q[key + 'duration'] = choices(prefix + 'What duration does the player explicitly state?' + (template === 'attack' ? ' Self-attack is once even if the player says until you die.' : ''), durationOptions);
  if (template === 'attack') q[key + 'mode'] = choices(prefix + 'How many attacks are intended?', {
    single: 'One hit/attack, including bare Attack or Kill with an omitted target, kill yourself, kill another monster, everyone/anybody.',
    engage: 'Attack/kill an explicitly specified target or filtered selector; ordinary ongoing attack, not bare Attack/Kill.',
    unclear: 'Cannot determine attack scope.'
  });
  return q;
}

export function slots(template, variant) {
  return ({
    move: { destination: 'destination' }, follow: { subject: 'creature' }, flee: { threat: 'creature' },
    guard: { ward: 'destination' }, attack: { target: 'damage' },
    use_skill: variant.skill && SKILLS[variant.skill] ? { target: SKILLS[variant.skill].targets } : {},
    use_item: { item: 'item', target: 'damage' }, pick_up: { item: 'item' }, drop: { item: 'item' },
    give: { item: 'item', recipient: 'creature' }, throw: { item: 'item', target: 'damage' },
    interact: { object: 'object' }, carry_to: { cargo: 'cargo', destination: 'destination' },
    eat: { item: 'item' }, read_scripture: { source: 'item' },
    trigger: variant.trigger && TRIGGERS[variant.trigger]?.target ? { target: 'creature' } : {}
  })[template] || {};
}

export async function interpret(text, scene, ask, { experiment = 'baseline', gating } = {}) {
  if (!['baseline', 'separated_groups'].includes(experiment)) throw new Error('Architecture was not retained; its source is available only in recorded experiment snapshots.');
  const segmentation = experiment === 'separated_groups' ? separateActions(text) : undefined;
  const trace = [], decisions = [], templates = [], variantsByStep = [{}, {}], argsByStep = [{}, {}];
  const refMaps = {};
  let overflow = false;
  const detailState = i => actionState(segmentation?.segments[i] ?? text, templates[i], i, scene, i ? {
    kind: templates[i - 1], variants: variantsByStep[i - 1], arguments: argsByStep[i - 1]
  } : undefined);
  async function phase(name, questions, state) {
    if (experiment !== 'separated_groups') return rawPhase(name, questions, state);
    const small = Object.fromEntries(Object.entries(questions).filter(([,q]) => Object.keys(q.criteria).length <= 8));
    const selected = await rawPhase(name, small, state);
    for (const [id,q] of Object.entries(questions)) {
      if (id in small) continue;
      async function route(question, depth) {
        if (Object.keys(question.criteria).length <= 8) return (await rawPhase(name + '_leaf_' + depth, { [id]:question }, state))[id];
        const groups=choiceGroups(question, scene), groupId=id+'_group_'+depth;
        const actionQuestion = id === 'first' || id === 'action_kind';
        const instructions = actionQuestion ? 'You are a monster obeying this command. Which group contains the requested action?' : question.instructions + ' Choose the group containing that answer.';
        const answer=await rawPhase(name+'_group_'+depth, { [groupId]:choices(instructions, Object.fromEntries(Object.entries(groups).map(([key,g])=>[key,g.label]))) }, state);
        const chosen=groups[answer[groupId]];
        return route({ ...question, criteria:Object.fromEntries(chosen.keys.map(key=>[key,question.criteria[key]])) },depth+1);
      }
      selected[id]=await route(q,0);
    }
    return selected;
  }
  async function rawPhase(name, questions, state) {
    if (!Object.keys(questions).length) return {};
    const call = await ask(state, questions, name);
    trace.push(call);
    const answers = call.response.answers;
    const selected = {}, uncertain = {};
    for (const [id, q] of Object.entries(questions)) {
      const a = answers?.[id];
      if (!a || a.type !== 'choice' || !(a.choice in q.criteria) || !a.probabilities || Object.keys(a.probabilities).some(k => !(k in q.criteria))) throw new Error('Malformed Choice response');
      const all = Object.values(a.probabilities);
      if (all.some(p => !Number.isFinite(p) || p < 0 || p > 1) || Math.abs(all.reduce((a, b) => a + b, 0) - 1) > 0.02) throw new Error('Invalid Choice distribution');
      const p = a.probabilities[a.choice];
      const runnerUp = Math.max(0, ...Object.entries(a.probabilities).filter(([k]) => k !== a.choice).map(([, p]) => p));
      const gate=choiceGate(a,id,gating),accepted=gate.accepted;
      decisions.push({ phase: name, question: id, choice: a.choice, probability: p, margin: p - runnerUp, confidence: a.confidence, ...gate });
      selected[id] = a.choice;
      // Overflow is telemetry, never permission to execute a third action.
      if (!accepted && id !== 'overflow') uncertain[id] = q;
    }
    if (Object.keys(uncertain).length && !name.endsWith('_refine')) {
      const refined = Object.fromEntries(Object.entries(uncertain).map(([id, q]) => [id, { ...q, instructions: q.instructions + ' Re-read the command. Use only the meaning requested; do not invent missing details.' }]));
      Object.assign(selected, await rawPhase(name + '_refine', refined, state));
    } else if (Object.keys(uncertain).length) {
      const e = new Error('Low-confidence required decision'); e.unresolved = true; throw e;
    }
    return selected;
  }
  function refQuestions(template, i) {
    const q = {};
    for (const [slot, type] of Object.entries(slots(template, variantsByStep[i]))) {
      const id = 's' + i + '_' + slot;
      const options = referenceOptions(scene, type, i, templates[0]);
      if (argsByStep[i][slot]) continue; // omission already resolved by the presence question
      options.unclear.label = 'Supplied but ambiguous, hidden, unknown, unsupported or map-only reference, including there/that one. Do not replace it with a default. Also use if no omission default exists.';
      refMaps[id] = options;
      q[id] = choices(questionPrefix(i, template) + 'What is the requested ' + slot + '? ' + REFERENCE_RULES + (['item', 'source'].includes(slot) ? ' ' + INVENTORY_RULES : ''),
        Object.fromEntries(Object.entries(options).map(([id, entry]) => [id, entry.label])));
    }
    return q;
  }
  function applyAnswers(answers) {
    for (const [id, value] of Object.entries(answers)) {
      const match = id.match(/^s([01])_(.+)$/);
      if (!match) continue;
      const i = Number(match[1]), field = match[2];
      if (['unclear', 'unsupported', 'vague'].includes(value)) { const e = new Error('Unresolved ' + field); e.unresolved = true; e.reason = value === 'unsupported' ? 'unsupported' : 'unclear'; throw e; }
      if (refMaps[id]) argsByStep[i][field] = refMaps[id][value].value;
      else variantsByStep[i][field] = value;
    }
  }
  try {
    if (segmentation) {
      overflow=segmentation.segments.length>2;
      for (const [i, segment] of segmentation.segments.slice(0,2).entries()) {
        const selected=await phase('segment_'+i+'_template', {
          action_kind:choices('You are a monster obeying this command. Which action does it request? Ignore cancelled or quoted requests. A polite question can be a command.',TEMPLATES)
        },classificationState(segment));
        if (outcomes.has(selected.action_kind)) return { outcome:{kind:'unresolved',reason:selected.action_kind},trace,decisions,overflow,segmentation };
        if (templates[0]==='trigger' && selected.action_kind==='trigger') { overflow=true; break; }
        templates.push(selected.action_kind);
      }
    } else {
    const first = await phase('first_template', {
      first: choices(CLASSIFY_FIRST, TEMPLATES)
    }, classificationState(text));
    if (outcomes.has(first.first)) return { outcome: { kind: 'unresolved', reason: first.first }, trace, decisions, overflow };
    templates.push(first.first);
    const secondOptions = { ...TEMPLATES, none: 'No separate second action is requested.' };
    delete secondOptions.chat;
    const second = await phase('second_template', {
      second: choices(CLASSIFY_SECOND, secondOptions),
      overflow: choices('Does the player request more than two actions, or more than one special belief/secret reaction? Count carrying someone to a destination as one action. Ignore cancelled requests.', { yes: 'Yes.', no: 'No.', unclear: 'Cannot tell.' })
    }, classificationState(text, templates[0]));
    overflow = second.overflow === 'yes';
    if (outcomes.has(second.second)) return { outcome: { kind: 'unresolved', reason: second.second === 'unsupported' ? 'unsupported' : 'unclear' }, trace, decisions, overflow };
    if (second.second !== 'none') {
      if (templates[0] === 'trigger' && second.second === 'trigger') overflow = true;
      else templates.push(second.second);
    }
    }
    // Each action receives its own rules; two actions never share a detail prompt.
    for (let i = 0; i < templates.length; i++) {
      const template = templates[i], prefix = 'action_' + i + '_' + template;
      applyAnswers(await phase(prefix + '_variants', variants(template, i), detailState(i)));
      const presenceQuestions = {}, presenceSlots = {};
      for (const [slot] of Object.entries(slots(template, variantsByStep[i]))) {
        const fallback = omittedReference(template, slot);
        if (!fallback) continue;
        const id = 's' + i + '_' + slot + '_presence';
        presenceSlots[id] = { slot, fallback };
        presenceQuestions[id] = choices(questionPrefix(i, template) + 'Does the player supply words for the ' + slot + '? Pronouns, descriptions, directions, here/there and unknown names all count as supplied. Judge the words, not whether you can find the reference. A different action does not fill this slot automatically.', {
          supplied: 'Words specify this reference, even if ambiguous or impossible to find.',
          omitted: 'No words specify this reference.'
        });
      }
      const presence = await phase(prefix + '_reference_presence', presenceQuestions, detailState(i));
      for (const [id, answer] of Object.entries(presence)) if (answer === 'omitted') {
        const { slot, fallback } = presenceSlots[id]; argsByStep[i][slot] = fallback;
      }
      applyAnswers(await phase(prefix + '_references', refQuestions(template, i), detailState(i)));
    }
    const steps = templates.map((template, i) => {
      const args = { ...argsByStep[i] }, v = variantsByStep[i];
      for (const field of ['movement', 'skill', 'trigger', 'pose', 'operation']) if (v[field]) args[field] = v[field];
      if (template === 'call_allies') args.call = 'default';
      const s = { template, args };
      if (v.duration && v.duration !== 'unspecified') s.duration = v.duration;
      if (template === 'attack') s.mode = v.mode;
      return s;
    });
    // Structural completion rules are code, not command-language matching.
    if (steps.length === 2 && ['follow', 'flee', 'guard', 'stop'].includes(steps[0].template) && (!steps[0].duration || steps[0].duration === 'until_expiry')) {
      return { outcome: { kind: 'unresolved', reason: 'unclear' }, trace, decisions, overflow, segmentation, diagnostic: 'Non-final continuous action has no supported finite boundary.' };
    }
    return { outcome: { kind: 'order', steps }, trace, decisions, overflow, segmentation };
  } catch (e) {
    if (e.unresolved) return { outcome: { kind: 'unresolved', reason: e.reason || 'unclear' }, trace, decisions, overflow, segmentation, diagnostic: e.message };
    // Error messages are local fixed strings or sanitized HTTP status, never headers.
    return { outcome: { kind: 'technical_failure', reason: 'inference_error' }, trace, decisions, overflow, segmentation, diagnostic: e.status ? 'Jev HTTP ' + e.status : e.name === 'TimeoutError' ? 'API request timeout' : 'Inference/response validation failed' };
  }
}

export function envelope(id, outcome) {
  return { protocol: 2, bundle_id: BUNDLE, session_id: 'cast-' + id, lease_id: 'lease-' + id, snapshot_id: 'snapshot-' + id, request_id: 'request-' + id, outcome };
}
