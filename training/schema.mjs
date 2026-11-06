import { REFS, fixture } from '../eval/registry.mjs';
const text = { type: 'string' };
const list = items => ({ type: 'array', items });
const object = properties => ({ type: 'object', properties, required: Object.keys(properties), additionalProperties: false });
const span = object({text, occurrence:{type:'integer',minimum:0}});
const referenceId={type:'string',enum:[...Object.keys(REFS).filter(k=>k!=='result0'),...new Set(['imp','nurse:dead_red'].flatMap(id=>fixture(id).world.map(e=>'e:'+e.handle))),'default_target','random_inventory','result:0','result:1','unclear']};
export const ACTIONS = ['move','follow','flee','stop','guard','pose','attack','use_skill','use_item','pick_up','drop','give','throw','interact','carry_to','eat','call_allies','read_scripture','trigger','unclear','unsupported'];
export const caseSchema = object({
  id: text, family: text, transcript: { type:'string', maxLength:500 }, scene: text,
  intent_count: { type:'string', enum:['zero','one','two','three','four_plus','unclear'] },
  outcome: { type:'string', enum:['order','chat','unclear','unsupported'] },
  actions: { ...list(object({
    spans: list(span), template: { type:'string', enum:ACTIONS },
    slots: list(object({ name:text, ref:referenceId, evidence:list(span) })),
    variants: list(object({ name:text, value:text, evidence:list(span) }))
  })), maxItems:4 },
  excluded_spans: list(span), tags:list(text), design_refs:list(text), rationale:text
});
export const batchSchema = object({ cases:list(caseSchema) });
export const CONTRACT = Object.freeze({ protocol:3, bundleId:'bw-three-actions-1', maxSteps:3 });
