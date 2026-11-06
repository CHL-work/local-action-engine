// Prototype content catalogue, independent of the evaluation corpus/answer key.
export const BUNDLE = 'bw-jev-eval-2';
export const TEMPLATES = {
  move: 'Move to a place or in a direction. Includes bare directions: left, right, forward, back, up. Walk, run, jump or fly.',
  follow: 'Follow a subject continuously.', flee: 'Flee or run away from a threat.',
  stop: 'Stop or wait, deliberately stand still.', guard: 'Guard a place or protect a ward.',
  pose: 'Make a gesture: dance, bow, sit, wave or turn around.',
  attack: 'Attack, hit or kill.',
  use_skill: 'Use an ability such as fireball, heal, revive or an ultimate.',
  use_item: 'Activate a held item, rather than physically throwing it.',
  pick_up: 'Pick up an item into inventory.', drop: 'Drop a held item.',
  give: 'Give a held or previously acquired item to a recipient.',
  throw: 'Physically throw an item.',
  interact: 'Operate an object: open/close door, pull lever, push crate.',
  carry_to: 'Carry someone or something to a destination, including flying with them.',
  eat: 'Eat or swallow something.', call_allies: 'Call or summon existing allies.',
  read_scripture: 'Read or recite an accessible scripture.',
  trigger: 'Accept a belief about yourself, react to a theft accusation, or reveal a hidden secret/treasure.',
  chat: 'No actionable instruction: social conversation, remarks, irrelevant questions or silence.',
  unclear: 'Cannot determine intended action; incomplete, ambiguous or unintelligible.',
  unsupported: 'Intelligible request outside the command catalogue, e.g. time travel, code execution, universal persistent rules.'
};
export const SKILLS = {
  fireball: { description: 'Cast a fireball projectile (not a physical held fire pot).', targets: 'damage' },
  ice_bolt: { description: 'Cast an ice bolt.', targets: 'damage' },
  acid_pool: { description: 'Create a persistent acid pool on the ground.', targets: 'point' },
  heal: { description: 'Heal a living creature.', targets: 'creature' },
  revive: { description: 'Revive/resurrect dead player remains.', targets: 'remains' },
  ultimate: { description: 'Use the actor species ultimate attack.', targets: 'damage' },
  strongest: { description: 'Explicitly use your strongest available ability.', targets: 'damage' }
};
export const TRIGGERS = {
  cheese_theft: { description: 'Mouse: assertion/accusation that a target stole your cheese.', target: true, species: 'mouse' },
  hidden_gift: { description: 'Mimic: ask it to reveal/give the good item it hides.', target: false, species: 'mimic' },
  dragon_delusion: { description: 'Imp: speaker asserts that the imp is a dragon.', target: false, species: 'imp' }
};

const species = (id, rank = 'any', more = []) => ({ kind: 'selector', entity_kind: 'monster', filters: [{ type: 'species', value: id }, ...more], rank });
const selector = (kind, filters = [], rank = 'any') => ({ kind: 'selector', entity_kind: kind, filters, rank });
const named = (kind, id) => selector(kind, [{ type: 'kind', value: id }]);
const direction = (frame, axis) => ({ kind: 'direction', frame, axis });
const held = expected_kind => ({ kind: 'held', slot: 'hand', ...(expected_kind ? { expected_kind } : {}) });

