module Bench.App

open Fable.Core
open FSharp.Data.Adaptive
open Aardvark.Portable
open Aardvark.Portable.Render
open Aardvark.Portable.Shader
open Aardvark.Portable.Dom
open Aardvark.Portable.Host
open Aardvark.Portable.Shell.Web

// ─────────────────────────────────────────────────────────────────────
// Change-density benchmark.
//
// A synthetic CAD-like assembly: N parts, each its own scene node with
// an adaptive per-part transform (cval<Trafo3d>) — deliberately NAIVE
// input (no manual instancing hints, no merging, no baking). The sweep
// edits k random parts per frame (one transaction) for a series of k
// values and records frame time + transaction time. The k = N step is
// the "everything, all the time" reference line a two-bucket engine
// pays at any density for dynamic content.
//
// Output: window.__benchCsv (one row per frame) + __benchDone flag;
// the driver script collects them.
//
// Two modes:
//   ?n=5004           synthetic grid of boxes (default)
//   ?model=geforce    the real NVIDIA cadscene GeForce board (110
//                     unique geometries → 2497 drawable nodes, 248k
//                     triangles), converted by tools/csf_convert.py.
// ─────────────────────────────────────────────────────────────────────

[<Emit("performance.now()")>]
let private nowMs () : float = jsNative

[<Emit("new URLSearchParams(window.location.search).get($0)")>]
let private queryParam (_name: string) : string = jsNative

[<Emit("window.__benchCsv = $0")>]
let private setCsv (_s: string) : unit = jsNative

[<Emit("window.__benchDone = true")>]
let private setDone () : unit = jsNative

[<Emit("window.__benchMeta = $0")>]
let private setMeta (_s: string) : unit = jsNative

[<Emit("window.__dbg = $0")>]
let private setDbg (_o: obj) : unit = jsNative

let private paramInt (name: string) (dflt: int) =
    match queryParam name with
    | null -> dflt
    | s -> match System.Int32.TryParse s with | true, v -> v | _ -> dflt

// Deterministic PRNG (xorshift32) — reproducible edit patterns.
let mutable private rngState = 0x9E3779B9u
let private rnd (bound: int) : int =
    let mutable x = rngState
    x <- x ^^^ (x <<< 13)
    x <- x ^^^ (x >>> 17)
    x <- x ^^^ (x <<< 5)
    rngState <- x
    int (x % uint32 bound)

// ─── Parameters ───────────────────────────────────────────────────────

let private model         = queryParam "model"          // "geforce" → real CSF assembly
let private framesPerStep = paramInt "frames" 60
let private warmupFrames  = paramInt "warmup" 20

// nParts is fixed by the model in geforce mode; assigned by init below.
let mutable private nParts = paramInt "n" 5004

/// Log-spaced sweep: 1, 3, 10, 32, … up to and including N.
let private mkSweep (n: int) =
    let steps =
        [ 0 .. 12 ]
        |> List.map (fun i -> System.Math.Pow(10.0, float i / 2.0) |> round |> int)
        |> List.filter (fun k -> k >= 1 && k < n)
        |> List.distinct
    steps @ [ n ]
let mutable private sweep : int list = []

// ─── Assembly (mode-independent core) ─────────────────────────────────

// Per-part adaptive transform = (edit rotation) ∘ (base placement),
// composed into ONE trafo (nested Sg.Trafo scopes compose in reverse
// order in prerelease0003 — see README). Both modes fill baseTrafos;
// edits are identical: rotate the part in place about its own origin.
let mutable private baseTrafos : Trafo3d[] = [||]
let mutable private partTrafos : cval<Trafo3d>[] = [||]
let mutable private partAngles : float[] = [||]

let private initTrafos (bases: Trafo3d[]) =
    nParts <- bases.Length
    sweep <- mkSweep nParts
    baseTrafos <- bases
    partTrafos <- bases |> Array.map cval
    partAngles <- Array.zeroCreate nParts

/// One edit = rotate part i in place. Cost shape matches a CAD nudge.
let private editPart (i: int) =
    partAngles.[i] <- partAngles.[i] + 0.05
    partTrafos.[i].Value <-
        Trafo3d.rotation (V3d.create 0.0 0.0 1.0) partAngles.[i]
        * baseTrafos.[i]

// ─── Shader: ModelViewProjTrafo + lambert ─────────────────────────────
// The published DefaultSurfaces.trafo (prerelease0003) is CAMERA-ONLY
// (reads ViewProjTrafo, no model term) — per-part Sg.Trafo placement
// requires a shader consuming ModelViewProjTrafo (which the runtime
// composes incl. the model chain).

type VertexInput =
    { [<Position>]            Positions : V4f
      [<Semantic("Normals")>] Normal    : V3f }

type VertexOutput =
    { [<Position>]            ClipPos : V4f
      [<Semantic("Normals")>] Normal  : V3f }

