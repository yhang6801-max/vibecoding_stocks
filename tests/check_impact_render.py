"""Check actual impact cues and unchanged states: OUTPUT BASELINE_FULL_OUTPUT."""
import csv,json,sys
from pathlib import Path
root=Path(sys.argv[1])
baseline=Path(sys.argv[2])
record=json.loads((root/'build_record.json').read_text())
cue_files=list((root/'chunks').glob('*/impact_cues.json'))
assert len({p.read_bytes() for p in cue_files})==1, 'Chunk cue schedules differ'
cues=json.loads(cue_files[0].read_text())
rows=list(csv.DictReader(open('examples/nikkei/daily.csv', encoding='utf-8-sig')))
quotes={r['date']:float(r['close']) for r in rows}
# Independent offline threshold crossing list; only prior samples participate.
mode=0; hi=lo=float(rows[0]['close']); confirmations={}
for r in rows[1:]:
 v=float(r['close']);hi=max(hi,v);lo=min(lo,v)
 if not mode:
  if v>=lo*1.08: mode=1;hi=v
  elif v<=hi*.92: mode=-1;lo=v
 elif mode==1 and v<=hi*.92:
  confirmations[r['date']]=('阶段回落确认',(v/hi-1)*100)
  mode=-1;hi=lo=v
 elif mode==-1 and v>=lo*1.08:
  confirmations[r['date']]=('阶段反弹确认',(v/lo-1)*100)
  mode=1;hi=lo=v
old=[json.loads(x) for x in (baseline/'frame_states.jsonl').read_text().splitlines()]
new=[json.loads(x) for x in (root/'frame_states.jsonl').read_text().splitlines()]
assert new==old[record['start_frame']:record['start_frame']+record['frames']]
for c in cues:
 assert c['close']==quotes[c['date']]
 if not c['key']:
  title,change=confirmations[c['date']]
  assert c['title']==title
  assert f'{change:.1f}%' in c['detail']
  frame=round(c['time']*30)
  assert old[frame]['date']>=c['date'] and old[frame-1]['date']<c['date'], 'Backdated or late confirmation'
keys=[c for c in cues if c['key']]
project=json.loads(Path('examples/nikkei/project.json').read_text())
assert [(c['date'],c['time']) for c in keys]==[(e['date'],e['arrival']) for e in project['events']]
print(f'PASS: {len(cues)} cues, unchanged {len(new)} states, identical chunk schedules, causal threshold crossings, all 17 event timings')