// Each entry holds a wire reference and semantic type. Options never contain gold labels.
export const REFS = {
  self: [{ kind: 'self' }, 'creature', 'Yourself, the controlled listener.'],
  speaker: [{ kind: 'speaker' }, 'creature', 'The player speaking: me/I/myself in the player command.'],
  here: [{ kind: 'here' }, 'point', 'Here, your current saved location.'],
  held: [held(), 'item', 'Whatever item you already hold; no specified kind.'],
  held_key: [held('key'), 'item', 'A key already held in your hand.'],
  held_fire_pot: [held('fire_pot'), 'item', 'A fire pot in your hand, even if you do not actually hold one.'],
  held_coin: [held('coin'), 'item', 'A coin in your hand.'],
  held_scripture: [held('scripture'), 'item', 'The scripture you hold.'],
  result0: [{ kind: 'step_result', step: 0, output: 'item' }, 'item', 'The exact item produced/acquired by action ONE; it/that item after pickup.'],
  another: [selector('monster', [{ type: 'exclude_self', value: true }]), 'monster', 'Any random OTHER monster, another monster, anyone/everyone broad attack: never self.'],
  nearest_monster: [selector('monster', [{ type: 'exclude_self', value: true }], 'nearest'), 'monster', 'The nearest other monster, as a selector.'],
  largest_monster: [selector('monster', [{ type: 'exclude_self', value: true }], 'largest'), 'monster', 'The largest other monster, as a selector.'],
  smallest_monster: [selector('monster', [{ type: 'exclude_self', value: true }], 'smallest'), 'monster', 'The smallest other monster, as a selector.'],
  wounded_monster: [selector('monster', [{ type: 'exclude_self', value: true }], 'most_wounded'), 'monster', 'The most wounded other monster.'],
  goblin: [species('goblin'), 'monster', 'A/the goblin, named by species only; search when absent.'],
  golem: [species('ice_golem'), 'monster', 'A/the ice golem, named by species only; search when absent.'],
  mouse: [species('mouse'), 'monster', 'A/the mouse monster, named by species.'],
  nearest_goblin: [species('goblin', 'nearest'), 'monster', 'The nearest goblin.'],
  behind_goblin: [species('goblin', 'any', [{ type: 'direction', frame: 'listener', axis: 'back' }]), 'monster', 'A goblin behind you, using your saved direction even while searching.'],
  key_carrier: [selector('monster', [{ type: 'held_item', value: 'key' }]), 'monster', 'The monster visibly holding a key.'],
  key: [named('item', 'key'), 'item', 'A/the key on the ground, named by kind; search if absent.'],
  cheese: [named('item', 'cheese'), 'item', 'Some/the cheese, named by kind.'],
  lever: [named('object', 'lever'), 'object', 'A/the lever; search if absent.'],
  door: [named('object', 'door'), 'object', 'A/the door; search if absent.'],
  crate: [named('object', 'crate'), 'object', 'A/the crate; search if absent.'],
  ledge: [named('object', 'ledge'), 'point', 'A/the ledge; search if absent.'],
  rope: [named('object', 'rope'), 'object', 'A/the rope; search if absent.']
};
for (const frame of ['listener', 'speaker']) for (const axis of ['forward', 'back', 'left', 'right', 'up', 'down']) {
  const id = (frame === 'listener' ? '' : 'my_') + axis;
  REFS[id] = [direction(frame, axis), 'direction', (frame === 'listener' ? 'Your/the listener\'s ' : 'The speaking player\'s (my) ') + axis + ' direction. Bare directions use listener frame.'];
}

const baseWorld = [
  { handle: 'p_white', kind: 'player', label: 'white player speaking in front of you', visible: true, los: true, distanceWidths: 2, color: 'white', alive: true },
  { handle: 'g1', kind: 'monster', species: 'goblin', label: 'small green goblin holding a key, in front of you', visible: true, los: true, distanceWidths: 5, size: 1, health: 0.8, held: 'key' },
  { handle: 'i1', kind: 'monster', species: 'ice_golem', label: 'big blue ice golem on your left', visible: true, los: true, distanceWidths: 7, size: 3, health: 1 },
  { handle: 'm1', kind: 'monster', species: 'mouse', label: 'small grey mouse on your right', visible: true, los: true, distanceWidths: 3, size: 0.5, health: 0.3 },
  { handle: 'p_blue', kind: 'player', label: 'blue player behind you, near and unobstructed', visible: false, los: true, distanceWidths: 3, color: 'blue', alive: true },
  { handle: 'p_red', kind: 'player', label: 'red player in front of you', visible: true, los: true, distanceWidths: 5, color: 'red', alive: true },
  { handle: 'k1', kind: 'item', itemKind: 'key', label: 'brass key on the floor on your right', visible: true, los: true, distanceWidths: 2 },
  { handle: 'c1', kind: 'item', itemKind: 'cheese', label: 'piece of cheese in front of you', visible: true, los: true, distanceWidths: 2 },
  ...['lever', 'door', 'crate', 'ledge', 'rope'].map((kind, i) => ({ handle: 'o_' + kind, kind: 'object', objectKind: kind, label: kind + ' in front of you', visible: true, los: true, distanceWidths: 4 + i, destructible: ['crate', 'rope'].includes(kind) }))
];
export function fixture(name = 'imp') {
  const parts = name.split(':');
  const actor = parts[0];
  const world = structuredClone(baseWorld);
  const baseSkills = { imp: ['fireball'], mouse: ['ultimate', 'acid_pool'], nurse: ['heal', 'revive'], harpy: [], mimic: [], player: [] };
  if (!(actor in baseSkills)) throw new Error('Unknown fixture actor: ' + actor);
  const scene = {
    id: name, actor: { handle: 'listener', species: actor, skills: baseSkills[actor], canFly: actor === 'harpy', canCarry: ['harpy', 'player'].includes(actor),
      canEat: ['mouse', 'mimic'].includes(actor), held: 'key', cooldown: [], triggerReady: true },
    speaker: { color: 'white', label: 'white player speaking' },
    world, controlSeconds: 5, status: 'Thinking', leaseValid: true
  };
  for (const variant of parts.slice(1)) {
    if (variant === 'empty') scene.actor.held = null;
    else if (['fire_pot', 'coin', 'scripture'].includes(variant)) scene.actor.held = variant;
    else if (variant === 'cooldown') scene.actor.cooldown = ['fireball'];
    else if (variant === 'trigger_blocked') scene.actor.triggerReady = false;
    else if (variant === 'no_goblin') scene.world = scene.world.filter(e => e.species !== 'goblin');
    else if (variant === 'near_key') Object.assign(world.find(e => e.handle === 'k1'), { visible: false, los: true, distanceWidths: 3, label: 'brass key behind you within three widths, unobstructed' });
    else if (variant === 'blocked_key') Object.assign(world.find(e => e.handle === 'k1'), { visible: false, los: false, distanceWidths: 3, label: 'key behind a crate' });
    else if (variant === 'far_key') Object.assign(world.find(e => e.handle === 'k1'), { visible: false, los: true, distanceWidths: 8, label: 'key far behind you' });
    else if (variant === 'blocked_blue') Object.assign(world.find(e => e.handle === 'p_blue'), { los: false });
    else if (variant === 'dead_red') { scene.world = scene.world.filter(e => e.handle !== 'p_red'); scene.world.push({ handle: 'r_red', kind: 'remains', label: 'red player remains on the floor', visible: true, los: true, distanceWidths: 2, color: 'red', alive: false }); }
    else if (variant === 'no_items') scene.world = scene.world.filter(e => e.kind !== 'item');
    else throw new Error('Unknown fixture variant: ' + variant);
  }
  scene.actor.inventory = scene.actor.held ? [{ handle: 'owned-1', itemKind: scene.actor.held, transferable: true, usable: true, throwable: true }] : [];
  scene.perceived = scene.world.filter(e => e.los && (e.visible || e.distanceWidths <= 4));
  return scene;
}

