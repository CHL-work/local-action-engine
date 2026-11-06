import { BUNDLE, SKILLS, TRIGGERS, omittedReference } from './registry.mjs';

// Engine-neutral contract validator and symbolic task compiler; no physics/navigation.
const schema = {
  move: ['destination', 'movement'], follow: ['subject'], flee: ['threat'], stop: [], guard: ['ward'], pose: ['pose'],
  attack: ['target'], use_skill: ['skill', 'target'], use_item: ['item', 'target'], pick_up: ['item'], drop: ['item'],
  give: ['item', 'recipient'], throw: ['item', 'target'], interact: ['object', 'operation'],
  carry_to: ['cargo', 'destination', 'movement'], eat: ['item'], call_allies: ['call'], read_scripture: ['source'], trigger: ['trigger']
};
const refSlots = new Set(['destination', 'subject', 'threat', 'ward', 'target', 'item', 'recipient', 'object', 'cargo', 'source']);
const durationTemplates = new Set(['move', 'follow', 'flee', 'stop', 'guard', 'attack']);
const exactKeys = (obj, keys) => obj && typeof obj === 'object' && !Array.isArray(obj) && Object.keys(obj).sort().join('|') === [...keys].sort().join('|');
const enumValue = (x, values) => values.includes(x);

function validateRef(ref, i, steps, scene) {
  if (!ref || typeof ref !== 'object') return false;
  if (ref.kind === 'default_target') return exactKeys(ref, ['kind']);
  if (ref.kind === 'inventory') return exactKeys(ref, ['kind', 'selection']) && ref.selection === 'random';
  if (ref.kind === 'entity') return exactKeys(ref, ['kind', 'handle']) && scene.perceived.some(e => e.handle === ref.handle);
  if (['self', 'speaker', 'here'].includes(ref.kind)) return exactKeys(ref, ['kind']);
  if (ref.kind === 'held') return exactKeys(ref, ref.expected_kind ? ['kind', 'slot', 'expected_kind'] : ['kind', 'slot']) && ref.slot === 'hand' && (!ref.expected_kind || enumValue(ref.expected_kind, ['key', 'fire_pot', 'coin', 'scripture']));
  if (ref.kind === 'step_result') return exactKeys(ref, ['kind', 'step', 'output']) && Number.isInteger(ref.step) && ref.step >= 0 && ref.step < i && ref.output === 'item' && ['pick_up', 'drop', 'give', 'throw'].includes(steps[ref.step].template);
  if (ref.kind === 'direction') return exactKeys(ref, ['kind', 'frame', 'axis']) && enumValue(ref.frame, ['listener', 'speaker']) && enumValue(ref.axis, ['forward', 'back', 'left', 'right', 'up', 'down']);
  if (ref.kind === 'selector') {
    if (!exactKeys(ref, ['kind', 'entity_kind', 'filters', 'rank']) || !enumValue(ref.entity_kind, ['monster', 'item', 'object']) || !enumValue(ref.rank, ['nearest', 'largest', 'smallest', 'most_wounded', 'any']) || !Array.isArray(ref.filters) || ref.filters.length > 4) return false;
    if (new Set(ref.filters.map(f => f.type)).size !== ref.filters.length) return false;
    return ref.filters.every(f => {
      if (f.type === 'direction') return exactKeys(f, ['type', 'frame', 'axis']) && enumValue(f.frame, ['listener', 'speaker']) && enumValue(f.axis, ['forward', 'back', 'left', 'right']);
      if (!exactKeys(f, ['type', 'value'])) return false;
      if (f.type === 'species') return ref.entity_kind === 'monster' && enumValue(f.value, ['goblin', 'ice_golem', 'mouse']);
      if (f.type === 'kind') return enumValue(f.value, ref.entity_kind === 'item' ? ['key', 'cheese'] : ref.entity_kind === 'object' ? ['lever', 'door', 'crate', 'ledge', 'rope'] : []);
      if (f.type === 'held_item') return ref.entity_kind === 'monster' && f.value === 'key';
      return f.type === 'exclude_self' && ref.entity_kind === 'monster' && f.value === true;
    });
  }
  return false;
}
function refType(r, scene) {
  if (r.kind === 'entity') return scene.perceived.find(e => e.handle === r.handle)?.kind;
  if (r.kind === 'selector') return r.entity_kind;
  if (r.kind === 'self' || r.kind === 'speaker') return 'player';
  if (['held', 'step_result', 'inventory'].includes(r.kind)) return 'item';
  return 'point';
}
function allowedSlot(template, slot, args, contract) {
  if (slot === 'destination' && contract?.protocol === 3) return ['monster', 'player', 'object', 'point', 'item'];
  if (slot === 'item' || slot === 'source') return ['item'];
  if (['subject', 'threat', 'recipient'].includes(slot)) return ['monster', 'player'];
  if (slot === 'object') return ['object'];
  if (template === 'trigger') return ['monster', 'player'];
  if (template === 'attack') return ['monster', 'player', 'object'];
  if (template === 'use_skill' && args.skill === 'revive') return ['remains'];
  if (template === 'use_skill' && args.skill === 'heal') return ['monster', 'player'];
  if (template === 'use_skill' && args.skill === 'acid_pool') return ['point', 'object'];
  return ['monster', 'player', 'object', 'point', ...(slot === 'cargo' ? ['item'] : [])];
}

