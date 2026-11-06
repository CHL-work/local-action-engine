// Experimental English clause separation. No corpus IDs, gold labels, or scene state.
// A bounded grammar, not a general semantic parser; unrecognized structures stay whole.
const VERBS = 'move|walk|run|go|jump|fly|follow|flee|stop|wait|guard|protect|dance|bow|sit|wave|turn|attack|hit|kill|strike|cast|use|fireball|heal|revive|resurrect|pick|grab|carry|transport|deliver|drop|give|hand|pass|throw|open|close|pull|push|eat|swallow|call|summon|read|recite|teleport';
const startsAction = new RegExp('^(?:(?:please|just|you|then|first|second|third)\\s+)*(?:' + VERBS + ')\\b', 'i');
const clean = s => s.trim().replace(/^[,;.!\s]+|[,;.!\s]+$/g, '').replace(/^(?:and\s+then|and|then|first|second|third)\s+/i, '').trim();
function normalize(s) {
  return clean(s).replace(/^you\s+/i, '').replace(/^(dancing|bowing|dropping|attacking|pulling|opening|closing|running|walking|giving|throwing|picking|waiting|following)\b/i, word => ({ dancing:'dance', bowing:'bow', dropping:'drop', attacking:'attack', pulling:'pull', opening:'open', closing:'close', running:'run', walking:'walk', giving:'give', throwing:'throw', picking:'pick', waiting:'wait', following:'follow' })[word.toLowerCase()]);
}
const negated = s => /^(?:please\s+)?(?:do not|don't|never mind|ignore my request)\b/i.test(s);

export function separateActions(text) {
  const original = text.trim();
  const notes = [];
  // Preserve quoted/hypothetical/corrective context for the model rather than executing a quote.
  if (/["“”]/.test(text) || /\b(?:sorry|I mean|actually|take that back|if I had|rather than)\b|[—–]\s*no\b/i.test(text)) {
    return { method:'bounded-english-grammar-v1', segments:[original], notes:['Complex correction/quotation kept intact; separation may miss a later action.'], uncertain:true };
  }
  function split(s, depth=0) {
    s=clean(s);
    if (!s || depth>12) return s ? [s] : [];
    let m;
    if ((m=s.match(/^before\s+(.+?),\s*(.+)$/i))) {
      notes.push('before: reverse surface order');
      return [...split(m[2],depth+1),...split(normalize(m[1]),depth+1)];
    }
    if ((m=s.match(/^after\s+(.+?),\s*(.+)$/i))) {
      notes.push('after: retain surface order');
      return [...split(normalize(m[1]),depth+1),...split(m[2],depth+1)];
    }
    if ((m=s.match(/^(.+?),?\s+but first\s+(.+)$/i))) {
      notes.push('but first: move trailing request before earlier requests');
      return [...split(m[2],depth+1),...split(m[1],depth+1)];
    }
    // Only split when the next fragment starts a catalog action or explicit cancellation.
    // Commas inside descriptions and conjunctions inside noun phrases remain intact.
    const boundaries=[...s.matchAll(/(?:[,;.!]+\s*|\s+(?:and then|then|and)\s+)/gi)];
    for (const boundary of boundaries) {
      const left=clean(s.slice(0,boundary.index));
      const right=clean(s.slice(boundary.index+boundary[0].length));
      if (left && right && (startsAction.test(right)||negated(right))) return [...split(left,depth+1),...split(right,depth+1)];
    }
    return [normalize(s)];
  }
  let segments=split(original);
  if (segments.length>1 && segments.some(s=>!negated(s))) {
    const filtered=segments.filter(s=>!negated(s));
    if (filtered.length!==segments.length) notes.push('Removed explicitly cancelled clauses beside active requests');
    segments=filtered;
  }
  // Adjacent pickup/carry and travel of a passenger is one carry intention.
  // Ordinary item pickup followed by giving/throwing remains two actions.
  const combined=[];
  for (let i=0;i<segments.length;i++) {
    const first=segments[i], next=segments[i+1];
    if (next && /^(?:grab|pick up|carry)\s+(?:me|him|her|them|the\s+\w+\s+player)\b/i.test(first) && /^(?:fly|carry|transport|deliver)\s+(?:me|him|her|them|the\s+\w+\s+player)\s+to\b/i.test(next)) {
      combined.push(first+' and '+next); i++; notes.push('Grouped passenger pickup and transport as one action');
    } else combined.push(first);
  }
  return { method:'bounded-english-grammar-v1', segments:combined.length?combined:[original], notes, uncertain:false };
}

// These cover the entire intent catalogue, including unknown/unsupported meanings.
export const ACTION_GROUPS = {
  travel: { label:'Move, follow, flee, or transport a passenger to a destination.', keys:['move','follow','flee','carry_to'] },
  combat_magic: { label:'Attack or kill, cast a spell, heal, revive, or use an ability.', keys:['attack','use_skill'] },
  inventory: { label:'Pick up, drop, give, throw, activate, or eat an item.', keys:['pick_up','drop','give','throw','use_item','eat'] },
  posture_duty: { label:'Wait, stop, guard, protect, dance, bow, sit, wave, or turn around.', keys:['stop','guard','pose'] },
  objects_communication: { label:'Operate a door or lever, call allies, or recite scripture.', keys:['interact','call_allies','read_scripture'] },
  special_reaction: { label:'A theft accusation, belief about you, or request for hidden treasure.', keys:['trigger'] },
  no_supported_action: { label:'Conversation, unintelligible meaning, or a request outside these actions.', keys:['chat','unclear','unsupported','none'] }
};

// Trees partition existing options; no gold-dependent retrieval or capability filtering.
export function choiceGroups(question, scene) {
  const keys=Object.keys(question.criteria);
  let groups;
  if (keys.includes('carry_to') && keys.includes('attack')) groups=ACTION_GROUPS;
  else if (keys.includes('unspecified') && keys.includes('until_expiry')) groups={
    unstated:{label:'No duration is explicitly stated.',keys:['unspecified']},
    seconds:{label:'An explicit whole number of seconds between one and seven.',keys:['1s','2s','3s','4s','5s','6s','7s']},
    control_end:{label:'Explicitly until brainwash or control ends.',keys:['until_expiry']},
    other_duration:{label:'Vague duration, forever, fractional duration, or another unsupported duration.',keys:['vague','unsupported']}
  };
  else if (keys.includes('fireball') && keys.includes('revive')) groups={
    offense:{label:'Fireball, ice bolt, acid pool, ultimate or strongest offensive ability.',keys:['fireball','ice_bolt','acid_pool','ultimate','strongest']},
    recovery:{label:'Heal a living creature or revive a dead player.',keys:['heal','revive']},
    unknown_skill:{label:'Missing, ambiguous, or uncatalogued ability.',keys:['unclear','unsupported']}
  };
  else {
    const buckets={};
    for (const k of keys) {
      let group;
      if (['self','speaker','here'].includes(k)) group='participants_place';
      else if (/^(?:my_)?(?:forward|back|left|right|up|down)$/.test(k)) group=k.startsWith('my_')?'speaker_direction':'listener_direction';
      else if (k.startsWith('held')||k==='result0') group='inventory_reference';
      else if (k.startsWith('e:')) group='observed_entity';
      else if (['unclear','unsupported'].includes(k)) group='unknown_reference';
      else if (['goblin','golem','mouse','nearest_goblin','behind_goblin','another','nearest_monster','largest_monster','smallest_monster','wounded_monster','key_carrier'].includes(k)) group='monster_description';
      else group='object_description';
      (buckets[group]??=[]).push(k);
    }
    const labels={participants_place:'Yourself, the speaking player, or here at your current location.',speaker_direction:'A direction relative to the speaking player: my left, my right, behind me, etc.',listener_direction:'A direction relative to you: left, right, forward, back, up, down.',inventory_reference:'An item held in your hand or acquired by the previous action.',observed_entity:'One observed entity identified by details such as color or appearance.',unknown_reference:'An ambiguous, unknown, hidden, map-only, or unsupported reference.',monster_description:'A monster described by species, rank, position, or held item; search if absent.',object_description:'An item, object, or destination described only by its kind.'};
    groups=Object.fromEntries(Object.entries(buckets).map(([k,list])=>[k,{label:labels[k],keys:list}]));
    if (Object.keys(groups).length===1) {
      if (buckets.monster_description) groups={
        species:{label:'A goblin, ice golem, mouse, nearest goblin, or goblin behind you.',keys:['goblin','golem','mouse','nearest_goblin','behind_goblin']},
        properties:{label:'Another monster or a monster selected by size, injury, proximity, or held item.',keys:['another','nearest_monster','largest_monster','smallest_monster','wounded_monster','key_carrier']}
      };
      else if (buckets.observed_entity) {
        const entityGroups={};
        for (const k of keys) {
          const entity=scene.perceived.find(e=>e.handle===k.slice(2));
          const family=entity?.kind??'unknown'; (entityGroups[family]??=[]).push(k);
        }
        groups=Object.fromEntries(Object.entries(entityGroups).map(([kind,list])=>[kind,{label:'A specifically described observed '+kind+'.',keys:list}]));
      }
    }
  }
  const result=Object.fromEntries(Object.entries(groups).map(([k,g])=>[k,{...g,keys:g.keys.filter(id=>keys.includes(id))}]).filter(([,g])=>g.keys.length));
  if (keys.includes('none') && result.no_supported_action) result.no_supported_action.label='No separate second action, an unclear request, or an unsupported request.';
  const flattened=Object.values(result).flatMap(g=>g.keys);
  if (flattened.length!==keys.length || new Set(flattened).size!==keys.length || keys.some(k=>!flattened.includes(k))) throw new Error('Hierarchy must partition every original option exactly once');
  if (Object.keys(result).length<2 || Object.keys(result).length>8) throw new Error('Cannot form a meaningful bounded hierarchy');
  return result;
}
