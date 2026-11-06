"""Bounded, sequential fp16 evaluation. Never modifies or stops a trainer."""
import contextlib
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import sys
import time

os.environ.setdefault('USE_TF', '0')
os.environ.setdefault('HF_HUB_OFFLINE', '1')
os.environ.setdefault('TRANSFORMERS_OFFLINE', '1')
sys.stdin.reconfigure(encoding='utf-8')
sys.stdout.reconfigure(encoding='utf-8')
checkpoint = Path(sys.argv[1]).resolve()
def emit(value):
    print(json.dumps(value, ensure_ascii=False, allow_nan=False), flush=True)

with contextlib.redirect_stdout(sys.stderr):
    import torch
    import laya.train as train
    from laya.common import build_sequence, build_head, render_options, collate_items, uses_parallel_layout
    torch.set_num_threads(2)
    torch.manual_seed(0)
    if not torch.cuda.is_available():
        raise RuntimeError('CUDA required; no fallback')
    device = torch.device('cuda:0')
    free, total = torch.cuda.mem_get_info(device)
    if free < 8 * 1024**3:
        raise RuntimeError('Less than 8 GiB free before evaluation load; leaving trainer untouched')
    torch.cuda.set_per_process_memory_fraction((4 * 1024**3) / total, device)
    model, tok, cfg = train.load_checkpoint(str(checkpoint))
    if cfg.get('training', {}).get('protocol') != 3:
        raise RuntimeError('Expected a protocol-3 trained checkpoint')
    max_len, head_max_len = cfg['max_len'], cfg['head_max_len']
    model = model.half().to(device).eval()
    parallel = uses_parallel_layout(cfg)
    digest = hashlib.sha256()
    with (checkpoint / 'model.safetensors').open('rb') as handle:
        for block in iter(lambda: handle.read(1024*1024), b''):
            digest.update(block)
metadata = {'checkpoint': str(checkpoint), 'weightsSha256': digest.hexdigest(), 'device': torch.cuda.get_device_name(),
            'dtype': 'float16 weights, float16 autocast; logits float32', 'microbatch': 1, 'temperature': 1,
            'allocatorLimitBytes': 4*1024**3, 'initialFreeBytes': free, 'max_len': max_len, 'head_max_len': head_max_len,
            'packages': {p: importlib.metadata.version(p) for p in ['torch','laya','transformers']},
            'note': 'Checkpoint saved in fp16; training validation used in-memory fp32 weights with fp16 autocast. No gold inputs or confidence calibration.'}
emit({'ready': metadata})

for line in sys.stdin:
    message = json.loads(line)
    try:
        with contextlib.redirect_stdout(sys.stderr):
            # Leave headroom for the independent trainer. This process never kills another PID.
            free, _ = torch.cuda.mem_get_info(device)
            if free < 4 * 1024**3:
                raise RuntimeError('GPU pressure: fewer than 4 GiB free; stopping evaluation')
            state, questions = message['state'], message['questions']
            if len(questions) != 1:
                raise ValueError('Exactly one decision per inference is required')
            qid, external = next(iter(questions.items()))
            q = train.to_internal(qid, external)
            ids, markers, option_stats, state_stats = build_sequence(tok,state,q,max_len,head_max_len,return_stats=True,return_truncation_stats=True)
            head, head_markers, _ = build_head(tok,q,head_max_len)
            ins = tok.encode(q['t']+' question: '+str(q['ins']).replace(tok.mask_token,' '),add_special_tokens=False)
            instruction_used = head.index(tok.sep_token_id)-1
            cuts = []
            for i, option in enumerate(render_options(q)):
                full = tok.encode(' '+option.replace(tok.mask_token,' '),add_special_tokens=False)
                end = head_markers[i+1] if i+1<len(head_markers) else len(head)-1
                used = end-head_markers[i]-1
                if used<len(full): cuts.append({'option':list(external['criteria'])[i],'tokens':len(full),'used':used})
            audit = {'sequenceTokens':len(ids),'instructionTokens':len(ins),'instructionTokensUsed':instruction_used,
                     'instructionTruncated':instruction_used<len(ins),'optionCuts':cuts,'options':option_stats,'state':state_stats,
                     'effectiveInputText':tok.decode(ids,skip_special_tokens=False)}
            # Compare to an unlimited-context rendering as an independent truncation check.
            full_ids, _ = build_sequence(tok,state,q,32768,32768)
            if cuts or instruction_used<len(ins) or len(ids)!=len(full_ids):
                raise ValueError('Input truncation is forbidden: '+json.dumps(audit))
            # Dummy target is required by the installed tensor adapter, never used by inference.
            row={'state':state,'questions':questions,'expected':{qid:next(iter(external['criteria']))}}
            items, skipped=train.items_from_rows(tok,[row],max_len,head_max_len)
            if skipped or len(items)!=1: raise ValueError('Invalid inference item: '+str(skipped))
            encoded=train.encode_item(tok,items[0],max_len,head_max_len,parallel=parallel)
            batch=collate_items([[encoded]],tok.pad_token_id)
            torch.cuda.synchronize(); began=time.perf_counter()
            with torch.inference_mode():
                logits=train._forward(model,batch,device,True,False)[0,:len(external['criteria'])]
                if not torch.isfinite(logits).all(): raise ValueError('Non-finite classifier logits')
                probabilities=torch.softmax(logits,dim=-1).cpu().tolist()
                values=logits.cpu().tolist()
            torch.cuda.synchronize(); elapsed=(time.perf_counter()-began)*1000
            keys=list(external['criteria']); choice=keys[max(range(len(keys)),key=lambda i:probabilities[i])]
            response={'answers':{qid:{'type':'choice','choice':choice,'probabilities':dict(zip(keys,probabilities)),
                                      'logits':dict(zip(keys,values))}}}
        emit({'id':message['id'],'response':response,'responseText':json.dumps(response,ensure_ascii=False,allow_nan=False),
              'inferenceMs':elapsed,'inputAudit':{qid:audit}})
    except Exception as error:
        emit({'id':message['id'],'error':type(error).__name__+': '+str(error)})
        # Model/runtime/pressure errors are fatal. Parent may resume the completed rows later.
        break
