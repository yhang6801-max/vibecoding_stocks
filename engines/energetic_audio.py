"""Original 140 BPM electronic score for classic220; no external samples or song melodies."""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path
import numpy as np
import audio_instruments as inst
import cinematic_audio as mastering

SR=48000
BPM=140
BEAT=60/BPM
SEED=20261020

def energy(t):
    for end,value in [(8,.35),(30,.95),(45,.62),(97,.82),(124,.98),(139,.48),(185,.92),(190,.58),(218,1.05),(220,.2)]:
        if t<end:return value
    return 0

def cowbell(note, duration=.25):
    t=np.arange(round(SR*duration))/SR
    f=inst.hz(note)
    # Inharmonic metallic percussion, not a sampled cowbell or a borrowed hook.
    sig=(np.sin(2*np.pi*f*t)+.5*np.sin(2*np.pi*f*1.47*t)+.2*np.sin(2*np.pi*f*2.03*t))
    return np.tanh(sig*1.4)*inst.envelope(len(t),.002,.08,15)

def compose(config):
    duration=float(config['video']['duration'])
    count=round(duration*SR)
    inst.N=count;inst.RNG=np.random.default_rng(SEED)
    music=np.zeros((count,2),dtype=np.float32)
    drums=np.zeros_like(music);effects=np.zeros_like(music)
    drum={kind:inst.drum(kind) for kind in ['kick','snare','hat']}
    roots=[38,34,41,36] # D minor / B-flat / F / C
    chord_types=[(0,3,7),(0,4,7),(0,4,7),(0,4,7)]
    pads={i:inst.pad([roots[i]+24+n for n in chord_types[i]],4*BEAT+.9,1.1) for i in range(4)}
    notes={note:inst.pluck(note,.55,1.4) for note in range(58,85)}
    bass={note:inst.bass(note,.3) for note in roots}
    bells={note:cowbell(note) for note in range(70,85)}
    steps=[0,7,12,7,3,7,12,7]
    for bar in range(math.ceil((duration-3)/(4*BEAT))):
        start=3+bar*4*BEAT
        if start>=218:break
        chord=(bar//2)%4;root=roots[chord];power=energy(start)
        inst.add(music,pads[chord],start,.25*power)
        for step in range(8):
            at=start+step*BEAT/2
            if at>=218:continue
            inst.add(music,bass[root],at,.48*power)
            interval=steps[(step+bar%2*2)%8]
            if interval==3:interval=chord_types[chord][1]
            tone=root+24+interval
            if tone in notes: inst.add(music,notes[tone],at,.2*power,(-.35 if step%2 else .35))
            if bar%8>=4 and power>.6 and step in [0,3,4,6]:
                bell_note=74+[0,3,7,10][(step+bar)%4]
                inst.add(music,bells[bell_note],at,.075*power,(-.2 if step%2 else .2))
            if power>.6 or step in [0,4]:
                inst.add(drums,drum['hat'],at,.085*power,(-.45 if step%2 else .45))
            if step%2==0 and (power>.6 or step in [0,4]):
                inst.add(drums,drum['kick'],at,.9*power)
            if step in [2,6]: inst.add(drums,drum['snare'],at,.33*power)
        if bar%8==7 and power>.6:
            for n in range(4):inst.add(drums,drum['snare'],start+3*BEAT+n*BEAT/4,.1*(n+1)*power)
    # Tempo-synced pumping on the musical bed, leaving the kick transient clear.
    for begin in range(0,count,SR*8):
        stop=min(count,begin+SR*8)
        t=np.arange(begin,stop)/SR
        phase=np.mod(t-3,BEAT)
        gain=.5+.5*(1-np.exp(-phase/.09))
        music[begin:stop]*=gain[:,None]
    music+=drums
    del drums
    inst.add(music,inst.pad([50,57,62,65],4.0),0,.18)
    inst.add(effects,inst.whoosh(2.9),0,1.1)
    inst.add(effects,inst.impact(1.8,metal=True),3,.45)
    cues=[]
    for event in config['events']:
        at=float(event['arrival'])
        cues.append({'time':at,'id':event['id'],'label':event['label']})
        inst.add(effects,inst.whoosh(.7),at-.7,1.05)
        inst.add(effects,inst.impact(1.7,metal=not event.get('low',False)),at,.65 if event.get('major') else .42)
        # Brief duck around the actual event, independent of the musical beat grid.
        a=max(0,round((at-.1)*SR));b=min(count,round((at+.45)*SR))
        if b>a:music[a:b]*=np.linspace(.45,1,b-a)[:,None]
    inst.add(effects,inst.impact(1.8,metal=True),218,.32)
    # Leave room to read the closing question: beat bed drops at 218 seconds, tail resolves softly.
    end=round(218*SR)
    ramp_start=end-round(.15*SR)
    music[ramp_start:end]*=np.linspace(1,.12,end-ramp_start)[:,None]
    music[end:]*=.12
    fade_in=np.minimum(1,np.arange(count)/SR/.8)
    fade_out=np.minimum(1,(count-1-np.arange(count))/SR/1.5)
    for bus in (music,effects):bus*= (fade_in*fade_out)[:,None]
    raw=music+effects
    scale=min(1,.78/max(float(np.max(np.abs(raw))),1e-9))
    return music*scale,effects*scale,raw*scale,cues

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--project',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--ffmpeg',required=True)
    args=parser.parse_args()
    config=json.loads(args.project.read_text(encoding='utf-8-sig'))
    if config['preset']!='classic220' or config['video']['duration']!=220:raise ValueError('energetic220 requires classic220')
    args.output.mkdir(parents=True,exist_ok=True)
    mastering.OUT=args.output
    music,sfx,raw,cues=compose(config)
    for name,bus in [('music.wav',music),('sfx.wav',sfx),('mix_raw.wav',raw)]:mastering.save_wav24(args.output/name,bus)
    del music,sfx,raw
    target='loudnorm=I=-16:TP=-1.8:LRA=8'
    run=lambda opts,log:mastering.ffmpeg_run(args.ffmpeg,opts,log)
    first=mastering.loudnorm_json(run(['-i',str(args.output/'mix_raw.wav'),'-af',target+':print_format=json','-f','null','-'],'loudnorm_pass1.log'))
    measured=':'.join([f"measured_I={first['input_i']}",f"measured_TP={first['input_tp']}",f"measured_LRA={first['input_lra']}",f"measured_thresh={first['input_thresh']}",f"offset={first['target_offset']}"])
    run(['-y','-i',str(args.output/'mix_raw.wav'),'-af',target+':'+measured+':linear=true','-ar',str(SR),'-ac','2','-c:a','pcm_s24le',str(args.output/'mix.wav')],'loudnorm_pass2.log')
    manifest={'title':'Forty Years / Momentum','style':'Original driving electronic / phonk-inspired percussion','bpm':BPM,'duration':220,'seed':SEED,'source':'Procedural synthesis in energetic_audio.py and audio_instruments.py; no external recordings or song melodies','license':'MIT, repository original code and generated score','event_cues':cues,'ending':'Drums drop at 218 s for the closing question','mastering':{'integrated_lufs':-16,'true_peak_dbtp':-1.8,'pass1':first}}
    (args.output/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({'score':manifest['title'],'bpm':BPM,'events':len(cues),'output':str(args.output)}))
if __name__=='__main__':main()