export function validateProposal(proposal, scene, { protocol = 2, bundleId = BUNDLE, maxSteps = 2 } = {}) {
  const errors = [];
  if (!exactKeys(proposal, ['protocol', 'bundle_id', 'session_id', 'lease_id', 'snapshot_id', 'request_id', 'outcome'])) return ['Invalid envelope keys'];
  if (proposal.protocol !== protocol || proposal.bundle_id !== bundleId) errors.push('Unknown protocol or bundle');
  for (const k of ['session_id', 'lease_id', 'snapshot_id', 'request_id']) if (typeof proposal[k] !== 'string' || !/^[a-zA-Z0-9-]{1,80}$/.test(proposal[k])) errors.push('Invalid ID: ' + k);
  if (!scene.leaseValid || scene.status !== 'Thinking') errors.push('Lease no longer owns Thinking');
  const o = proposal.outcome;
  if (o?.kind === 'unresolved') {
    if (!exactKeys(o, ['kind', 'reason']) || !enumValue(o.reason, ['unclear', 'chat', 'unsupported'])) errors.push('Invalid unresolved outcome');
    return errors;
  }
  if (o?.kind === 'technical_failure') {
    if (!exactKeys(o, ['kind', 'reason']) || !enumValue(o.reason, ['stt_error', 'inference_error'])) errors.push('Invalid technical outcome');
    return errors;
  }
  if (o?.kind !== 'order' || !exactKeys(o, ['kind', 'steps']) || !Array.isArray(o.steps) || o.steps.length < 1 || o.steps.length > maxSteps) return [...errors, 'Invalid order shape/count'];
  for (const [i, s] of o.steps.entries()) {
    if (!schema[s.template]) { errors.push('Unknown template'); continue; }
    const args = s.args, fields = [...schema[s.template]];
    if (s.template === 'trigger' && TRIGGERS[args?.trigger]?.target) fields.push('target');
    const top = ['template', 'args', ...(s.template === 'attack' ? ['mode'] : []), ...(s.duration !== undefined ? ['duration'] : [])];
    if (!exactKeys(s, top) || !exactKeys(args, fields)) { errors.push('Invalid fields on step ' + i); continue; }
    if (s.duration && (!durationTemplates.has(s.template) || !/^(?:[1-7]s|until_expiry)$/.test(s.duration))) errors.push('Invalid duration');
    if (i < o.steps.length - 1 && ['follow', 'flee', 'stop', 'guard'].includes(s.template) && (!s.duration || s.duration === 'until_expiry')) errors.push('Unbounded non-final continuous step');
    for (const [field, value] of Object.entries(args)) if (refSlots.has(field)) {
      if (!validateRef(value, i, o.steps, scene)) errors.push('Invalid reference ' + i + '.' + field);
      else if (['default_target', 'inventory'].includes(value.kind)) {
        if (omittedReference(s.template, field)?.kind !== value.kind) errors.push('No omission default for ' + i + '.' + field);
      }
      else if (!allowedSlot(s.template, field, args, { protocol }).includes(refType(value, scene))) errors.push('Wrong reference type ' + i + '.' + field);
    }
    if (s.mode && !enumValue(s.mode, ['single', 'engage'])) errors.push('Invalid attack mode');
    if (args.movement && !enumValue(args.movement, ['walk', 'run', 'jump', 'fly'])) errors.push('Invalid movement mode');
    if (s.template === 'use_skill' && !SKILLS[args.skill]) errors.push('Unknown skill');
    if (s.template === 'trigger' && !TRIGGERS[args.trigger]) errors.push('Unknown trigger');
    if (s.template === 'pose' && !enumValue(args.pose, ['dance', 'bow', 'sit', 'wave', 'turn_around'])) errors.push('Unknown pose');
    if (s.template === 'interact' && !enumValue(args.operation, ['pull', 'open', 'close', 'push'])) errors.push('Unknown interaction');
    if (s.template === 'call_allies' && args.call !== 'default') errors.push('Unknown call');
  }
  return errors;
}

