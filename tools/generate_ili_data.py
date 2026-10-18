#!/usr/bin/env python3
"""Generate fake in-line inspection (ILI) data for the pipeline integrity study project.

Writes three files into the output directory:

  pipeline.json   Pipeline properties + route, the body for POST /pipelines
  ili_run_1.xlsx  First inspection
  ili_run_2.xlsx  Same pipeline, inspected again some years later

Run 2 contains the same defects as run 1, only deeper (they corroded), plus a few
new ones. Like real pig data, the distances drift slightly between runs and the
vendor's feature IDs are different, so matching the two runs is a real exercise.

Usage:
  pip install openpyxl
  python tools/generate_ili_data.py                    # defaults
  python tools/generate_ili_data.py --count 20000 --years 7 --seed 42 --out data
"""

import argparse
import json
import math
import random
from datetime import date
from pathlib import Path

from openpyxl import Workbook

# A 20" API 5L X52 gas line. Feel free to change these.
PIPELINE = {
    "name": "Demo Line 20in",
    "outerDiameterMm": 508.0,
    "wallThicknessMm": 8.7,
    "grade": "X52",
    "smysMpa": 359.0,
    "maopMpa": 7.0,
}

# Route waypoints (lat, lon). The pipe runs in straight segments between them.
WAYPOINTS = [
    (3.1390, 101.6869),
    (3.2100, 101.8000),
    (3.3500, 101.9000),
    (3.4200, 102.0500),
]

HEADERS = ["feature_id", "distance_m", "lat", "lon", "depth_pct", "length_mm"]
EARTH_RADIUS_M = 6_371_000


def segment_length_m(a, b):
    """Approximate ground distance between two (lat, lon) points (equirectangular)."""
    lat1, lon1 = map(math.radians, a)
    lat2, lon2 = map(math.radians, b)
    x = (lon2 - lon1) * math.cos((lat1 + lat2) / 2)
    y = lat2 - lat1
    return math.hypot(x, y) * EARTH_RADIUS_M


def build_route():
    """Return [(cumulative_distance_m, (lat, lon))] for each waypoint."""
    route = [(0.0, WAYPOINTS[0])]
    for a, b in zip(WAYPOINTS, WAYPOINTS[1:]):
        route.append((route[-1][0] + segment_length_m(a, b), b))
    return route


def position_at(route, distance_m):
    """Interpolate the (lat, lon) at a distance along the route."""
    for (d1, p1), (d2, p2) in zip(route, route[1:]):
        if distance_m <= d2:
            f = (distance_m - d1) / (d2 - d1)
            return p1[0] + f * (p2[0] - p1[0]), p1[1] + f * (p2[1] - p1[1])
    return route[-1][1]


def random_depth_pct(rng):
    """Most defects are shallow; a few are deep and worth digging up."""
    roll = rng.random()
    if roll < 0.85:
        return rng.uniform(5, 30)
    if roll < 0.97:
        return rng.uniform(30, 60)
    return rng.uniform(60, 80)


def make_run_1(rng, route, count):
    total_m = route[-1][0]
    defects = []
    for _ in range(count):
        distance = rng.uniform(0, total_m)
        defects.append({
            "distance_m": distance,
            "depth_mm": random_depth_pct(rng) / 100 * PIPELINE["wallThicknessMm"],
            "length_mm": rng.uniform(10, 300),
            # Hidden truth used to grow the defect for run 2. Not written to the file.
            "growth_mm_per_year": rng.uniform(0.1, 0.5),
        })
    return sorted(defects, key=lambda d: d["distance_m"])


def make_run_2(rng, route, run_1, years, new_count):
    total_m = route[-1][0]
    wt = PIPELINE["wallThicknessMm"]
    defects = []
    for d in run_1:
        defects.append({
            # Pig odometers drift, so the same defect is reported a little elsewhere.
            "distance_m": min(max(d["distance_m"] + rng.gauss(0, 0.3), 0), total_m),
            "depth_mm": min(d["depth_mm"] + d["growth_mm_per_year"] * years, 0.95 * wt),
            "length_mm": d["length_mm"] * rng.uniform(1.0, 1.15),
        })
    for _ in range(new_count):
        defects.append({
            "distance_m": rng.uniform(0, total_m),
            "depth_mm": rng.uniform(5, 20) / 100 * wt,
            "length_mm": rng.uniform(10, 100),
        })
    return sorted(defects, key=lambda d: d["distance_m"])


def write_xlsx(path, route, defects, id_prefix):
    wb = Workbook()
    ws = wb.active
    ws.title = "Anomalies"
    ws.append(HEADERS)
    for i, d in enumerate(defects, start=1):
        lat, lon = position_at(route, d["distance_m"])
        ws.append([
            f"{id_prefix}-{i:06d}",
            round(d["distance_m"], 2),
            round(lat, 6),
            round(lon, 6),
            round(d["depth_mm"] / PIPELINE["wallThicknessMm"] * 100, 1),
            round(d["length_mm"], 1),
        ])
    wb.save(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--count", type=int, default=5000, help="defects in run 1 (default 5000)")
    parser.add_argument("--years", type=float, default=5, help="years between run 1 and run 2 (default 5)")
    parser.add_argument("--seed", type=int, default=1, help="random seed, same seed = same files (default 1)")
    parser.add_argument("--out", type=Path, default=Path("sample-data"), help="output directory (default sample-data)")
    args = parser.parse_args()

    rng = random.Random(args.seed)
    route = build_route()
    args.out.mkdir(parents=True, exist_ok=True)

    run_1 = make_run_1(rng, route, args.count)
    run_2 = make_run_2(rng, route, run_1, args.years, new_count=args.count // 20)

    run_1_date = date(2020, 3, 1)
    run_2_date = date(run_1_date.year + round(args.years), 3, 1)

    pipeline = {
        **PIPELINE,
        "route": {"type": "LineString", "coordinates": [[lon, lat] for lat, lon in WAYPOINTS]},
        "runs": [
            {"file": "ili_run_1.xlsx", "inspectionDate": run_1_date.isoformat()},
            {"file": "ili_run_2.xlsx", "inspectionDate": run_2_date.isoformat()},
        ],
    }
    (args.out / "pipeline.json").write_text(json.dumps(pipeline, indent=2))
    write_xlsx(args.out / "ili_run_1.xlsx", route, run_1, "A")
    write_xlsx(args.out / "ili_run_2.xlsx", route, run_2, "B")

    print(f"Pipeline length: {route[-1][0] / 1000:.1f} km")
    print(f"Run 1 ({run_1_date}): {len(run_1)} anomalies")
    print(f"Run 2 ({run_2_date}): {len(run_2)} anomalies ({len(run_2) - len(run_1)} new)")
    print(f"Written to {args.out.resolve()}")


if __name__ == "__main__":
    main()
