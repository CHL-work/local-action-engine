import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { validateCase, tokens, tokenRoles } from './contract.mjs';
import { fixture, TEMPLATES, referenceOptions } from '../eval/registry.mjs';
import { variants, slots } from '../eval/interpreter.mjs';
import { ACTION_GROUPS, choiceGroups } from '../eval/experiments/structure.mjs';
import { actionState, REFERENCE_RULES } from '../eval/prompts.mjs';

export function compileCase(c) {
  const errors=validateCase(c);if(errors.length) throw new Error(c.id+': '+errors.join('; '));
  const rows=[],scene=fixture(c.scene);
  const add=(stage,state,id,instructions,criteria,answer)=>{
    if(!(answer in criteria)) throw new Error('Answer missing from candidate catalogue: '+id+' '+answer);
    if(Object.keys(criteria).length>8) throw new Error('Oversized choice group: '+id);
    rows.push({state,questions:{[id]:{type:'choice',instructions,criteria}},expected:{[id]:answer},provenance:{caseId:c.id,family:c.family,stage,split:c.provenance?.split??'train',inputFormat:'marked_word_and_focus_text_v1',supervision:'teacher-forced; runtime must supply predicted parents'}});
  };
  const state={player_transcript:c.transcript};
  add('structure',state,'action_count','You are a monster hearing this command. How many distinct actions are requested after corrections? A passenger grab-and-transport is one action.',{zero:'No action.',one:'One action.',two:'Two actions.',three:'Three actions.',four_plus:'Four or more actions.',unclear:'Cannot establish the number.'},c.intent_count);
  const words=tokens(c.transcript),roles=tokenRoles(c);
  for(const [i,t] of words.entries()) add('span_membership',{marked_transcript:c.transcript.slice(0,t.start)+'<focus>'+t.text+'</focus>'+c.transcript.slice(t.end)},'word_role','Which requested action in EXECUTION order does the word inside <focus> belong to? Include its arguments. Carrying a passenger to a place is one action. Ignore quoted, negated and retracted requests, connectors and unrelated text.',{action_0:'First action.',action_1:'Second action.',action_2:'Third action.',overflow:'Fourth or later action.',ignored:'Not part of a retained request: cancelled, negated, quoted, filler or unrelated text.'},['excluded','other'].includes(roles[i])?'ignored':roles[i]);
  add('outcome',state,'meaning','Does this speech contain an interpretable supported command?',{order:'An interpretable supported order, even if your abilities or time prevent execution.',chat:'Conversation with no request.',unclear:'Insufficient or ambiguous meaning or unsupported completion boundary.',unsupported:'Clear meaning outside the command catalogue.'},c.outcome);
  for(const [i,a] of c.actions.slice(0,3).entries()) {
    const spanState={...state,action_number:i+1,focus_text:a.spans.map(s=>s.text),earlier_actions:c.actions.slice(0,i).map(p=>({kind:p.template}))};
    const group=Object.entries(ACTION_GROUPS).find(([,g])=>g.keys.includes(a.template))?.[0];
    if(!group) throw new Error('No family for '+a.template);
    add('action_family',spanState,'action_family','You are a monster obeying focus_text. What kind of action is it? Use the full command only for context.',Object.fromEntries(Object.entries(ACTION_GROUPS).map(([k,g])=>[k,g.label])),group);
    const kinds=Object.fromEntries(ACTION_GROUPS[group].keys.filter(k=>k in TEMPLATES).map(k=>[k,TEMPLATES[k]]));
    add('action_type',spanState,'action_kind','Which action is requested in focus_text?',kinds,a.template);
    if(['unclear','unsupported'].includes(a.template)) continue;
    const v=Object.fromEntries(a.variants.map(v=>[v.name,v.value]));
    const detail={...actionState(c.transcript,a.template,i,scene),focus_text:spanState.focus_text,earlier_actions:c.actions.slice(0,i).map(p=>({kind:p.template,slots:p.slots.map(s=>({name:s.name,ref:s.ref}))}))};
    function tree(stage,id,question,answer,depth=0) {
      const entries=Object.keys(question.criteria);
      if(entries.length<=8) return add(stage,detail,id,question.instructions,question.criteria,answer);
      let groups=choiceGroups(question,scene);
      // result:0/1 are typed inventory references in the new contract.
      for(const [k,g] of Object.entries(groups)) {
        const moved=g.keys.filter(x=>/^result:/.test(x));
        if(moved.length&&k!=='inventory_reference') {
          g.keys=g.keys.filter(x=>!moved.includes(x));
          (groups.inventory_reference??={label:'An item held or produced by an earlier action.',keys:[]}).keys.push(...moved);
        }
      }
      groups=Object.fromEntries(Object.entries(groups).filter(([,g])=>g.keys.length));
      if(Object.keys(groups).length>8 || Object.values(groups).some(g=>g.keys.length===entries.length) || depth>5) throw new Error('Unbounded grouping tree');
      const selected=Object.entries(groups).find(([,g])=>g.keys.includes(answer))?.[0];
      add(stage+'_group',detail,id+'_group_'+depth,question.instructions+' Which group contains the answer?',Object.fromEntries(Object.entries(groups).map(([k,g])=>[k,g.label])),selected);
      tree(stage,id,{...question,criteria:Object.fromEntries(groups[selected].keys.map(k=>[k,question.criteria[k]]))},answer,depth+1);
    }
    for(const [id,q] of Object.entries(variants(a.template,i))) tree('variant',id,q,v[id.replace(/^s\d+_/,'')]);
    for(const [slot,type] of Object.entries(slots(a.template,v))) {
      const annotation=a.slots.find(s=>s.name===slot);
      add('reference_presence',detail,'s'+i+'_'+slot+'_presence','Does the command supply words for the '+slot+'? Pronouns and unknown names count as supplied.',{supplied:'Words supply this reference, even if it is ambiguous.',omitted:'No words supply this reference.'},annotation.evidence.length?'supplied':'omitted');
      if(!annotation.evidence.length) continue;
      const refs=referenceOptions(scene,type,i,c.actions[i-1]?.template);
      if(type==='destination') Object.assign(refs,referenceOptions(scene,'item',i,c.actions[i-1]?.template));
      delete refs.result0;
      if(type==='item') for(let j=0;j<i;j++) if(['pick_up','drop','give','throw'].includes(c.actions[j].template)) refs['result:'+j]={label:'The item produced by earlier action '+(j+1)};
      tree('reference','s'+i+'_'+slot,{type:'choice',instructions:REFERENCE_RULES+' Identify the '+slot+' for this action. Earlier result references retain exact item identity.',criteria:Object.fromEntries(Object.entries(refs).map(([k,r])=>[k,r.label]))},annotation.ref);
    }
  }
  return rows;
}