function eligibleOwned(scene, template) {
  return (scene.actor.inventory || []).filter(item => item.transferable !== false && (template !== 'use_item' || item.usable) && (template !== 'throw' || item.throwable));
}

export function resolveReference(r, scene, context = {}) {
  if (r.kind === 'inventory') {
    const pool = eligibleOwned(scene, context.template);
    if (!pool.length) return { kind: 'missing_item' };
    // Recorded symbolic runs use a seeded host RNG; never sample interpretation.
    const item = pool[Math.min(pool.length - 1, Math.floor((context.random || Math.random)() * pool.length))];
    return { kind: 'bound_inventory', handle: item.handle, itemKind: item.itemKind, selection: 'random', retainIdentity: true };
  }
  if (r.kind === 'default_target') {
    const { template, slot, args = {} } = context;
    const accepts = allowedSlot(template, slot, args, context.contract);
    let pool = scene.perceived.filter(e => e.handle !== scene.actor.handle && accepts.includes(e.kind));
    if (['attack', 'use_item', 'throw'].includes(template) || (template === 'use_skill' && SKILLS[args.skill]?.targets === 'damage')) pool = pool.filter(e => e.kind !== 'object' || e.destructible);
    if (slot === 'recipient' || (template === 'use_skill' && args.skill === 'heal')) pool = pool.filter(e => e.alive !== false);
    if (template === 'interact') pool = pool.filter(e => e.objectKind === ({ pull: 'lever', open: 'door', close: 'door', push: 'crate' })[args.operation]);
    if (template === 'read_scripture') pool = pool.filter(e => e.itemKind === 'scripture');
    pool.sort((a, b) => a.distanceWidths - b.distanceWidths || a.handle.localeCompare(b.handle));
    if (!pool.length) return { kind: 'search', reference: r, accepts, behavior: 'turn_until_vision_cone_match_with_los', consumesControlTime: true };
    return { kind: 'bound', handle: pool[0].handle, policy: 'nearest_compatible', retainIdentity: true };
  }
  if (r.kind === 'entity') return { kind: 'bound', handle: r.handle };
  if (r.kind !== 'selector') return { kind: r.kind, reference: r };
  let pool = scene.perceived.filter(e => e.kind === r.entity_kind);
  for (const f of r.filters) pool = pool.filter(e => {
    if (f.type === 'species') return e.species === f.value;
    if (f.type === 'kind') return (e.itemKind || e.objectKind) === f.value;
    if (f.type === 'held_item') return e.held === f.value;
    if (f.type === 'direction') return e.axis === f.axis; // saved-frame game fact, never transcript parsing
    return f.type === 'exclude_self';
  });
  if (!pool.length) return { kind: 'search', selector: r, behavior: 'turn_until_vision_cone_match_with_los', consumesControlTime: true };
  if (r.rank === 'nearest') pool.sort((a, b) => a.distanceWidths - b.distanceWidths);
  if (r.rank === 'largest') pool.sort((a, b) => (b.size || 0) - (a.size || 0));
  if (r.rank === 'smallest') pool.sort((a, b) => (a.size || 0) - (b.size || 0));
  if (r.rank === 'most_wounded') pool.sort((a, b) => (a.health || 1) - (b.health || 1));
  // Deterministic symbolic test binding for 'any'; shipped gameplay may use host RNG.
  return { kind: 'bound', handle: pool[0].handle, selector: r };
}

