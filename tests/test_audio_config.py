import copy
import sys
import tempfile
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from project import ROOT, audio_enabled, load_project, write_json

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
