#!/usr/bin/env python3
"""Summarizes one publish configuration's startup samples into the shared JSON and Markdown report.

Split out of measure-startup.sh so the statistics are testable and so the Markdown table that lands
in ADR-0007 and the README is generated rather than retyped -- a hand-computed median in a document
is a number nobody can check.
"""
import json
import os
import statistics
from pathlib import Path

ORDER = ["fdd", "sc", "sc-trim", "sc-r2r", "tool"]
LABELS = {
    "fdd": "A - framework-dependent (JIT baseline)",
    "sc": "B - self-contained",
    "sc-trim": "C - self-contained, trimmed (TrimMode=full)",
    "sc-r2r": "D - self-contained, ReadyToRun",
    "tool": "E - installed dotnet tool",
}


def samples(name):
    raw = os.environ.get(name, "")
    return [float(x) for x in raw.split(",") if x]


def main():
    out = Path(os.environ["MEASURE_OUT"])
    cid = os.environ["MEASURE_ID"]

    ready, exits, rss = samples("MEASURE_READY"), samples("MEASURE_EXIT"), samples("MEASURE_RSS")

    path = out / "startup-metrics.json"
    data = json.loads(path.read_text()) if path.exists() else {"configs": {}}

    data["configs"][cid] = {
        "label": LABELS.get(cid, cid),
        "publish_bytes": int(os.environ.get("MEASURE_SIZE", 0)),
        "ready_ms": {
            "median": round(statistics.median(ready), 1) if ready else None,
            "min": round(min(ready), 1) if ready else None,
            "max": round(max(ready), 1) if ready else None,
            "samples": ready,
        },
        "exit_ms": {
            "median": round(statistics.median(exits), 1) if exits else None,
            "min": round(min(exits), 1) if exits else None,
            "max": round(max(exits), 1) if exits else None,
            "samples": exits,
        },
        "peak_rss_kb": {
            "median": round(statistics.median(rss), 1) if rss else None,
            "min": round(min(rss), 1) if rss else None,
            "max": round(max(rss), 1) if rss else None,
            "samples": rss,
        },
    }
    path.write_text(json.dumps(data, indent=2))

    lines = [
        "| Config | Ready, median (min-max) | Process exit, median | Peak RSS, median | Publish size |",
        "|---|---|---|---|---|",
    ]
    for key in ORDER:
        c = data["configs"].get(key)
        if not c:
            continue
        r, e, m = c["ready_ms"], c["exit_ms"], c["peak_rss_kb"]
        lines.append(
            f"| {c['label']} "
            f"| {r['median']} ms ({r['min']}-{r['max']}) "
            f"| {e['median']} ms "
            f"| {m['median'] / 1024:.1f} MB "
            f"| {c['publish_bytes'] / 1_048_576:.1f} MB |"
        )
    lines.append("")
    lines.append(
        "`Ready` is spawn to the host's `phase=ready` marker: everything blocking is done and the "
        "transport is serving. It includes two Postgres round trips (schema DDL, then the first "
        "`LISTEN`) against a loopback database -- that share is real startup cost for this host, but "
        "it is loopback latency, not production latency."
    )
    (out / "startup-metrics.md").write_text("\n".join(lines) + "\n")

    r = data["configs"][cid]["ready_ms"]
    print(f"    ready median {r['median']} ms ({r['min']}-{r['max']})")


if __name__ == "__main__":
    main()
