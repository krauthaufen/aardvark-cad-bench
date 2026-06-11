module CadBench.Program

// ─────────────────────────────────────────────────────────────────────
// Change-density benchmark on classic Aardvark.Rendering (.NET, GL).
//
// Mirrors the web bench (../App.fs) one level lower: NO window, NO
// Aardium/browser presentation — the scene is compiled to an
// IRenderTask and explicitly Run() into an offscreen FBO every frame.
// GPU time comes from an ITimeQuery passed via RenderToken (its
// blocking GetResult doubles as the frame sync), so frameMs is true
// end-to-end frame cost, not a vsync-clamped rAF delta.
//
// Modes:
//   --model synthetic        grid of n boxes (default, --n)
//   --model geforce-parts    converted CSF assets (../assets/<model>/)
//   (any converted model dir works: geforce, worldcar, geforce-parts)
//
// Output CSV: nParts,k,frameMs,editMs,gpuMs  (superset of the web
// bench's columns; tools/aggregate.py ignores the extra column).
// ─────────────────────────────────────────────────────────────────────

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Runtime.InteropServices
open Aardvark.Base
open Aardvark.Rendering
open FSharp.Data.Adaptive
open Aardvark.SceneGraph
open Aardvark.Application.Slim

// ─── Args ─────────────────────────────────────────────────────────────

type Args =
    { Model   : string option
      N       : int
      Frames  : int
      Warmup  : int
      Size    : int
      Assets  : string
      Out     : string }

let private parseArgs (argv: string[]) =
    let mutable a =
        { Model = None; N = 5004; Frames = 60; Warmup = 20; Size = 1024
          Assets = Path.Combine(__SOURCE_DIRECTORY__, "..", "assets")
          Out = "results/dotnet.csv" }
    let rec go i =
        if i < argv.Length - 1 then
            match argv.[i] with
            | "--model"  -> a <- { a with Model = Some argv.[i+1] }
            | "--n"      -> a <- { a with N = int argv.[i+1] }
            | "--frames" -> a <- { a with Frames = int argv.[i+1] }
            | "--warmup" -> a <- { a with Warmup = int argv.[i+1] }
            | "--size"   -> a <- { a with Size = int argv.[i+1] }
            | "--assets" -> a <- { a with Assets = argv.[i+1] }
            | "--out"    -> a <- { a with Out = argv.[i+1] }
            | _ -> ()
            go (i + 1)
    go 0
    a

// Deterministic PRNG (xorshift32) — same edit pattern as the web bench.
let mutable private rngState = 0x9E3779B9u
let private rnd (bound: int) =
    let mutable x = rngState
    x <- x ^^^ (x <<< 13)
    x <- x ^^^ (x >>> 17)
    x <- x ^^^ (x <<< 5)
    rngState <- x
    int (x % uint32 bound)

// ─── Assembly (same naive structure as the web bench) ─────────────────

/// Per-part adaptive transform = (edit rotation) ∘ (base placement),
/// one composed trafo per part. One edit = rotate the part in place.
type Assembly =
    { Sg         : ISg
      Trafos     : cval<Trafo3d>[]
      BaseTrafos : Trafo3d[]
      Angles     : float[]
      Center     : V3d
      Radius     : float }

    member this.N = this.Trafos.Length

    member this.EditPart (i: int) =
        this.Angles.[i] <- this.Angles.[i] + 0.05
        this.Trafos.[i].Value <-
            Trafo3d.RotationZ this.Angles.[i] * this.BaseTrafos.[i]

let private synthetic (n: int) =
    let side = ceil (sqrt (float n)) |> int
    let bases =
        Array.init n (fun i ->
            Trafo3d.Translation(float (i % side) * 1.2, float (i / side) * 1.2, 0.0))
    let trafos = bases |> Array.map cval
    let box = Sg.box' C4b.White Box3d.Unit
    let parts =
        Array.init n (fun i -> box |> Sg.trafo trafos.[i])
    let c = sqrt (float n) * 0.6
    { Sg = Sg.ofArray parts
      Trafos = trafos; BaseTrafos = bases; Angles = Array.zeroCreate n
      Center = V3d(c, c, 0.0); Radius = sqrt (float n) * 0.6 }