[<ShaderEffect>]
let private benchVertex (v: VertexInput) =
    vertex {
        return { ClipPos = (uniform?ModelViewProjTrafo : M44f) * v.Positions
                 Normal  = Vec.normalize v.Normal }
    }

type FragmentInput  = { [<Semantic("Normals")>] Normal : V3f }
type FragmentOutput = { [<Color>] Color : V4f }

[<ShaderEffect>]
let private benchFragment (f: FragmentInput) =
    fragment {
        let n        = Vec.normalize f.Normal
        let lightDir = Vec.normalize (V3f (0.5f, 1.0f, 0.4f))
        let lambert  = max 0.2f (Vec.dot n lightDir)
        let tint : V4f = uniform?Tint
        return { Color = V4f (tint.X * lambert, tint.Y * lambert, tint.Z * lambert, 1.0f) }
    }

let private benchEffect : Effect =
    Effect.compose [ Effect.ofFunction benchVertex
                     Effect.ofFunction benchFragment ]

// Scene + camera, assigned by the mode init before runApp.
let mutable private sceneRoot : ISceneNode = Unchecked.defaultof<_>
let mutable private camCenter = V3d.create 0.0 0.0 0.0
let mutable private camDistance = 10.0

// ─── Synthetic mode: grid of boxes ────────────────────────────────────

let private initSynthetic () =
    let n = nParts
    let side = System.Math.Ceiling(System.Math.Sqrt(float n)) |> int
    let partPos =
        Array.init n (fun i ->
            V3d.create (float (i % side) * 1.2) (float (i / side) * 1.2) 0.0)
    initTrafos (partPos |> Array.map Trafo3d.translation)
    let parts =
        List.init n (fun i ->
            sg {
                Sg.Trafo (partTrafos.[i] :> aval<_>)
                Sg.Adapter (Primitives.box ())
            })
    sceneRoot <-
        sg {
            Sg.Effect benchEffect
            Sg.Uniform ("Tint", box (V4f (0.62f, 0.68f, 0.78f, 1.0f)))
            parts
        }
    let c = System.Math.Sqrt(float n) * 0.6
    camCenter <- V3d.create c c 0.0
    camDistance <- System.Math.Sqrt(float n) * 1.8

// ─── GeForce mode: real CSF assembly (converted by tools/csf_convert) ─

[<Emit("Promise.all(['manifest.json','positions.bin','normals.bin','indices.bin'].map((f,i) => fetch($0+f).then(r => i===0 ? r.json() : r.arrayBuffer())))")>]
let private fetchModel (_baseUrl: string) : obj = jsNative

[<Emit("$0.then($1)")>]
let private thenDo (_p: obj) (_f: obj -> unit) : unit = jsNative

// copy `count` f32s from src ArrayBuffer at byteOff into dst ArrayBuffer
[<Emit("new Float32Array($0).set(new Float32Array($1, $2, $3))")>]
let private fillF32 (_dst: obj) (_src: obj) (_byteOff: int) (_count: int) : unit = jsNative

// fresh-buffer u32 copy (a view would alias the whole shared buffer)
[<Emit("new Uint32Array($0, $1, $2).slice()")>]
let private sliceU32 (_src: obj) (_byteOff: int) (_count: int) : uint32[] = jsNative

// Dynamic-access helpers live in a nested module: JsInterop's `?`
// operator must NOT leak into shader-DSL scope (it shadows uniform?X).
module private Csf =
    open Fable.Core.JsInterop

    [<Import("M44d", "@aardworx/wombat.base")>]
    let M44dJs : obj = jsNative
    [<Import("Trafo3d", "@aardworx/wombat.base")>]
    let Trafo3dJs : obj = jsNative
    // row-major float16 → Trafo3d (fromArray/fromMatrix exist in JS only)
    let trafoOfRowMajor (a: float[]) : Trafo3d =
        unbox (Trafo3dJs?fromMatrix(M44dJs?fromArray(a)))

    type Geom = { v0: int; vn: int; i0: int; ic: int }
    type Node = { g: int; c: float[]; tm: float[] }
    let geoms  (m: obj) : Geom[]  = unbox m?geoms
    let nodes  (m: obj) : Node[]  = unbox m?nodes
    let center (m: obj) : float[] = unbox m?center
    let radius (m: obj) : float   = unbox m?radius

