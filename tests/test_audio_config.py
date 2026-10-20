import copy
import sys
import tempfile
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from project import ROOT, audio_enabled, load_project, prepare, write_json
from stock_video import inputs_hash

class AudioConfigTests(unittest.TestCase):
    def test_existing_silent_and_cinematic_contracts(self):
        _, silent, _, _=load_project(ROOT/'examples/nikkei/project.json')
        _, cinematic, _, _=load_project(ROOT/'examples/kospi/project.json')
        self.assertFalse(audio_enabled(silent))
        self.assertTrue(audio_enabled(cinematic))

    def test_explicit_score_keeps_timeline_and_data(self):
        _, silent, rows, _=load_project(ROOT/'examples/nikkei/project.json')
        _, sound, sound_rows, _=load_project(ROOT/'examples/nikkei/project-energetic.json')
        self.assertTrue(audio_enabled(sound))
        self.assertEqual(rows,sound_rows)
        self.assertEqual(silent['video'],sound['video'])
        self.assertEqual(silent['events'],sound['events'])

    def test_unknown_score_is_rejected(self):
        _, config, _, data=load_project(ROOT/'examples/nikkei/project.json')
        config['data']=str(data);config['audio']={'score':'missing-generator'}
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'project.json';write_json(path,config)
            with self.assertRaisesRegex(ValueError,'audio.score'):load_project(path)

    def test_external_audio_paths_and_replacement_invalidate_fingerprint(self):
        _, config, _, data=load_project(ROOT/'examples/nikkei/project.json')
        config['data']=str(data)
        config['audio']={'score':'external220','file':'music.wav','provenance':'source.json'}
        with tempfile.TemporaryDirectory() as folder:
            folder=Path(folder);path=folder/'project.json'
            write_json(path,config);write_json(folder/'source.json',{'origin':'original composition'})
            (folder/'music.wav').write_bytes(b'first master')
            _, prepared, _, _, _=prepare(path,folder/'out')
            self.assertTrue(audio_enabled(prepared))
            self.assertEqual(prepared['audio']['file'],str((folder/'music.wav').resolve()))
            first=inputs_hash(path,data)
            (folder/'music.wav').write_bytes(b'new master at same filename')
            self.assertNotEqual(first,inputs_hash(path,data))

    def test_external_audio_requires_provenance(self):
        _, config, _, data=load_project(ROOT/'examples/nikkei/project.json')
        config['data']=str(data)
        with tempfile.TemporaryDirectory() as folder:
            folder=Path(folder);(folder/'music.wav').write_bytes(b'fixture')
            config['audio']={'score':'external220','file':'music.wav'}
            path=folder/'project.json';write_json(path,config)
            with self.assertRaisesRegex(ValueError,'provenance'):load_project(path)
