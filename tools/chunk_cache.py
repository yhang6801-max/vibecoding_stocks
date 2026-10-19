"""Reuse only complete chunks whose inputs and output bytes still match."""
from __future__ import annotations
from pathlib import Path
from project import digest, read_json, write_json


def reusable(folder: Path, expected: dict, video: Path, states: Path):
    manifest = folder / 'complete.json'
    try:
        saved = read_json(manifest)
        return (saved['inputs'] == expected and saved['video'] == digest(video)
                and saved['states'] == digest(states))
    except (OSError, ValueError, KeyError, TypeError):
        return False


def complete(folder: Path, expected: dict, video: Path, states: Path):
    target = folder / 'complete.json'
    temporary = target.with_suffix('.tmp')
    write_json(temporary, {'inputs': expected, 'video': digest(video), 'states': digest(states)})
    temporary.replace(target)