if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const input=process.argv[2];if(!input) throw new Error('Usage: node training/compile.mjs training/data/<run>/accepted.jsonl');
  const cases=(await readFile(input,'utf8')).trim().split('\n').filter(Boolean).map(JSON.parse);
  const rows=cases.flatMap(compileCase),output=path.join(path.dirname(input),'decisions.train.jsonl');
  if(cases.some(c=>c.provenance?.split!=='train')) throw new Error('This command only exports the training pilot, never calibration/test');
  const contents=rows.map(r=>JSON.stringify(r)).join('\n')+'\n';
  await writeFile(output,contents);
  const byCount={};for(const c of cases) {const b=byCount[c.intent_count]??={commands:0,decisions:0};b.commands++;b.decisions+=compileCase(c).length;}
  const stages={};for(const r of rows) stages[r.provenance.stage]=(stages[r.provenance.stage]??0)+1;
  const summary={commands:cases.length,decisionItems:rows.length,byCount,stages,sha256:createHash('sha256').update(contents).digest('hex'),trainingStarted:false,limitations:['Draft machine-reviewed supervision; human spot review still needed.','Token-membership parser is a proposed learned replacement for the bounded grammar; inference integration and end-to-end evaluation are not implemented.','Parent actions/spans are teacher-forced here; this file alone does not measure end-to-end accuracy.']};
  await writeFile(path.join(path.dirname(input),'compilation.json'),JSON.stringify(summary,null,2));console.log(JSON.stringify(summary,null,2));
}
