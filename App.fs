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
// Assembly statistics are modeled loosely on the NVIDIA cadscene
// GeForce board (110 unique geometries → 5004 instances → 68k parts):
// few archetypes, heavy repetition, per-part editability.
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

let private nParts        = paramInt "n" 5004          // homage to the GeForce board node count
let private framesPerStep = paramInt "frames" 60
let private warmupFrames  = paramInt "warmup" 20

/// Log-spaced sweep: 1, 3, 10, 32, … up to and including N.
let private sweep =
    let steps =
        [ 0 .. 12 ]
        |> List.map (fun i -> System.Math.Pow(10.0, float i / 2.0) |> round |> int)
        |> List.filter (fun k -> k >= 1 && k < nParts)
        |> List.distinct
    steps @ [ nParts ]

// ─── Assembly ─────────────────────────────────────────────────────────

// Per-part adaptive transform + a base angle (edits rotate the part
// in place). Placement is baked into the SAME trafo (rotation THEN
// translation, Aardvark's flipped `*`): nested Sg.Trafo scopes
// OVERRIDE rather than compose in prerelease0003 (see README).
let private side = System.Math.Ceiling(System.Math.Sqrt(float nParts)) |> int
let private partPos : V3d[] =
    Array.init nParts (fun i ->
        V3d.create (float (i % side) * 1.2) (float (i / side) * 1.2) 0.0)
let private partTrafos : cval<Trafo3d>[] =
    Array.init nParts (fun i -> cval (Trafo3d.translation partPos.[i]))
let private partAngles : float[] = Array.zeroCreate nParts
// (debug exports filled in `scene` below)

/// One edit = rotate part i in place. Cost shape matches a CAD nudge.
let private editPart (i: int) =
    partAngles.[i] <- partAngles.[i] + 0.05
    partTrafos.[i].Value <-
        Trafo3d.rotation (V3d.create 0.0 0.0 1.0) partAngles.[i]
        * Trafo3d.translation partPos.[i]

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
        return { Color = V4f (0.62f * lambert, 0.68f * lambert, 0.78f * lambert, 1.0f) }
    }

let private benchEffect : Effect =
    Effect.compose [ Effect.ofFunction benchVertex
                     Effect.ofFunction benchFragment ]

let private scene : ISceneNode =
    // Few archetypes, heavy repetition — the GeForce-board shape. One
    // shared geometry; per-part placement via scene-graph composition
    // (translate ∘ scale ∘ adaptive-rotation), no instancing hints.
    // NOTE: per-part leaf; ONE Sg.Trafo per part carrying placement +
    // rotation (nested trafo scopes override in prerelease0003).
    let parts =
        List.init nParts (fun i ->
            sg {
                Sg.Trafo (partTrafos.[i] :> aval<_>)
                Sg.Adapter (Primitives.box ())
            })
    let root =
        sg {
            Sg.Effect benchEffect
            parts
        }
    let dbgIdx = min 10 (nParts - 1)
    setDbg {| pos10 = box partPos.[dbgIdx]; trafo10 = box partTrafos.[dbgIdx].Value
              part10 = box (List.item dbgIdx parts); root = box root |}
    root

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
    setMeta (sprintf "{\"nParts\":%d,\"framesPerStep\":%d,\"sweep\":[%s],\"startupMs\":%.1f}"
                nParts framesPerStep
                (sweep |> List.map string |> String.concat ",") (nowMs () - t0))

    let cam =
        OrbitController.attach
            { OrbitController.defaults with
                InitialCenter   = V3d.create (System.Math.Sqrt(float nParts) * 0.6) (System.Math.Sqrt(float nParts) * 0.6) 0.0
                InitialDistance = System.Math.Sqrt(float nParts) * 1.8 }
            ctx.Window.Size

    div {
        Dom.Style "width:100%; height:100vh; touch-action:none; user-select:none; position:relative; background:#101216"
        renderControl {
            Dom.Style "width:100%; height:100%"
            cam.Attributes
            cam.Camera
            cam.Frustum
            scene
        }
        div {
            Dom.Style "position:absolute; top:1rem; left:1rem; color:#eee; font-family:monospace; background:rgba(0,0,0,0.5); padding:0.6rem 0.9rem; border-radius:0.4rem"
            h3 { Dom.Style "margin:0 0 0.4rem 0"; sprintf "cad-bench  n=%d" nParts }
            p  { Dom.Style "margin:0"; statusText :> aval<string> }
        }
    }

[<EntryPoint>]
let main _ =
    Shell.runApp app |> ignore
    raf tick
    0