// ─── Converted-CSF model loader (same assets as the web bench) ───────

type private GeomJson = { v0: int; vn: int; i0: int; ic: int }
type private NodeJson = { g: int; c: float[]; tm: float[] }
type private ManifestJson =
    { geoms: GeomJson[]; nodes: NodeJson[]; center: float[]; radius: float }

let private loadModel (dir: string) =
    let manifest =
        use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))
        let r = doc.RootElement
        let floats (e: JsonElement) = [| for x in e.EnumerateArray() -> x.GetDouble() |]
        { geoms =
            [| for g in r.GetProperty("geoms").EnumerateArray() ->
                 { v0 = g.GetProperty("v0").GetInt32(); vn = g.GetProperty("vn").GetInt32()
                   i0 = g.GetProperty("i0").GetInt32(); ic = g.GetProperty("ic").GetInt32() } |]
          nodes =
            [| for n in r.GetProperty("nodes").EnumerateArray() ->
                 { g = n.GetProperty("g").GetInt32()
                   c = floats (n.GetProperty("c")); tm = floats (n.GetProperty("tm")) } |]
          center = floats (r.GetProperty("center"))
          radius = r.GetProperty("radius").GetDouble() }
    let posBytes = File.ReadAllBytes(Path.Combine(dir, "positions.bin"))
    let nrmBytes = File.ReadAllBytes(Path.Combine(dir, "normals.bin"))
    let idxBytes = File.ReadAllBytes(Path.Combine(dir, "indices.bin"))

    // every unique object gets its own arrays (truly naive input);
    // instances of the same object share the resulting IndexedGeometry
    let geometries =
        manifest.geoms |> Array.map (fun g ->
            let pos = Array.zeroCreate<V3f> g.vn
            let nrm = Array.zeroCreate<V3f> g.vn
            let idx = Array.zeroCreate<int> g.ic
            MemoryMarshal.Cast<byte, V3f>(ReadOnlySpan(posBytes, g.v0 * 12, g.vn * 12)).CopyTo(Span pos)
            MemoryMarshal.Cast<byte, V3f>(ReadOnlySpan(nrmBytes, g.v0 * 12, g.vn * 12)).CopyTo(Span nrm)
            Buffer.BlockCopy(idxBytes, g.i0 * 4, idx, 0, g.ic * 4)
            IndexedGeometry(
                Mode = IndexedGeometryMode.TriangleList,
                IndexArray = (idx :> Array),
                IndexedAttributes =
                    SymDict.ofList [
                        DefaultSemantic.Positions, pos :> Array
                        DefaultSemantic.Normals,   nrm :> Array
                    ])
            |> Sg.ofIndexedGeometry)

    let n = manifest.nodes.Length
    let bases =
        manifest.nodes |> Array.map (fun nd ->
            let m = M44d(nd.tm)
            Trafo3d(m, m.Inverse))
    let trafos = bases |> Array.map cval
    let parts =
        manifest.nodes |> Array.mapi (fun i nd ->
            geometries.[nd.g]
            |> Sg.trafo trafos.[i]
            |> Sg.uniform "Color" (AVal.constant (C4f(nd.c.[0], nd.c.[1], nd.c.[2], 1.0))))
    { Sg = Sg.ofArray parts
      Trafos = trafos; BaseTrafos = bases; Angles = Array.zeroCreate n
      Center = V3d manifest.center; Radius = manifest.radius }

// ─── Sweep ────────────────────────────────────────────────────────────

/// Log-spaced sweep: 1, 3, 10, 32, … up to and including N.
let private mkSweep (n: int) =
    [ 0 .. 12 ]
    |> List.map (fun i -> Math.Pow(10.0, float i / 2.0) |> round |> int)
    |> List.filter (fun k -> k >= 1 && k < n)
    |> List.distinct
    |> fun steps -> steps @ [ n ]