export const ref = id => {
  if (id === 'default_target') return { kind: 'default_target' };
  if (id === 'random_inventory') return { kind: 'inventory', selection: 'random' };
  if (id.startsWith('e:')) return { kind: 'entity', handle: id.slice(2) };
  if (!REFS[id]) throw new Error('Unknown reference: ' + id);
  return structuredClone(REFS[id][0]);
};

// Omission is an explicit classifier choice, not a guess from a failed grounding.
// Secret-trigger accusations still require their subject; missing action/skill names
// are not reference slots and cannot acquire this default.
export function omittedReference(template, slot) {
  if (template === 'trigger') return null;
  if (slot === 'item' && ['give', 'drop', 'throw', 'use_item'].includes(template)) return ref('random_inventory');
  return ref('default_target');
}

export function referenceOptions(scene, type, step, previous) {
  const compatible = t => {
    if (type === 'damage') return ['monster', 'creature', 'object', 'direction', 'point'].includes(t);
    if (type === 'creature') return ['creature', 'monster'].includes(t);
    if (type === 'destination') return ['creature', 'monster', 'object', 'point', 'direction'].includes(t);
    if (type === 'point') return ['point', 'direction', 'object'].includes(t);
    if (type === 'cargo') return ['creature', 'monster', 'item', 'object'].includes(t);
    return t === type;
  };
  const options = {};
  for (const [id, [value, kind, label]] of Object.entries(REFS)) {
    if (!compatible(kind)) continue;
    if (id === 'result0' && !(step === 1 && ['pick_up', 'drop', 'give', 'throw'].includes(previous))) continue;
    options[id] = { value, label };
  }
  for (const entity of scene.perceived) {
    const typeName = entity.kind === 'player' ? 'creature' : entity.kind;
    if (compatible(typeName)) options['e:' + entity.handle] = { value: { kind: 'entity', handle: entity.handle }, label: 'Specific observed entity: ' + entity.label + '. Use for identifying details/color, not species/kind alone.' };
  }
  // An unknown hidden colored player is not supplied as a fabricated entity handle.
  options.unclear = { value: null, label: 'Required reference is absent, ambiguous, deictic (there/that one), a made-up name, map-only exit, or unsupported description. Do not guess.' };
  return options;
}

export const step = (template, args = {}, extra = {}) => ({ template, args, ...extra });
export const A = (target, mode = 'engage') => step('attack', { target: ref(target) }, { mode });
export const M = (destination, mode = 'walk', duration) => step('move', { destination: ref(destination), movement: mode }, duration ? { duration } : {});
export const S = (skill, target) => step('use_skill', { skill, target: ref(target) });
export const P = item => step('pick_up', { item: ref(item) });
export const D = (item = 'held') => step('drop', { item: ref(item) });
export const G = (item = 'held', recipient = 'speaker') => step('give', { item: ref(item), recipient: ref(recipient) });
export const T = (item, target) => step('throw', { item: ref(item), target: ref(target) });
export const U = (item, target) => step('use_item', { item: ref(item), target: ref(target) });
export const F = (subject = 'speaker', duration) => step('follow', { subject: ref(subject) }, duration ? { duration } : {});
export const Stop = duration => step('stop', {}, duration ? { duration } : {});
export const Pose = pose => step('pose', { pose });
export const I = (object, operation) => step('interact', { object: ref(object), operation });
export const C = (cargo, destination, movement = 'fly') => step('carry_to', { cargo: ref(cargo), destination: ref(destination), movement });
export const Tr = (trigger, target) => step('trigger', { trigger, ...(target ? { target: ref(target) } : {}) });
