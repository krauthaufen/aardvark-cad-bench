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

Modes:

- **synthetic** (default, `?n=N`) — a grid of N boxes, arbitrary scale.
- **`?model=geforce`** — the real NVIDIA nvpro-samples **GeForce board**
  cadscene (`assets/geforce.csf`, fetched from NVIDIA's server): 110 unique
  geometries → 2 497 drawable nodes (5 004 hierarchy nodes, ~2.6× geometric
  instancing, max 800 instances of one geometry), 218 604 vertices /
  248 569 triangles.
- **`?model=geforce-parts`** — the same board at **part granularity**:
  every node split into its CSF parts → **68 452 individually-editable
  objects** (22 514 unique part geometries, each with its own compact
  vertex/index buffers).
- **`?model=worldcar`** — the nvpro **worldcar** cadscene: 576 geometries
  → 1 811 drawable nodes, 1.82 M vertices / 2.33 M triangles (3.7 M
  instanced) — geometrically ~9× heavier than the board.

`tools/csf_convert.py` converts a CSF into web-loadable buffers. The
input stays **truly naive**: on load every unique object gets its own
fresh vertex/normal/index buffers (no packing, no cross-object sharing);
instances of the same object share their geometry, nothing else. Every
drawable object gets its own `cval<Trafo3d>` + material color. Two CSF
gotchas the converter handles: the baked `worldTM` block is
uninitialized in these assets (world transforms are recomputed by
walking the hierarchy, as the nvpro loader does), and some material
color blocks are garbage (sanitized to grey).

![GeForce board at part granularity, 68 452 individually-editable objects](results/geforce-parts-board.png)

## Run

```bash
dotnet tool restore && npm install
# model modes: fetch + convert the cadscenes once
for m in geforce worldcar; do
  curl -o assets/$m.csf.gz https://developer.download.nvidia.com/ProGraphics/nvpro-samples/$m.csf.gz
  gunzip assets/$m.csf.gz
done
python3 tools/csf_convert.py assets/geforce.csf assets/geforce
python3 tools/csf_convert.py assets/geforce.csf assets/geforce-parts --parts
python3 tools/csf_convert.py assets/worldcar.csf assets/worldcar
npm run dev          # vite on :5173
# browser: http://localhost:5173/?n=5004&frames=60&warmup=20
#          http://localhost:5173/?model=geforce
# headless: node driver/run.cjs "http://localhost:5173/?model=geforce" results/geforce
python3 tools/aggregate.py results/*.csv
```

URL params: `n` (parts, synthetic), `model=geforce`, `frames` (measured
frames per step), `warmup`.
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
- **The 68 k-object mode found a real engine bug** (fixed in
  `wombat.rendering 0.19.18`, required by this bench): the heap
  allocator's debug overlap-validation scanned every live allocation
  per alloc — O(n²) scene build, 65 s at 20 k objects and a renderer
  crash at 68 k. Now opt-in (`globalThis.__wombatDebugAllocOverlap`);
  the 68 452-object scene builds in ~1 s.

## First results

`results/summary.csv`; median over 51 frames/step (RTX 5060, Chromium WebGPU):

| n | k=1 | k=100 | k=1000 | k=3162 | k=n ("everything") |
|---|---|---|---|---|---|
| 5 004 — edit ms | 0.1 | 0.9 | 7.9 | 30.0 | 46.6 |
| 5 004 — frame ms | 16.6 | 16.8 | 16.6 | 32.3 | 49.9 |
| 20 000 — edit ms | 0.1 | 1.0 | 10.7 | 37.0 | 207.3 |
| 20 000 — frame ms | 16.7 | 16.8 | 16.5 | 41.8 | 215.6 |

Real models (medians over 61 frames):

| model | n | k=1 | k=100 | k=1000 | k=10000 | k=n ("everything") |
|---|---|---|---|---|---|---|
| geforce — edit ms | 2 497 | 0.1 | 0.8 | 6.2 | — | 16.5 |
| geforce — frame ms | 2 497 | 16.7 | 16.8 | 16.7 | — | 18.1 |
| worldcar — edit ms | 1 811 | 0.1 | 0.7 | 6.0 | — | 11.1 |
| worldcar — frame ms | 1 811 | 16.8 | 16.8 | 16.8 | — | 16.9 |
| geforce-parts — edit ms | 68 452 | 0.1 | 0.9 | 7.6 | 80.6 | 554.4 |
| geforce-parts — frame ms | 68 452 | 16.6 | 16.7 | 16.3 | 95.2 | 600.0 |

The headline is **geforce-parts**: a real CAD assembly with 68 452
individually-editable objects stays **vsync-bound (60 fps) while editing
up to ~1 000 parts per frame** (~1.5 % change density), while the
"everything, all the time" reference costs 600 ms/frame (1.7 fps) — a
**6 000× span** between sparse-edit and full-update cost, on real data.
worldcar (2.3 M unique triangles, ~9× the board's geometry) confirms
frame cost is geometry-independent at low k: vsync-bound at every
density, with the same ~6–10 µs/edit propagation cost.

Reading: at 20 000 parts the "everything, all the time" reference costs
215 ms/frame (≈4.6 fps); sparse edits cost 0.1 ms and the frame stays
vsync-bound (60 fps) up to ~1 000 edited parts/frame (~5 % density) —
a ~2 000× span between sparse-edit and full-update cost, widening with n.
The crossover where per-change tracking stops paying sits between 5 % and
15 % change density — the honest line in the central figure.

![n=1000 grid](results/n1000-grid.png)

## .NET bench (classic Aardvark.Rendering, no vsync)

`dotnet/` runs the same sweep one level lower on classic
**Aardvark.Rendering** (.NET, GL backend): no window, no Aardium
presentation (which blits every frame through a memory-mapped file) —
the scene is compiled to an `IRenderTask` and explicitly `Run()` into an
**offscreen FBO** every frame. GPU time comes from an `ITimeQuery`
passed via `RenderToken`; its blocking `GetResult` doubles as the frame
sync, so `frameMs` is true end-to-end frame cost, not a vsync-clamped
rAF delta. Same converted assets, same edit pattern, extra `gpuMs`
CSV column.

```bash
cd dotnet
DISPLAY=:0 dotnet run -c Release -- --model geforce-parts --out ../results/dotnet-geforce-parts.csv
dotnet run -c Release -- --n 20000 --out ../results/dotnet-n20000.csv
```

Results (medians; RTX 5060, GL):

| model | n | metric | k=1 | k=100 | k=1000 | k=10000 | k=n |
|---|---|---|---|---|---|---|---|
| geforce-parts | 68 452 | frame ms | 33.0 | 32.9 | 38.1 | 92.2 | 367 |
| | | edit ms | 0.02 | 0.27 | 2.7 | 34.3 | 213 |
| | | gpu ms | 32.2 | 31.9 | 34.2 | 55.2 | 148 |
| worldcar | 1 811 | frame ms | 1.2 | 1.9 | 4.2 | — | 5.8 |
| synthetic | 20 000 | frame ms | 3.2 | 3.3 | 8.4 | 58.9 | 104 |

What the uncapped numbers add to the story:

- **Sparse edits are even cheaper on .NET**: ~2.7 µs/edit
  (k=1000 → 2.7 ms) vs ~7 µs/edit in the JS port — and a k=1 edit
  is 20 µs end to end.
- **Object count, not triangle count, is the GPU-side cost driver**
  for classic per-draw GL: worldcar's 2.33 M triangles in 1 811 draws
  render in 0.7 ms GPU, while geforce-parts' 0.25 M triangles in
  68 452 draws take 32 ms GPU (~0.5 µs/draw). This is precisely the
  CAD many-small-parts problem the paper targets — and the
  wombat/WebGPU heap renderer (MDI, one indirect dispatch) renders
  the same 68 k objects inside a 16.7 ms vsync budget where classic
  GL needs 33 ms.
- The k=N reference on .NET: 367 ms (213 edit + 148 GPU) vs 600 ms
  in the browser — FSharp.Data.Adaptive propagation is ~2.6× faster
  on .NET than the JS port.
- First frame (compile + upload) at 68 k objects: 23.5 s on classic
  Aardvark's per-RO compile path vs ~1 s scene build in wombat —
  another place where naive-input scale stresses paths tuned for
  smaller object counts.

Timing-comparison caveat: the web bench's `frameMs` is a rAF delta —
WebGPU work is submitted asynchronously and vsync clamps from below, so
browser frame numbers are not directly comparable to the .NET
blocking-query numbers; the .NET column is the honest hardware cost.

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