[<EntryPoint>]
let main argv =
    Aardvark.Init()
    let args = parseArgs argv

    let assembly =
        match args.Model with
        | Some m -> loadModel (Path.Combine(args.Assets, m))
        | None -> synthetic args.N
    let n = assembly.N
    let modelName = args.Model |> Option.defaultValue "synthetic"
    Log.line "model=%s n=%d frames=%d warmup=%d size=%d" modelName n args.Frames args.Warmup args.Size

    use app = new OpenGlApplication()
    let runtime = app.Runtime :> IRuntime

    // camera matches the web bench's initial orbit position
    let center, dist = assembly.Center, assembly.Radius * 3.0
    let view =
        CameraView.lookAt (center + V3d(dist, 0.0, dist * 0.3)) center V3d.OOI
        |> CameraView.viewTrafo
    let proj =
        Frustum.perspective 60.0 (dist * 0.01) (dist * 100.0) 1.0
        |> Frustum.projTrafo

    let sg =
        assembly.Sg
        |> Sg.effect [
            DefaultSurfaces.trafo          |> toEffect
            DefaultSurfaces.sgColor        |> toEffect
            DefaultSurfaces.simpleLighting |> toEffect
           ]
        |> Sg.viewTrafo' view
        |> Sg.projTrafo' proj
        |> Sg.uniform "Color" (AVal.constant (C4f(0.62, 0.68, 0.78, 1.0)))

    // offscreen FBO — no window, no swapchain, no Aardium blit
    let size = V2i args.Size
    use signature =
        runtime.CreateFramebufferSignature([
            DefaultSemantic.Colors,       TextureFormat.Rgba8
            DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
        ])
    let colorTex = runtime.CreateTexture2D(size, TextureFormat.Rgba8)
    let depthTex = runtime.CreateTexture2D(size, TextureFormat.Depth24Stencil8)
    let fbo =
        runtime.CreateFramebuffer(signature, Map.ofList [
            DefaultSemantic.Colors,       colorTex.GetOutputView()
            DefaultSemantic.DepthStencil, depthTex.GetOutputView()
        ])
    let output = OutputDescription.ofFramebuffer fbo

    use task =
        RenderTask.ofList [
            runtime.CompileClear(signature, clear { color (C4f(0.063, 0.07, 0.086)); depth 1.0 })
            sg |> Sg.compile runtime signature
        ]

    let gpuQuery = runtime.CreateTimeQuery()
    let token = { RenderToken.Empty with Queries = [ gpuQuery ] }

    let renderFrame () =
        task.Run(AdaptiveToken.Top, token, output)
        // blocking query read = frame sync (no glFinish needed)
        let gpu = gpuQuery.GetResult((), reset = true)
        gpu.TotalMilliseconds

    let sw = Stopwatch()
    let csv = Text.StringBuilder()
    csv.AppendLine("nParts,k,frameMs,editMs,gpuMs") |> ignore

    // first frame compiles shaders + uploads everything — report, exclude
    sw.Restart()
    let _ = renderFrame ()
    Log.line "first frame (compile+upload): %.0f ms" sw.Elapsed.TotalMilliseconds

    for k in mkSweep n do
        for f in 1 .. args.Warmup + args.Frames do
            sw.Restart()
            transact (fun () ->
                for _ in 1 .. k do assembly.EditPart (rnd n))
            let editMs = sw.Elapsed.TotalMilliseconds
            sw.Restart()
            let gpuMs = renderFrame ()
            let frameMs = editMs + sw.Elapsed.TotalMilliseconds
            if f > args.Warmup then
                csv.AppendLine(sprintf "%d,%d,%.3f,%.3f,%.3f" n k frameMs editMs gpuMs) |> ignore
        Log.line "k=%d done" k

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath args.Out)) |> ignore
    File.WriteAllText(args.Out, csv.ToString())
    Log.line "wrote %s" args.Out

    // proof-of-render screenshot next to the CSV
    let img = colorTex.Download().AsPixImage<uint8>()
    let png = Path.ChangeExtension(args.Out, ".png")
    img.Save png
    Log.line "wrote %s" png
    0