export function compileDirective(proposal, scene, { expired = false, random = Math.random, contract } = {}) {
  const errors = validateProposal(proposal, scene, contract);
  if (errors.length) return { processable: false, validationErrors: errors, disposition: 'rejected', directive: null };
  const directive = { directive_id: 'directive-' + proposal.lease_id, listener: scene.actor.species, lease_id: proposal.lease_id,
    status: 'Brainwashed', durationSeconds: scene.controlSeconds, expiresAfterSeconds: scene.controlSeconds, steps: [], tasks: [] };
  function fallback(disposition, reason) {
    directive.fallback = { recipe: 'confused_gesture', reason, usesRemainingControlTime: true };
    return { processable: true, validationErrors: [], disposition, directive };
  }
  if (expired || proposal.outcome.kind === 'technical_failure') return fallback('technical_failure', expired ? 'thinking_deadline' : proposal.outcome.reason);
  if (proposal.outcome.kind === 'unresolved') return fallback('unresolved', proposal.outcome.reason);
  directive.steps = structuredClone(proposal.outcome.steps);
  for (const [i, s] of directive.steps.entries()) {
    const a = s.args;
    if (s.template === 'attack' && a.target.kind === 'default_target') { s.mode = 'single'; delete s.duration; }
    if (a.target?.kind === 'self' || (a.target?.kind === 'selector' && a.target.entity_kind === 'monster' && a.target.rank === 'any' && a.target.filters.every(f => f.type === 'exclude_self'))) {
      if (s.template === 'attack') { s.mode = 'single'; delete s.duration; }
    }
    if ((a.movement === 'fly' && !scene.actor.canFly) || (s.template === 'carry_to' && !scene.actor.canCarry) || (s.template === 'eat' && !scene.actor.canEat)) return fallback('cant', 'missing_capability');
    if (s.template === 'use_skill' && !(a.skill === 'strongest' ? scene.actor.skills.length : scene.actor.skills.includes(a.skill))) return fallback('cant', 'unavailable_skill');
    for (const r of Object.values(a)) if (r?.kind === 'held' && (!scene.actor.held || (r.expected_kind && r.expected_kind !== scene.actor.held))) return fallback('missing_item', 'held_item_missing_or_wrong_kind');
    for (const r of Object.values(a)) if (r?.kind === 'inventory' && !eligibleOwned(scene, s.template).length) return fallback('missing_item', 'no_eligible_owned_item');
    if (s.template === 'trigger' && (TRIGGERS[a.trigger].species !== scene.actor.species || !scene.actor.triggerReady)) return fallback('trigger_gate_failed', 'trigger_unavailable');
    let task = s.template;
    if (s.template === 'use_skill' && scene.actor.cooldown.includes(a.skill)) {
      if (['monster', 'player'].includes(refType(a.target, scene)) || a.target.kind === 'default_target') task = 'basic_attack_same_target';
      else return fallback('cant', 'no_compatible_cooldown_fallback');
    }
    const bindings = Object.fromEntries(Object.entries(a).filter(([field]) => refSlots.has(field)).map(([field, value]) => [field, resolveReference(value, scene, { template: s.template, slot: field, args: a, random, contract })]));
    const nonFinal = i < directive.steps.length - 1;
    let completion = 'one_activation';
    if (s.template === 'attack') completion = s.mode === 'single' ? 'one_activation' : s.duration ? 'duration_or_expiry' : nonFinal ? 'one_attack_cycle' : 'target_defeated_or_expiry';
    if (s.template === 'move') completion = s.duration ? 'duration_or_expiry' : nonFinal && a.destination.kind === 'direction' ? 'advance_two_player_widths' : a.destination.kind === 'direction' ? 'expiry' : 'arrival_or_expiry';
    if (['follow', 'flee', 'stop', 'guard'].includes(s.template)) completion = 'duration_or_expiry';
    directive.tasks.push({ step: i, recipe: task, bindings, completion,
      ...(s.template === 'attack' && a.target.kind === 'default_target' ? { attackPolicy: 'weakest_usable_or_basic_fallback', maxActivations: 1 } : {}),
      ...(s.duration ? { durationSeconds: s.duration === 'until_expiry' ? scene.controlSeconds : Math.min(Number(s.duration.slice(0, -1)), scene.controlSeconds) } : {}),
      sharesAbsoluteExpiry: true });
  }
  return { processable: true, validationErrors: [], disposition: 'execute', directive };
}

// Compare against independently authored gold. Object key ordering is irrelevant;
// action order, selector identity, reference frame, skill and mode are significant.
export function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])]));
  return value;
}
export function grade(expected, proposal, host, expectedOverflow, overflow) {
  const reasons = [];
  const actual = proposal.outcome;
  let semanticMatch = actual.kind === expected.kind;
  if (semanticMatch && actual.kind === 'order') {
    // Only explicit, documented host normalization (e.g. self-hit single) is used.
    const normalized = host.directive?.steps?.length ? host.directive.steps : actual.steps;
    semanticMatch = [expected.steps, ...(expected.alternatives || [])].some(steps => JSON.stringify(canonical(steps)) === JSON.stringify(canonical(normalized)));
    if (!semanticMatch) reasons.push('Final action sequence or arguments differ from authored expectation.');
  } else if (semanticMatch && actual.kind === 'unresolved') {
    semanticMatch = expected.reasons.includes(actual.reason);
    if (!semanticMatch) reasons.push('Unresolved reason differs from expectation.');
  } else if (!semanticMatch) reasons.push('Expected ' + expected.kind + ', received ' + actual.kind + '.');
  const hostMatch = host.disposition === expected.host;
  if (!hostMatch) reasons.push('Expected host outcome ' + expected.host + ', received ' + host.disposition + '.');
  if (!host.processable) reasons.push(...host.validationErrors);
  const overflowMatch = expectedOverflow === undefined || expectedOverflow === overflow;
  if (!overflowMatch) reasons.push('Overflow telemetry differs (two-action cap is enforced separately).');
  return { semanticMatch, hostMatch, overflowMatch, processable: host.processable, success: semanticMatch && hostMatch && host.processable, reasons };
}
