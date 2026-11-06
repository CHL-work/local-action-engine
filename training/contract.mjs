import Ajv from 'ajv';
import { caseSchema, CONTRACT } from './schema.mjs';
import { fixture, ref, omittedReference, SKILLS, TRIGGERS } from '../eval/registry.mjs';
import { slots, variants } from '../eval/interpreter.mjs';
import { validateProposal, compileDirective } from '../eval/host.mjs';

const checkShape = new Ajv({allErrors:true}).compile(caseSchema);
export const tokens = text => [...text.matchAll(/[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*/gu)].map(m => ({ text:m[0], start:m.index, end:m.index+m[0].length }));
export function locate(text, snippet) {
  let start=-1;
  for(let i=0;i<=snippet.occurrence;i++) {
    start=text.indexOf(snippet.text,start+1);
    if(start<0 || !snippet.text) throw new Error('Evidence occurrence is not in the transcript: '+snippet.text);
  }
  return { start, end:start+snippet.text.length };
}
export function reference(id) {
  if (/^result:[01]$/.test(id)) return {kind:'step_result',step:Number(id.slice(-1)),output:'item'};
  return ref(id);
}
export function proposal(c) {
  return {
    protocol:3,bundle_id:CONTRACT.bundleId,session_id:'training-cast',lease_id:'training-lease',snapshot_id:'training-snapshot',request_id:'training-request',
    outcome:c.outcome === 'order' ? {kind:'order',steps:c.actions.slice(0,3).map(a=>{
      const v=Object.fromEntries(a.variants.map(v=>[v.name,v.value]));
      const args=Object.fromEntries(a.slots.map(s=>[s.name,reference(s.ref)]));
      for (const key of ['movement','skill','trigger','pose','operation']) if (v[key]) args[key]=v[key];
      if (a.template==='call_allies') args.call='default';
      return {template:a.template,args,...(v.mode?{mode:v.mode}:{}),...(v.duration && v.duration!=='unspecified'?{duration:v.duration}:{})};
    })} : {kind:'unresolved',reason:c.outcome}
  };
}
export function validateCase(c) {
  const errors=[];
  const {provenance,host,...annotation}=c;
  if (!checkShape(annotation)) return checkShape.errors.map(e=>e.instancePath+' '+e.message);
  let scene;
  try { scene=fixture(c.scene); } catch(e) { return [e.message]; }
  const expectedCount={zero:0,one:1,two:2,three:3,four_plus:4}[c.intent_count];
  if (expectedCount !== undefined && c.actions.length!==expectedCount) errors.push('intent_count must match actions (four_plus stores first four)');
  if (c.outcome==='order' && !c.actions.length) errors.push('Order needs an action');
  const ranges=[];
  for (const [i,a] of c.actions.entries()) {
    try {
      if (!a.spans.length) throw new Error('Action needs evidence');
      const ownRanges=a.spans.map(snippet=>locate(c.transcript,snippet));
      for (const range of ownRanges) ranges.push({...range,step:i});
      const checkEvidence=e=>{const r=locate(c.transcript,e);if(!ownRanges.some(s=>r.start>=s.start&&r.end<=s.end))throw new Error('Argument evidence must be inside its own action span');};
      const v=Object.fromEntries(a.variants.map(v=>[v.name,v.value]));
      if (new Set(a.slots.map(s=>s.name)).size!==a.slots.length || new Set(a.variants.map(s=>s.name)).size!==a.variants.length) throw new Error('Duplicate slot or variant');
      const expectedVariants=variants(a.template,i);
      if (a.variants.map(v=>v.name).sort().join()!==Object.keys(expectedVariants).map(k=>k.replace(/^s\d+_/, '')).sort().join()) throw new Error('Missing or extra variants');
      for (const entry of a.variants) {
        if (!(entry.value in expectedVariants['s'+i+'_'+entry.name].criteria)) throw new Error('Unknown variant answer');
        for (const e of entry.evidence) checkEvidence(e);
      }
      const required=Object.keys(slots(a.template,v));
      if (required.sort().join()!==a.slots.map(s=>s.name).sort().join()) throw new Error('Missing or extra reference slots');
      for (const s of a.slots) {
        for (const e of s.evidence) checkEvidence(e);
        if (!s.evidence.length) {
          const fallback=omittedReference(a.template,s.name);
          if (!fallback || JSON.stringify(reference(s.ref))!==JSON.stringify(fallback)) throw new Error('Omission must use deterministic slot default');
        } else if (['default_target','random_inventory'].includes(s.ref)) throw new Error('Supplied reference cannot use omission default');
        if (s.ref !== 'unclear') {
          const r=reference(s.ref);
          if (r.kind==='step_result' && !(r.step<i && ['pick_up','drop','give','throw'].includes(c.actions[r.step].template))) throw new Error('Invalid earlier result reference');
        }
      }
    } catch(e) { errors.push('action '+i+': '+e.message); }
  }
  for (const s of c.excluded_spans) {
    try { ranges.push({...locate(c.transcript,s),step:-1}); } catch(e) { errors.push(e.message); }
  }
  for (let i=0;i<ranges.length;i++) for (let j=0;j<i;j++) if (ranges[i].step!==ranges[j].step && ranges[i].start<ranges[j].end && ranges[j].start<ranges[i].end) errors.push('Contradictory action/exclusion spans');
  if (c.actions.slice(0,3).filter(a=>a.template==='trigger').length>1) errors.push('Only one special trigger is supported');
  if (c.outcome==='order') {
    try { errors.push(...validateProposal(proposal(c),scene,CONTRACT)); } catch(e) { errors.push(e.message); }
  }
  return [...new Set(errors)];
}
export function hostResult(c) { return compileDirective(proposal(c),fixture(c.scene),{contract:CONTRACT,random:()=>0}); }
export function semanticSignature(c) {
  return JSON.stringify({count:c.intent_count,outcome:c.outcome,actions:c.actions.map(a=>({
    template:a.template,slots:a.slots.map(s=>[s.name,s.ref,s.evidence.length>0]).sort(),variants:a.variants.map(v=>[v.name,v.value]).sort()
  }))});
}
export function tokenRoles(c) {
  const spans=c.actions.flatMap((a,i)=>a.spans.map(s=>({...locate(c.transcript,s),role:i<3?'action_'+i:'overflow'})));
  spans.push(...c.excluded_spans.map(s=>({...locate(c.transcript,s),role:'excluded'})));
  return tokens(c.transcript).map(t=>{
    const roles=[...new Set(spans.filter(s=>s.start<t.end&&t.start<s.end).map(s=>s.role))];
    if(roles.length>1) throw new Error('Token overlaps incompatible action labels');
    return roles[0]??'other';
  });
}
