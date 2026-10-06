"""Self-hosted sensitivity model for artist workflows and source activities.

This is arithmetic, not a Temporal benchmark. All event-size assumptions are
explicit so a later local pilot can replace them with measurements.
"""
import csv
import json
from pathlib import Path

out = Path(__file__).parent / 'evidence'
artists = 4374
active_artists = 37
requests = artists + 9 * active_artists
provider_scans = 4379 + 9 * active_artists  # assumes one mapping per active artist
rows = []
for label, dirty_sources, repeats in [('daily_low', 500, 30), ('daily_high', 5000, 30), ('full_catalog_once', 289718, 1)]:
    # One fetch/normalize/transaction activity/source. Three coarse activities
    # and twenty events/artist request are placeholders for listing/finalizing.
    activities = 3 * requests + dirty_sources
    events = 20 * requests + 6 * dirty_sources
    history_gb = events * 1024 / 1e9
    rows.append(dict(scenario=label, source_items=dirty_sources,
        artist_requests=requests, provider_scans=provider_scans,
        modeled_activities=activities, modeled_events=events,
        history_GB_at_1KiB_per_event=round(history_gb, 4),
        repeats=repeats,
        HTTP_hours_at_1_RPS=round((provider_scans+dirty_sources)/3600, 3),
        HTTP_hours_at_5_RPS=round((provider_scans+dirty_sources)/18000, 3),
        HTTP_hours_at_10_RPS=round((provider_scans+dirty_sources)/36000, 3)))
with (out/'capacity-scenarios.csv').open('w') as f:
    w = csv.DictWriter(f, fieldnames=rows[0].keys()); w.writeheader(); w.writerows(rows)
print(json.dumps(rows, indent=2))
print('GD at 500 source activities/run:', -(-18200//500), 'runs')
print('Daily artist children at 250/run:', -(-4374//250), 'parent runs')