let private initGeforce (manifest: obj) (pos: obj) (nrm: obj) (idx: obj) =
    // 110 shared IndexedGeometry slices out of the concatenated buffers
    let geometries =
        Csf.geoms manifest |> Array.map (fun g ->
            let p = V3fArray g.vn
            fillF32 p.Buffer pos (g.v0 * 12) (g.vn * 3)
            let n = V3fArray g.vn
            fillF32 n.Buffer nrm (g.v0 * 12) (g.vn * 3)
            { IndexedGeometry.empty with
                Positions = p
                Normals   = Some n
                Indices   = Some (sliceU32 idx (g.i0 * 4) g.ic) })
    let nodes = Csf.nodes manifest
    initTrafos (nodes |> Array.map (fun n -> Csf.trafoOfRowMajor n.tm))
    // one leaf per drawable node — naive input, no instancing hints
    let parts =
        nodes
        |> Array.mapi (fun i n ->
            sg {
                Sg.Trafo (partTrafos.[i] :> aval<_>)
                Sg.Uniform ("Tint", box (V4f (float32 n.c.[0], float32 n.c.[1], float32 n.c.[2], 1.0f)))
                Sg.Adapter geometries.[n.g]
            })
        |> Array.toList
    sceneRoot <-
        sg {
            Sg.Effect benchEffect
            parts
        }
    let ctr = Csf.center manifest
    camCenter <- V3d.create ctr.[0] ctr.[1] ctr.[2]
    camDistance <- Csf.radius manifest * 3.0

// ─── Sweep driver (runs on the render-feedback loop) ─────────────────

type private Phase =
    | Warmup of remaining: int
    | Measure of remaining: int

let private csv = System.Text.StringBuilder()
let private statusText = cval "starting"
let mutable private stepIdx = -1
let mutable private phase = Warmup 0
let mutable private lastFrame = 0.0
let mutable private finished = false
let private t0 = nowMs ()

let private currentK () = if stepIdx >= 0 && stepIdx < sweep.Length then sweep.[stepIdx] else 0

let private advanceStep () =
    stepIdx <- stepIdx + 1
    if stepIdx >= sweep.Length then
        finished <- true
        setCsv (csv.ToString())
        setDone ()
        transact (fun () -> statusText.Value <- "DONE")
    else
        phase <- Warmup warmupFrames
        transact (fun () ->
            statusText.Value <- sprintf "step %d/%d  k=%d" (stepIdx + 1) sweep.Length (currentK ()))

[<Emit("requestAnimationFrame($0)")>]
let private raf (_cb: float -> unit) : unit = jsNative

/// Called once per animation frame (rAF). Performs the next edit batch
/// (which marks the scene and schedules a render) and records timings
/// for measure-phase frames.
let rec private tick (_t: float) =
    if finished then () else
    let now = nowMs ()
    let frameDt = if lastFrame = 0.0 then 0.0 else now - lastFrame
    lastFrame <- now

    if stepIdx < 0 then advanceStep ()
    else
        match phase with
        | Warmup r ->
            if r <= 0 then phase <- Measure framesPerStep
            else phase <- Warmup (r - 1)
        | Measure r ->
            if r <= 0 then advanceStep ()
            else phase <- Measure (r - 1)

    if not finished then
        // one transaction editing k random parts
        let k = currentK ()
        let tEdit0 = nowMs ()
        transact (fun () ->
            for _ in 1 .. k do editPart (rnd nParts))
        let editMs = nowMs () - tEdit0
        match phase with
        | Measure _ when frameDt > 0.0 ->
            csv.AppendLine(sprintf "%d,%d,%.3f,%.3f" nParts (currentK ()) frameDt editMs) |> ignore
        | _ -> ()
        raf tick

// ─── App ──────────────────────────────────────────────────────────────

let app : App = fun ctx ->
    ctx.SetTitle "aardvark-cad-bench"
    setMeta (sprintf "{\"nParts\":%d,\"framesPerStep\":%d,\"model\":\"%s\",\"sweep\":[%s],\"startupMs\":%.1f}"
                nParts framesPerStep
                (if isNull model then "synthetic" else model)
                (sweep |> List.map string |> String.concat ",") (nowMs () - t0))

    let cam =
        OrbitController.attach
            { OrbitController.defaults with
                InitialCenter   = camCenter
                InitialDistance = camDistance
                Near            = camDistance * 0.01
                Far             = camDistance * 100.0 }
            ctx.Window.Size

    div {
        Dom.Style "width:100%; height:100vh; touch-action:none; user-select:none; position:relative; background:#101216"
        renderControl {
            Dom.Style "width:100%; height:100%"
            cam.Attributes
            cam.Camera
            cam.Frustum
            sceneRoot
        }
        div {
            Dom.Style "position:absolute; top:1rem; left:1rem; color:#eee; font-family:monospace; background:rgba(0,0,0,0.5); padding:0.6rem 0.9rem; border-radius:0.4rem"
            h3 { Dom.Style "margin:0 0 0.4rem 0"; sprintf "cad-bench  n=%d" nParts }
            p  { Dom.Style "margin:0"; statusText :> aval<string> }
        }
    }

[<EntryPoint>]
let main _ =
    if model = "geforce" then
        thenDo (fetchModel "/assets/geforce/") (fun loaded ->
            let a = unbox<obj[]> loaded
            initGeforce a.[0] a.[1] a.[2] a.[3]
            Shell.runApp app |> ignore
            raf tick)
    else
        initSynthetic ()
        Shell.runApp app |> ignore
        raf tick
    0
