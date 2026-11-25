"""Run only the supplied Python reference's pure technique module; never send keys.

No reference sources or outputs are written. Uses only the Python standard library.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import runpy
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-root", type=Path, required=True)
    parser.add_argument("--timeline", type=Path, default=Path(__file__).parent / "potatoab/main.timedata")
    parser.add_argument("--kps", type=float, default=7.0)
    parser.add_argument("--main-hand", choices=("left", "right"), default="right")
    args = parser.parse_args()
    if not math.isfinite(args.kps) or args.kps <= 0:
        parser.error("--kps must be finite and positive")

    reference = args.reference_root.resolve()
    source = reference / "app/technique.py"
    config = json.loads((reference / "config.json").read_text(encoding="utf-8-sig"))
    # Match the reference UI's four-key reversal on the left hand. Labels are
    # relevant for physical-key comparisons even though hand grouping is unchanged.
    left = config["left_keys"]
    left = [key for begin in range(0, len(left), 4) for key in reversed(left[begin:begin + 4])]
    right = config["right_keys"]
    if not left or not right or set(left) & set(right):
        parser.error("hand-label statistics require nonempty, disjoint left/right key sets")

    times_ms = [
        int(line.split(",")[0]) / 1000.0
        for line in args.timeline.read_text(encoding="utf-8-sig").splitlines()
        if line.strip()
    ]
    if not times_ms or any(a > b for a, b in zip(times_ms, times_ms[1:])):
        parser.error("timeline must contain ordered microsecond onsets")

    sys.dont_write_bytecode = True
    technique = runpy.run_path(str(source))["AdvancedTechnique"]
    assignments, holds = technique(
        left, right, single_finger_bpm=args.kps * 60.0, main_hand=args.main_hand
    ).simulate(times_ms)
    if len(assignments) != len(times_ms) or len(holds) != len(times_ms):
        raise ValueError("reference returned a different number of attacks or press durations")
    if any(not math.isfinite(value) or value < 0 for value in holds):
        raise ValueError("reference returned invalid press durations")

    left_set, right_set = set(left), set(right)
    hands = ["L" if key in left_set else "R" if key in right_set else "?" for key in assignments]
    if "?" in hands:
        raise ValueError("reference produced unmapped assignments")
    run_lengths = []
    previous = None
    for hand in hands:
        if hand != previous:
            run_lengths.append(0)
            previous = hand
        run_lengths[-1] += 1

    print(json.dumps({
        "source": str(source),
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "timeline_sha256": hashlib.sha256(args.timeline.read_bytes()).hexdigest(),
        "kps": args.kps,
        "main_hand": args.main_hand,
        "slots_per_hand": [len(left), len(right)],
        "onsets": len(times_ms),
        "assigned": len(assignments),
        "contiguous_hand_runs": len(run_lengths),
        "longest_hand_run": max(run_lengths),
        "first_24_hands": "".join(hands[:24]),
        "first_24_keys": assignments[:24],
        "median_hold_ms": sorted(holds)[len(holds) // 2],
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
