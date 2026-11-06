// Semantic questions only. Gameplay validation and execution stay in the host.
export const CLASSIFY_FIRST = 'You are a monster being commanded by a player. What kind of action are you being asked to do FIRST? A bare action or direction is a command. Follow corrections and before/after order. Ignore cancelled, negated or quoted requests. A polite question can be a command.';
export const CLASSIFY_SECOND = 'You are the same monster. After first_action_kind, what SECOND action does the player request? Choose none if there is no separate second action. Follow corrections and before/after order; ignore cancelled or quoted requests. Carrying someone to a place is one action, not separate grabbing and travelling actions.';

// Each entry belongs only to that action's detail calls. No omnibus rule block.
export const ACTION_RULES = Object.freeze({
  move: 'You are moving as instructed. A bare direction means move in that direction. Left/right/forward/back/up without a named frame is relative to you; my is relative to the speaking player. Ordinary travel is walking unless another movement is requested.',
  follow: 'You are following someone. Identify whom to follow and any stated duration. Me means the speaking player. Follow continues until its stated duration or the control ends.',
  flee: 'You are fleeing from a threat. Identify what to flee from and any stated duration. Preserve the named threat; do not replace it with a convenient nearby creature.',
  stop: 'You are stopping or waiting in place. Identify only a duration the player actually states. Without a stated duration, keep waiting until control ends.',
  guard: 'You are guarding a creature or place. Here means your current location, me means the speaking player. Preserve that ward and any stated duration.',
  pose: 'You are making a gesture. Choose the requested gesture: dance, bow, sit, wave or turn around. Turning around alone is a gesture, not travel.',
  attack: 'You are attacking as instructed. Attack/Kill with no target means one weakest attack on the nearest compatible target. Self-attack is once. Everyone, anybody or another monster means one attack on a random other monster. An explicitly named target otherwise means ongoing engagement unless the player asks for one hit. Preserve the requested target and any stated duration.',
  use_skill: 'You are using an ability. Identify the requested ability, even if you cannot use it. Fireball means the fireball skill, not throwing an item. If its target is omitted, use that skill on the nearest compatible target. Preserve explicit targets or directions; do not invent an ability.',
  use_item: 'You are activating an item. Identify the item and its target. Activation is different from physically throwing the item. If no item is specified, select a random eligible item you own; if no target is specified, use the nearest compatible target.',
  pick_up: 'You are picking up an item. Identify the item to acquire. If none is specified, pick up the nearest compatible item. The acquired item can be referred to by a later action.',
  drop: 'You are dropping an item you own. It or what you hold refers to your held item unless it explicitly refers to an item acquired by the previous action. Without any item reference, choose a random eligible owned item.',
  give: 'You are giving an item to someone. Identify the item and recipient separately. It after picking up an item refers to that acquired item. Me means the speaking player. Without an item reference, choose a random eligible owned item; without a recipient, choose the nearest compatible recipient.',
  throw: 'You are physically throwing an item. Identify the item and target or direction. Use a random eligible owned item when the item is omitted, and the nearest compatible target when the target is omitted. My left/right is the speaking player\'s frame; otherwise a bare direction is relative to you.',
  interact: 'You are operating an object. Identify the operation and object: pull a lever, open or close a door, push a crate. Without an object reference use the nearest object compatible with the operation. Do not invent an operation.',
  carry_to: 'You are carrying a passenger or cargo to a destination. Grab, carry, fly and deliver that same cargo to that destination form one carrying action. Identify cargo, destination and travel mode separately. Carrying defaults to walking unless flying or another mode is requested. Me means the speaking player.',
  eat: 'You are eating an item. Preserve the requested food or held-item reference. Without an item reference, choose the nearest compatible food.',
  call_allies: 'You are calling your allies. One request calls them once. This action needs no target or item.',
  read_scripture: 'You are reading or reciting scripture. Identify its source. A scripture in your hand is a held-item reference. Without a source reference, use the nearest compatible scripture.',
  trigger: 'You are reacting to a belief or accusation about you or a request for your secret. Identify the special meaning from your species meanings. A theft accusation must identify the accused; never invent that subject. Negated theft is not theft. I am a dragon describes the speaking player, not you. Only one special reaction is allowed; ignore later special reactions.'
});

export const REFERENCE_RULES = 'Identify the reference actually spoken. Me/I means the speaking player; you/yourself means you. Use a kind/species selector for a kind-only description, and a specific perceived entity for identifying details such as color. Here means your location. There/that one or an unknown name is not an omitted reference and must not be guessed.';
export const INVENTORY_RULES = 'Use your held-item reference when that is what the player describes. A named held item keeps its required kind. Use the previous action\'s item result only when the command refers to that acquired item.';

export function classificationState(text, first) {
  return { player_transcript: text, ...(first ? { first_action_kind: first } : {}) };
}

export function actionState(text, template, index, scene, previous) {
  if (!ACTION_RULES[template]) throw new Error('No rules for action ' + template);
  return {
    player_transcript: text,
    your_action: { number: index + 1, kind: template },
    rules: ACTION_RULES[template],
    ...(previous ? { previous_action: previous } : {}),
    ...(template === 'trigger' ? { your_species: scene.actor.species } : {})
  };
}
