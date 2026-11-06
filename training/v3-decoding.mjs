// Constraints use only predicted distributions and the public three-action contract.
// No command words, corpus IDs, gold labels or monotonic surface-order assumptions.
export const COUNT_KEYS=['zero','one','two','three','four_plus','unclear'];
export const ROLE_KEYS=['action_0','action_1','action_2','overflow','ignored'];
const countValue={zero:0,one:1,two:2,three:3,four_plus:4,unclear:0};
const log=p=>Math.log(Math.max(Number(p),1e-30));
export const argmax=values=>values.reduce((best,value,index)=>value>values[best]?index:best,0);

function occupiedDecode(roleProbabilities,count){
  // A 16-state DP enforces every retained action having at least one word.
  // Roles may interleave or reverse in surface order (before/after constructions).
  let states=new Map([[0,{score:0,roles:[]}]]);
  const allowed=[...Array(count).keys(),4];
  for(const probabilities of roleProbabilities){
    const next=new Map();
    for(const [mask,state] of states)for(const role of allowed){
      const nextMask=role===4?mask:mask|(1<<role),score=state.score+log(probabilities[role]);
      if(!next.has(nextMask)||score>next.get(nextMask).score)next.set(nextMask,{score,roles:[...state.roles,role]});
    }
    states=next;
  }
  return states.get((1<<count)-1)??null;
}

export function decodeStructure(countProbabilities,roleProbabilities,method='independent'){
  if(countProbabilities.length!==6||roleProbabilities.some(row=>row.length!==5))throw new Error('Unexpected structure catalogue');
  const rawCount=argmax(countProbabilities),rawRoles=roleProbabilities.map(argmax);
  if(method==='independent')return {count:COUNT_KEYS[rawCount],roles:rawRoles.map(role=>ROLE_KEYS[role]),method};
  if(method==='joint_preserve_count'){
    const joint=decodeStructure(countProbabilities,roleProbabilities,'joint_coverage');
    const baseline=decodeStructure(countProbabilities,roleProbabilities,'independent');
    const selected=countValue[joint.count]>=countValue[baseline.count]?joint:baseline;
    return {...selected,method,selectedRuntimeArm:selected.method};
  }
  if(method==='count_mask'){
    const allowed=[...Array(countValue[COUNT_KEYS[rawCount]]).keys(),4];
    return {count:COUNT_KEYS[rawCount],roles:roleProbabilities.map(row=>ROLE_KEYS[allowed[argmax(allowed.map(index=>row[index]))]]),method};
  }
  if(method!=='joint_coverage')throw new Error('Unknown structure decoding method');
  let best=null;
  for(const [index,key] of COUNT_KEYS.entries()){
    const candidate=occupiedDecode(roleProbabilities,countValue[key]);
    if(!candidate)continue;
    const score=log(countProbabilities[index])+candidate.score;
    if(!best||score>best.score)best={count:key,roles:candidate.roles.map(role=>ROLE_KEYS[role]),score,method};
  }
  return best??{count:'unclear',roles:roleProbabilities.map(()=>'ignored'),method};
}

export function focusTexts(text,words,roles,index){
  const pieces=[];let start=null,end=null;
  for(const [i,word] of words.entries()){
    if(roles[i]==='action_'+index){if(start===null)start=word.start;end=word.end;}
    else if(start!==null){pieces.push(text.slice(start,end));start=end=null;}
  }
  if(start!==null)pieces.push(text.slice(start,end));
  return pieces;
}
