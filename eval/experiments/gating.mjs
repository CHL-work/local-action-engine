export function decisionFamily(id) {
  const core=id.replace(/_group_\d+$/,'');
  const grouped=core!==id;
  let family;
  if(['first','action_kind'].includes(core))family='intent';
  else if(core==='second'||core==='overflow')family='sequence';
  else if(core==='relationship')family='separation';
  else if(core.endsWith('_presence'))family='presence';
  else if(/_(duration|skill|trigger|pose|operation|movement|mode)$/.test(core))family=core.match(/_(\w+)$/)[1];
  else family='reference';
  return (grouped?'group:':'')+family;
}
export function scaledProbabilities(probabilities,temperature=1){
  if(temperature===1)return probabilities;
  const entries=Object.entries(probabilities).map(([k,p])=>[k,Math.pow(Math.max(1e-12,p),1/temperature)]);
  const sum=entries.reduce((s,[,p])=>s+p,0);return Object.fromEntries(entries.map(([k,p])=>[k,p/sum]));
}
export function choiceGate(answer,id,policy){
  const rule=policy?.stages?.[decisionFamily(id)]??{};
  const probabilities=scaledProbabilities(answer.probabilities,rule.temperature??1);
  const probability=probabilities[answer.choice];
  const margin=probability-Math.max(0,...Object.entries(probabilities).filter(([k])=>k!==answer.choice).map(([,p])=>p));
  const minProbability=rule.minProbability??0.65,minMargin=rule.minMargin??0.15;
  return {probability,margin,accepted:probability>=minProbability&&margin>=minMargin,
    ...(policy?{rawProbability:answer.probabilities[answer.choice],calibration:{family:decisionFamily(id),temperature:rule.temperature??1,minProbability,minMargin}}:{})};
}
