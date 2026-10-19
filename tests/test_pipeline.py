"""Cache integrity and structured metadata regressions."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from chunk_cache import complete, reusable
from renderer import select_backend
from stock_video import merge_states
from project import ROOT, prepare


class PipelineTests(unittest.TestCase):
    def test_cache_requires_matching_inputs_and_intact_outputs(self):
        with tempfile.TemporaryDirectory() as d:
            folder = Path(d)
            video, states = folder/'part.mp4', folder/'states.jsonl'
            video.write_bytes(b'encoded'); states.write_text('{"frame":0}\n')
            inputs = {'start': 0, 'count': 1, 'encoder': 'libx264', 'scale': .5}
            self.assertFalse(reusable(folder, inputs, video, states))
            complete(folder, inputs, video, states)
            self.assertTrue(reusable(folder, inputs, video, states))
            for key, value in [('start', 1), ('encoder', 'other'), ('scale', 1)]:
                self.assertFalse(reusable(folder, dict(inputs, **{key:value}), video, states))
            video.write_bytes(b'corrupt')
            self.assertFalse(reusable(folder, inputs, video, states))
            video.write_bytes(b'encoded'); states.unlink()
            self.assertFalse(reusable(folder, inputs, video, states))

    def test_merge_rejects_gap_at_chunk_boundary(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d); parts=[]
            for i, frames in enumerate(([30,31], [33,34])):
                p=folder/f'{i}.jsonl';p.write_text(''.join(json.dumps({'frame':f})+'\n' for f in frames));parts.append(p)
            with self.assertRaisesRegex(ValueError, '不连续'):
                merge_states(parts, folder/'all.jsonl', 30, 4)

    def test_font_paths_resolve_relative_to_project(self):
        with tempfile.TemporaryDirectory() as d:
            folder=Path(d)
            config=json.loads((ROOT/'examples/kospi/project.json').read_text())
            config['data']=str(ROOT/'examples/kospi/daily.csv')
            config['fonts']={'text_file':'font.ttf'}
            (folder/'font.ttf').write_bytes(b'font')
            (folder/'project.json').write_text(json.dumps(config))
            _, prepared, _, _, _=prepare(folder/'project.json',folder/'out')
            self.assertEqual(prepared['fonts']['text_file'], str((folder/'font.ttf').resolve()))

    def test_non_windows_defaults_to_portable_renderer(self):
        with patch('renderer.os.name', 'posix'):
            self.assertEqual(select_backend(), 'skia')
        self.assertEqual(select_backend('windows'), 'windows')


if __name__ == '__main__':
    unittest.main()
