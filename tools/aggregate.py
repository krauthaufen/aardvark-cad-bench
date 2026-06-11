#!/usr/bin/env python3
"""Aggregate sweep CSVs: per-(nParts,k) median/p90 of frame + edit times.
usage: aggregate.py results/*.csv  — prints a table and writes summary.csv"""
import csv, sys, statistics, collections

rows = collections.defaultdict(list)
for path in sys.argv[1:]:
    with open(path) as f:
        for r in csv.DictReader(f):
            rows[(int(r["nParts"]), int(r["k"]))].append(
                (float(r["frameMs"]), float(r["editMs"])))

print(f"{'n':>7} {'k':>7} {'frames':>6} {'frame med':>10} {'frame p90':>10} {'edit med':>9} {'edit p90':>9}")
out = [("nParts","k","frames","frameMedMs","frameP90Ms","editMedMs","editP90Ms")]
def p90(xs):
    xs = sorted(xs); return xs[min(len(xs)-1, int(0.9*len(xs)))]
for (n, k) in sorted(rows):
    fr = [x[0] for x in rows[(n,k)]]; ed = [x[1] for x in rows[(n,k)]]
    line = (n, k, len(fr), round(statistics.median(fr),3), round(p90(fr),3),
            round(statistics.median(ed),3), round(p90(ed),3))
    out.append(line)
    print(f"{n:>7} {k:>7} {len(fr):>6} {statistics.median(fr):>10.2f} {p90(fr):>10.2f} {statistics.median(ed):>9.2f} {p90(ed):>9.2f}")
with open("results/summary.csv","w") as f:
    w = csv.writer(f); w.writerows(out)
