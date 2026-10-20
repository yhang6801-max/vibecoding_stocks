"""Verification reads structured metadata and rejects plausible but wrong media."""
from pathlib import Path
import json
import sys
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from project import ROOT, digest, load_project, write_json
from verify import verify


class VerifyTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.out=Path(self.temp.name)
        self.project=ROOT/'examples/nikkei/project.json'
        _, self.config, self.rows, csv=load_project(self.project)
        self.video=self.out/'preview.mp4';self.video.write_bytes(b'test-media')
        self.record={'video':'preview.mp4','video_sha256':digest(self.video),
            'input_hashes':{'project':digest(self.project),'daily':digest(csv)},
            'width':270,'height':480,'preview_scale':.25,'fps':30,'frames':2,'start_frame':0,
            'duration':2/30,'audio':False,'kind':'preview'}
        write_json(self.out/'build_record.json',self.record)
        (self.out/'frame_states.jsonl').write_text(''.join(json.dumps(dict(frame=i,date=self.rows[0]['date'],
            close=self.rows[0]['close'],visible_last_date=self.rows[0]['date']))+'\n' for i in range(2)))
        self.metadata={'streams':[dict(codec_type='video',codec_name='h264',pix_fmt='yuv420p',width=270,height=480,avg_frame_rate='30/1')],
            'format':{'duration':str(2/30)}}

    def tearDown(self):
        self.temp.cleanup()

    def run_verify(self):
        with patch('verify.probe_media',return_value=self.metadata), patch('verify.run',return_value=SimpleNamespace(stdout='frame=2\nprogress=end\n')):
            return verify(self.project,self.out,'ffmpeg')

    def test_structured_metadata_and_machine_progress(self):
        self.assertTrue(self.run_verify()['passed'])

    def test_requested_score_cannot_pass_as_silent(self):
        _, config, rows, csv = load_project(ROOT/'examples/nikkei/project-energetic.json')
        with patch('verify.load_project', return_value=(self.project, config, rows, csv)):
            with self.assertRaisesRegex(ValueError, '媒体验收失败'):
                self.run_verify()

    def test_replaced_external_audio_fails_source_integrity(self):
        _, config, rows, csv = load_project(self.project)
        audio=self.out/'music.wav';source=self.out/'source.json'
        audio.write_bytes(b'updated master');write_json(source,{'origin':'original'})
        config['audio']={'score':'external220','file':str(audio),'provenance':str(source)}
        self.record['input_hashes'].update(external_audio_file='old-master-hash',external_audio_provenance=digest(source))
        write_json(self.out/'build_record.json',self.record)
        with patch('verify.load_project',return_value=(self.project,config,rows,csv)):
            with self.assertRaisesRegex(ValueError,'媒体验收失败'):self.run_verify()
        checks=json.loads((self.out/'verification.json').read_text())['checks']
        self.assertFalse(next(c['passed'] for c in checks if c['name']=='current_external_audio'))

    def test_wrong_frame_rate_is_rejected(self):
        self.metadata['streams'][0]['avg_frame_rate']='24/1'
        with self.assertRaisesRegex(ValueError,'媒体验收失败'):
            self.run_verify()

    def test_missing_state_is_rejected(self):
        p=self.out/'frame_states.jsonl';p.write_text(p.read_text().splitlines()[0]+'\n')
        with self.assertRaisesRegex(ValueError,'媒体验收失败'):
            self.run_verify()

    def test_low_resolution_cannot_pass_as_full_video(self):
        self.record['kind']='full';write_json(self.out/'build_record.json',self.record)
        with self.assertRaisesRegex(ValueError,'媒体验收失败'):
            self.run_verify()

if __name__=='__main__':
    unittest.main()
