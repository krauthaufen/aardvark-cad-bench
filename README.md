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

## Findings on the published packages (prerelease0003 + npm wombat 0.17.x)

- **`DefaultSurfaces.trafo` is CAMERA-ONLY** — it reads `ViewProjTrafo`
  with no model term, so per-part `Sg.Trafo` placement silently never
  reaches the screen with the stock shader (the values flow fine; the
  runtime supplies `ModelTrafo`/`ModelViewProjTrafo`). The bench uses a
  custom `[<ShaderEffect>]` vertex shader consuming `ModelViewProjTrafo`.
  (An earlier revision of this README mis-diagnosed this as the engine
  dropping model trafos — it was the shader all along.)
- **Nested trafo scopes compose in REVERSE order** relative to the
  Aardvark.Dom convention on this version combination: an outer
  `Sg.Translate` + inner `Sg.Trafo(rotation)` yields `R·T·p` (positions
  swept along arcs about the origin) instead of `T·R·p` (parts rotating
  in place). The bench sidesteps it with ONE composed trafo per part.
- `Sg.Scale(float)` calls a missing `Trafo3d.scalingUniform` (runtime
  error); avoided.

## First results

`results/summary.csv`; median over 51 frames/step (RTX 5060, Chromium WebGPU):

| n | k=1 | k=100 | k=1000 | k=3162 | k=n ("everything") |
|---|---|---|---|---|---|
| 5 004 — edit ms | 0.1 | 0.9 | 7.9 | 30.0 | 46.6 |
| 5 004 — frame ms | 16.6 | 16.8 | 16.6 | 32.3 | 49.9 |
| 20 000 — edit ms | 0.1 | 1.0 | 10.7 | 37.0 | 207.3 |
| 20 000 — frame ms | 16.7 | 16.8 | 16.5 | 41.8 | 215.6 |

Reading: at 20 000 parts the "everything, all the time" reference costs
215 ms/frame (≈4.6 fps); sparse edits cost 0.1 ms and the frame stays
vsync-bound (60 fps) up to ~1 000 edited parts/frame (~5 % density) —
a ~2 000× span between sparse-edit and full-update cost, widening with n.
The crossover where per-change tracking stops paying sits between 5 % and
15 % change density — the honest line in the central figure.

![n=1000 grid](results/n1000-grid.png)

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
