// Runtime counterpart of the frozen v3 compiler: every parent is predicted.
import {tokens,reference} from './contract.mjs';
import {fixture,TEMPLATES,referenceOptions,omittedReference} from '../eval/registry.mjs';
import {variants,slots} from '../eval/interpreter.mjs';
import {ACTION_GROUPS,choiceGroups} from '../eval/experiments/structure.mjs';
import {actionState,REFERENCE_RULES} from '../eval/prompts.mjs';
import {decodeStructure,focusTexts,COUNT_KEYS,ROLE_KEYS} from './v3-decoding.mjs';
import {CONTRACT} from './schema.mjs';
export const question=(instructions,criteria)=>({type:'choice',instructions,criteria});
export const COUNT_QUESTION=question('You are a monster hearing this command. How many distinct actions are requested after corrections? A passenger grab-and-transport is one action.',{zero:'No action.',one:'One action.',two:'Two actions.',three:'Three actions.',four_plus:'Four or more actions.',unclear:'Cannot establish the number.'});
export const ROLE_QUESTION=question('Which requested action in EXECUTION order does the word inside <focus> belong to? Include its arguments. Carrying a passenger to a place is one action. Ignore quoted, negated and retracted requests, connectors and unrelated text.',{action_0:'First action.',action_1:'Second action.',action_2:'Third action.',overflow:'Fourth or later action.',ignored:'Not part of a retained request: cancelled, negated, quoted, filler or unrelated text.'});
export function envelope(outcome){return {protocol:3,bundle_id:CONTRACT.bundleId,session_id:'training-cast',lease_id:'training-lease',snapshot_id:'training-snapshot',request_id:'training-request',outcome};}

