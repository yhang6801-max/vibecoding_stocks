"""Input-contract regressions: prevent plausible-looking but incorrect quotes."""
import json
import os
from pathlib import Path
import shutil
import sys
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from project import ROOT, daily_csv, load_project


class ProjectContract(unittest.TestCase):
    def setUp(self):
        self.folder = ROOT / "output" / "test_inputs" / uuid.uuid4().hex
        self.folder.mkdir(parents=True)
        self.project = self.folder / "project.json"
        self.config = json.loads((ROOT / "examples/kospi/project.json").read_text(encoding="utf-8"))
        shutil.copyfile(ROOT / "examples/kospi/daily.csv", self.folder / "daily.csv")

    def tearDown(self):
        self.folder.resolve().relative_to((ROOT / "output" / "test_inputs").resolve())
        shutil.rmtree(self.folder)

    def load(self):
        self.project.write_text(json.dumps(self.config), encoding="utf-8")
        return load_project(self.project)

    def test_relative_data_path_is_independent_of_working_directory(self):
        previous = Path.cwd()
        try:
            os.chdir(ROOT / "tools")
            _, config, rows, path = self.load()
        finally:
            os.chdir(previous)
        self.assertEqual(path, self.folder / "daily.csv")
        self.assertEqual(config["full"]["data_end"], rows[-1]["date"])

    def test_event_prices_are_taken_from_csv(self):
        self.config["events"][0]["close"] = 999999
        _, config, rows, _ = self.load()
        self.assertEqual(config["events"][0]["close"], rows[0]["close"])

    def test_event_on_missing_trading_day_is_rejected(self):
        self.config["events"][0]["date"] = "1990-01-01"
        with self.assertRaisesRegex(ValueError, "实际交易日"):
            self.load()

    def test_overlapping_holds_are_rejected(self):
        self.config["events"][1]["arrival"] = 0
        with self.assertRaisesRegex(ValueError, "重叠"):
            self.load()

    def test_preset_cannot_silently_change_video_duration(self):
        self.config["video"]["duration"] = 60
        with self.assertRaisesRegex(ValueError, "固定规格"):
            self.load()

    def test_publishing_limits_include_hashtags(self):
        self.config["publishing"]["description"] = "话" * 998 + "#话题"
        with self.assertRaisesRegex(ValueError, "1000"):
            self.load()

    def test_invalid_csv_values_and_order_are_rejected(self):
        for content in (
            "date,close\n2024-01-02,10\n2024-01-02,11\n",
            "date,close\n2024-01-03,10\n2024-01-02,11\n",
            "date,close\n2024-01-02,nan\n2024-01-03,11\n",
            "date,close\n2024-01-02,0\n2024-01-03,11\n",
        ):
            with self.subTest(content=content):
                path = self.folder / "bad.csv"
                path.write_text(content, encoding="utf-8")
                with self.assertRaises(ValueError):
                    daily_csv(path)

    def test_both_examples_resolve_all_events_to_real_quotes(self):
        for name in ("kospi", "nikkei"):
            with self.subTest(example=name):
                _, config, rows, _ = load_project(ROOT / "examples" / name / "project.json")
                quotes = {r["date"]: r["close"] for r in rows}
                self.assertTrue(all(e["close"] == quotes[e["date"]] for e in config["events"]))


if __name__ == "__main__":
    unittest.main()
