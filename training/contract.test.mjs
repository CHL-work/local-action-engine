import test from 'node:test';
import assert from 'node:assert/strict';
import { CONTRACT } from './schema.mjs';
import { validateCase, proposal, tokenRoles, hostResult } from './contract.mjs';
import { compileCase } from './compile.mjs';
import { validateProposal } from '../eval/host.mjs';
import { fixture } from '../eval/registry.mjs';

const span=text=>({text,occurrence:0});
const slot=(name,ref,text)=>({name,ref,evidence:text?[span(text)]:[]});
const variant=(name,value,text)=>({name,value,evidence:text?[span(text)]:[]});
const sample=()=>({id:'test',family:'pickup-move-give',transcript:'Pick up the key, walk right, then give it to me.',scene:'imp',intent_count:'three',outcome:'order',actions:[
  {template:'pick_up',spans:[span('Pick up the key')],slots:[slot('item','key','the key')],variants:[]},
  {template:'move',spans:[span('walk right')],slots:[slot('destination','right','right')],variants:[variant('movement','walk','walk'),variant('duration','unspecified')]},
  {template:'give',spans:[span('give it to me')],slots:[slot('item','result:0','it'),slot('recipient','speaker','me')],variants:[]}
],excluded_spans:[],tags:['three'],design_refs:['TDD 9'],rationale:'The acquired key remains the referent after moving.'});
test('v3 accepts three steps and retains item identity across movement with one shared clock',()=>{
  const c=sample();assert.deepEqual(validateCase(c),[]);
  const host=hostResult(c);assert.equal(host.processable,true);assert.equal(host.disposition,'execute');
  assert.equal(host.directive.durationSeconds,5);assert.equal(host.directive.steps.length,3);
  assert.deepEqual(host.directive.steps[2].args.item,{kind:'step_result',step:0,output:'item'});
  assert(host.directive.tasks.every(t=>t.sharesAbsoluteExpiry));
});
test('old protocol rejects v3; v3 rejects four steps, forward refs and non-producing refs',()=>{
  const p=proposal(sample());assert(validateProposal(p,fixture()).length);
  p.outcome.steps.push(p.outcome.steps[0]);assert(validateProposal(p,fixture(),CONTRACT).length);
  for(const step of [1,2,-1]) { const q=proposal(sample());q.outcome.steps[2].args.item.step=step;assert(validateProposal(q,fixture(),CONTRACT).length); }
});
test('v3 movement can use a perceived item location as its destination',()=>{
  const p=proposal(sample());p.outcome.steps[1].args.destination={kind:'entity',handle:'k1'};
  assert.deepEqual(validateProposal(p,fixture(),CONTRACT),[]);
});
test('omitted object uses default; a supplied ambiguous object never gets nearest by default',()=>{
  const c={...sample(),transcript:'Attack',intent_count:'one',actions:[{template:'attack',spans:[span('Attack')],slots:[slot('target','default_target')],variants:[variant('mode','single'),variant('duration','unspecified')]}]};
  assert.deepEqual(validateCase(c),[]);assert.equal(hostResult(c).directive.tasks[0].maxActivations,1);
  c.actions[0].slots[0].evidence=[span('Attack')];assert(validateCase(c).some(e=>e.includes('omission default')));
});
test('token membership supports reversed execution order, repeated words and exclusions',()=>{
  const c={...sample(),transcript:'Bow then bow then bow',actions:[0,1,2].map(i=>({template:'pose',spans:[{text:i?'bow':'Bow',occurrence:i?i-1:0}],slots:[],variants:[variant('pose','bow')]}))};
  assert.deepEqual(tokenRoles(c),['action_0','other','action_1','other','action_2']);
  [c.actions[0],c.actions[2]]=[c.actions[2],c.actions[0]];
  assert.deepEqual(tokenRoles(c),['action_2','other','action_1','other','action_0']);
});
test('compiler builds bounded decisions without putting gold labels in structure input',()=>{
  const rows=compileCase(sample());assert(rows.length>20);
  for(const r of rows) for(const [id,q] of Object.entries(r.questions)) {assert(Object.keys(q.criteria).length<=8);assert(r.expected[id] in q.criteria);}
  assert.deepEqual(rows[0].state,{player_transcript:sample().transcript});
  assert(rows.some(r=>Object.values(r.expected).includes('result:0')));
  assert(!rows.some(r=>JSON.stringify(r.state).includes(sample().rationale)));
  const membership=rows.filter(r=>r.provenance.stage==='span_membership');
  assert(membership.some(r=>r.expected.word_role==='ignored'));
  assert(membership.every(r=>Object.keys(r.questions.word_role.criteria).length===5));
  assert(membership.every(r=>r.state.marked_transcript.replace(/<\/?focus>/g,'')===sample().transcript));
  assert(rows.filter(r=>r.provenance.stage==='action_family').every((r,i)=>JSON.stringify(r.state.focus_text)===JSON.stringify(sample().actions[i].spans.map(s=>s.text))));
});
test('repeated words are distinguished by in-context focus markers, without a gold action label',()=>{
  const c={...sample(),transcript:'Bow then bow then bow',actions:[0,1,2].map(i=>({template:'pose',spans:[{text:i?'bow':'Bow',occurrence:i?i-1:0}],slots:[],variants:[variant('pose','bow')]}))};
  const rows=compileCase(c).filter(r=>r.provenance.stage==='span_membership'&&/<focus>bow<\/focus>/i.test(r.state.marked_transcript));
  assert.equal(new Set(rows.map(r=>r.state.marked_transcript)).size,3);
  assert.deepEqual(rows.map(r=>r.expected.word_role),['action_0','action_1','action_2']);
  assert(rows.every(r=>Object.keys(r.state).length===1));
});
test('argument evidence cannot belong to a different action',()=>{
  const c=sample();c.actions[0].slots[0].evidence=[span('me')];
  assert(validateCase(c).some(e=>e.includes('own action span')));
});
