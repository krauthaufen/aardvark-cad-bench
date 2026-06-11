# aardvark-cad-bench

Change-density benchmark for the Aardvark.Portable / wombat (WebGPU) stack —
the measurement behind the thesis *"frequency tracked, not declared: cost
proportional to what actually changed."*

## What it measures

A synthetic CAD-like assembly of **N parts**, each its own scene node with an
adaptive per-part transform (`cval<Trafo3d>`). The input is deliberately
**naive**: no instancing hints, no merging, no baking — one node per part, the
way a domain user writes it.

The sweep edits **k random parts per frame** (one transaction) for
log-spaced k ∈ {1, 3, 10, …, N} and records, per frame:

- `frameMs` — rAF frame-to-frame delta (includes render)
- `editMs`  — wall time of the transaction (adaptive propagation)

The **k = N** step is the *"everything, all the time"* reference: the cost a
two-bucket engine pays at any density for content it classified as dynamic.

Assembly statistics modeled on the NVIDIA nvpro-samples **GeForce board**
cadscene (`assets/geforce.csf`, fetched from NVIDIA's server): 110 unique
geometries → 5 004 instances → 68 452 drawable parts, ~2.6× geometric
instancing, max 800 instances of one geometry. `tools/csf_stats.py` extracts
these numbers; a CSF→scene loader (real-model mode) is the planned next step.

## Run

```bash
dotnet tool restore && npm install
npm run dev          # vite on :5173
# browser: http://localhost:5173/?n=5004&frames=60&warmup=20
# headless: node driver/run.cjs "http://localhost:5173/?n=5004" results/n5004
python3 tools/aggregate.py results/*.csv
```

URL params: `n` (parts), `frames` (measured frames per step), `warmup`.
Results land in `window.__benchCsv` / `__benchMeta` (`__benchDone` flags
completion); the driver saves CSV + meta + screenshot.

## KNOWN ISSUE (blocks per-part placement on current published packages)

With `Aardvark.Portable 0.1.0-prerelease0003` + npm `wombat.dom 0.17.1` /
`wombat.rendering 0.19.17`, **per-part `Sg.Trafo` translations do not reach
the rendered output** — all parts render at the origin (verified: the cvals
in the scene tree carry correct matrices, `m03 = 12` etc.; a transpose
workaround does NOT fix it, so it is not a majorness mismatch — the model
trafo appears dropped on this version combination, suspected on the
heap/batched path). Nested `Sg.Trafo`/`Sg.Translate` scopes additionally
override instead of composing. `Sg.Scale(float)` calls a missing
`Trafo3d.scalingUniform`.

Consequences for current numbers: `editMs` (transaction/propagation cost)
is meaningful — it scales linearly with k as the thesis predicts — but
render-side per-part update cost cannot be attributed until the package
versions re-align (the in-flight portable/wombat modernization). Re-run the
sweep then.

## Status / caveats (v1)

- Frame time via rAF deltas — no GPU timestamps yet (WebGPU
  `timestamp-query` is the obvious upgrade).
- `editMs` covers the transaction (marking); render-side incremental cost is
  inside `frameMs`. Finer attribution (nodes marked, command bytes patched)
  needs engine instrumentation — planned.
- Single-engine flat-line baseline (k = N). Cross-engine naive baselines
  (e.g. three.js, one mesh per part) are future work.
- Built on the published `Aardvark.Portable 0.1.0-prerelease0003` packages;
  `Sg.Scale(float)` is avoided (missing `Trafo3d.scalingUniform` in that
  prerelease).