export async function inferV3(transcript,sceneId,ask,{method='joint_preserve_count',scene:sceneSnapshot}={}){
  // The unconstrained joint experiment increased three-action wrong execution.
  // Its full implementation/evidence remains in the immutable run snapshot.
  if(!['independent','joint_preserve_count'].includes(method))throw new Error('Only baseline or measured conservative decoding is enabled');
  // Gameplay supplies a host-owned snapshot. Historical callers retain the exact
  // fixture path; copying prevents later world changes from altering a request.
  const scene=sceneSnapshot===undefined?fixture(sceneId):structuredClone(sceneSnapshot),state={player_transcript:transcript},trace=[],actions=[];
  const result={method,trace,actions};
  async function call(stage,state,id,q){
    const response=await ask(state,{[id]:q},stage);trace.push(response);
    const answer=response.response.answers[id],keys=Object.keys(q.criteria);
    if(!answer||!keys.includes(answer.choice)||keys.some(k=>!Number.isFinite(answer.probabilities[k])))throw new Error('Malformed classification response');
    return answer;
  }
  const unresolved=(reason,detail)=>({...result,outcome:{kind:'unresolved',reason},unresolvedDetail:detail});
  const count=await call('structure',state,'action_count',COUNT_QUESTION),words=tokens(transcript),wordAnswers=[];
  for(const word of words)wordAnswers.push(await call('span_membership',{marked_transcript:transcript.slice(0,word.start)+'<focus>'+word.text+'</focus>'+transcript.slice(word.end)},'word_role',ROLE_QUESTION));
  const structure=decodeStructure(COUNT_KEYS.map(k=>count.probabilities[k]),wordAnswers.map(a=>ROLE_KEYS.map(k=>a.probabilities[k])),method);
  Object.assign(result,{structure,words,overflow:structure.count==='four_plus'});
  const meaning=await call('outcome',state,'meaning',question('Does this speech contain an interpretable supported command?',{order:'An interpretable supported order, even if your abilities or time prevent execution.',chat:'Conversation with no request.',unclear:'Insufficient or ambiguous meaning or unsupported completion boundary.',unsupported:'Clear meaning outside the command catalogue.'}));
  if(meaning.choice!=='order')return unresolved(meaning.choice,'Predicted overall meaning');
  const actionCount={one:1,two:2,three:3,four_plus:3}[structure.count];
  if(!actionCount)return unresolved('unclear','Order has no predicted action span');
  for(let i=0;i<actionCount;i++){
    const focus_text=focusTexts(transcript,words,structure.roles,i);
    if(!focus_text.length)return unresolved('unclear','Missing predicted span for action '+(i+1));
    const spanState={...state,action_number:i+1,focus_text,earlier_actions:actions.map(a=>({kind:a.template}))};
    const group=(await call('action_family',spanState,'action_family',question('You are a monster obeying focus_text. What kind of action is it? Use the full command only for context.',Object.fromEntries(Object.entries(ACTION_GROUPS).map(([k,g])=>[k,g.label]))))).choice;
    const template=(await call('action_type',spanState,'action_kind',question('Which action is requested in focus_text?',Object.fromEntries(ACTION_GROUPS[group].keys.filter(k=>k in TEMPLATES).map(k=>[k,TEMPLATES[k]]))))).choice;
    if(['chat','unclear','unsupported','none'].includes(template))return unresolved(template==='none'?'unclear':template,'Predicted action type');
    const detail={...actionState(transcript,template,i,scene),focus_text,earlier_actions:actions.map(a=>({kind:a.template,slots:a.slots}))};
    async function tree(stage,id,q,depth=0){
      const entries=Object.keys(q.criteria);
      if(entries.length<=8)return (await call(stage,detail,id,q)).choice;
      let groups=choiceGroups(q,scene);
      for(const [key,g] of Object.entries(groups)){
        const moved=g.keys.filter(k=>/^result:/.test(k));
        if(moved.length&&key!=='inventory_reference'){
          g.keys=g.keys.filter(k=>!moved.includes(k));
          (groups.inventory_reference??={label:'An item held or produced by an earlier action.',keys:[]}).keys.push(...moved);
        }
      }
      groups=Object.fromEntries(Object.entries(groups).filter(([,g])=>g.keys.length));
      if(Object.keys(groups).length>8||Object.values(groups).some(g=>g.keys.length===entries.length)||depth>5)throw new Error('Unbounded grouping tree');
      const group=(await call(stage+'_group',detail,id+'_group_'+depth,question(q.instructions+' Which group contains the answer?',Object.fromEntries(Object.entries(groups).map(([k,g])=>[k,g.label]))))).choice;
      return tree(stage,id,{...q,criteria:Object.fromEntries(groups[group].keys.map(k=>[k,q.criteria[k]]))},depth+1);
    }
    const v={},slotValues=[];
    for(const [id,q] of Object.entries(variants(template,i))){
      const value=await tree('variant',id,q);
      if(['unclear','unsupported','vague'].includes(value))return unresolved(value==='unsupported'?'unsupported':'unclear','Unresolved '+id);
      v[id.replace(/^s\d+_/,'')]=value;
    }
    for(const [slot,type] of Object.entries(slots(template,v))){
      const presence=(await call('reference_presence',detail,'s'+i+'_'+slot+'_presence',question('Does the command supply words for the '+slot+'? Pronouns and unknown names count as supplied.',{supplied:'Words supply this reference, even if it is ambiguous.',omitted:'No words supply this reference.'}))).choice;
      let id;
      if(presence==='omitted'){
        const fallback=omittedReference(template,slot);
        if(!fallback)return unresolved('unclear','No omission default for '+slot);
        id=['give','drop','throw','use_item'].includes(template)&&slot==='item'?'random_inventory':'default_target';
      }else{
        const refs=referenceOptions(scene,type,i,actions[i-1]?.template);
        if(type==='destination')Object.assign(refs,referenceOptions(scene,'item',i,actions[i-1]?.template));
        delete refs.result0;
        if(type==='item')for(let j=0;j<i;j++)if(['pick_up','drop','give','throw'].includes(actions[j].template))refs['result:'+j]={label:'The item produced by earlier action '+(j+1)};
        id=await tree('reference','s'+i+'_'+slot,question(REFERENCE_RULES+' Identify the '+slot+' for this action. Earlier result references retain exact item identity.',Object.fromEntries(Object.entries(refs).map(([k,r])=>[k,r.label]))));
        if(id==='unclear')return unresolved('unclear','Unresolved '+slot);
      }
      slotValues.push({name:slot,ref:id});
    }
    actions.push({template,focus_text,variants:v,slots:slotValues});
  }
  const steps=actions.map(a=>{
    const args=Object.fromEntries(a.slots.map(s=>[s.name,reference(s.ref)]));
    for(const key of ['movement','skill','trigger','pose','operation'])if(a.variants[key])args[key]=a.variants[key];
    if(a.template==='call_allies')args.call='default';
    return {template:a.template,args,...(a.variants.mode?{mode:a.variants.mode}:{}),...(a.variants.duration&&a.variants.duration!=='unspecified'?{duration:a.variants.duration}:{})};
  });
  return {...result,outcome:{kind:'order',steps}};
}
