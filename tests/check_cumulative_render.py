"""Integration check against real rendered states, not mocked chart functions.

Run: python tests/check_cumulative_render.py OUTPUT_DIR [CSV]
"""
import calendar
import csv
import json
import sys
from pathlib import Path


def check(output, csv_path):
    with csv_path.open(newline='', encoding='utf-8-sig') as handle:
        rows = [(r['date'], float(r['close'])) for r in csv.DictReader(handle)]
    quotes = dict(rows)
    bounds = {}
    low = high = rows[0][1]
    for date, value in rows:
        low, high = min(low, value), max(high, value)
        bounds[date] = (low, high)
    states = [json.loads(line) for line in (output / 'frame_states.jsonl').read_text().splitlines()]
    assert len(states) == 6600, 'Expected complete classic220 render'
    left = states[0]['camera_left']
    previous_span = 0
    for index, state in enumerate(states):
        assert state['frame'] == index, (index, 'frame gap')
        assert state['close'] == quotes[state['date']], (index, 'quote changed')
        assert state['visible_first_date'] == rows[0][0], (index, 'history dropped')
        assert state['camera_left'] == left, (index, 'start moved')
        assert state['camera_span'] >= previous_span, (index, 'horizontal expansion reversed')
        previous_span = state['camera_span']
        low, high = bounds[state['date']]
        assert state['y_min'] <= low <= high <= state['y_max'], (index, 'historical extreme clipped')
        year, month, day = map(int, state['date'].split('-'))
        coordinate = year * 12 + month - 1 + (day-1) / calendar.monthrange(year, month)[1]
        assert 0 <= coordinate - left < state['camera_span'], (index, 'latest quote outside plot')
    assert states[-1]['date'] == rows[-1][0], 'Final trading day missing'
    result = {'passed': True, 'frames': len(states), 'fixed_left': left,
              'first_date': rows[0][0], 'final_date': states[-1]['date'],
              'historical_extremes_visible': True, 'span_nondecreasing': True}
    (output / 'cumulative_checks.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    check(Path(sys.argv[1]), Path(sys.argv[2]) if len(sys.argv) > 2 else Path('examples/nikkei/daily.csv'))
