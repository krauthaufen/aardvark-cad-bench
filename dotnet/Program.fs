module CadBench.Program

// ─────────────────────────────────────────────────────────────────────
// Change-density benchmark on classic Aardvark.Rendering (.NET, Vulkan).
//
// Mirrors the web bench (../App.fs) one level lower: NO window, NO
// Aardium/browser presentation — render objects are built directly
// (no scene graph), compiled to an IRenderTask and explicitly Run()
// into an offscreen FBO every frame. GPU time comes from an ITimeQuery
// passed via RenderToken (its blocking GetResult doubles as the frame
// sync), so frameMs is true end-to-end frame cost, not a vsync-clamped
// rAF delta.
//
// --heap activates the 5.7-prerelease HEAP renderer
// (HeapConfig.Enabled + Heap.ofRenderObjects): the N per-part render
// objects collapse into one bucket per effect, drawn as a single
// indirect multidraw against a shared arena through the auto-rewritten
// shader — the .NET equivalent of the wombat/WebGPU heap path.
//
// Modes:
//   --model synthetic        grid of n boxes (default, --n)
//   --model geforce-parts    converted CSF assets (../assets/<model>/)
//
// Output CSV: nParts,k,frameMs,editMs,gpuMs  (superset of the web
// bench's columns; tools/aggregate.py shows gpuMs when present).
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
open FShade

// ─── Shaders (HeapSpike pattern: per-draw uniforms by name; the heap
//     rewrite redirects exactly the names passed to ofRenderObjects) ──

module Shaders =
    type Vertex =
        { [<Position>] pos : V4f
          [<Color>]    c   : V4f
          [<Normal>]   n   : V3f }

    let shade (v : Vertex) =
        vertex {
            let m   : M44f = uniform?HeapModelTrafo
            let col : V4f  = uniform?HeapColor
            let vp  : M44f = uniform?ViewProjTrafo
            return { v with pos = vp * (m * v.pos); c = col; n = m.TransformDir v.n }
        }

    let shadeFrag (v : Vertex) =
        fragment {
            let l  = Vec.normalize (V3f(1.0f, 2.0f, 3.0f))
            let nn = Vec.normalize v.n
            let d  = 0.25f + 0.75f * max 0.0f (Vec.dot nn l)
            return V4f(v.c.XYZ * d, 1.0f)
        }

// ─── Args ─────────────────────────────────────────────────────────────

type Args =
    { Model   : string option
      N       : int
      Frames  : int
      Warmup  : int
      Size    : int
      Heap    : bool
      Churn   : bool
      Ks      : int list option   // explicit sweep values (--ks 1,3,10)
      Assets  : string
      Out     : string }

let private parseArgs (argv: string[]) =
    let mutable a =
        { Model = None; N = 5004; Frames = 60; Warmup = 20; Size = 1024
          Heap = false; Churn = false; Ks = None
          Assets = Path.Combine(__SOURCE_DIRECTORY__, "..", "assets")
          Out = "results/dotnet.csv" }
    let rec go i =
        if i < argv.Length then
            match argv.[i] with
            | "--heap"   -> a <- { a with Heap = true }
            | "--churn"  -> a <- { a with Churn = true }
            | "--ks" when i + 1 < argv.Length ->
                a <- { a with Ks = Some (argv.[i+1].Split(',') |> Array.map int |> Array.toList) }
            | "--model" when i + 1 < argv.Length  -> a <- { a with Model = Some argv.[i+1] }
            | "--n"      when i + 1 < argv.Length -> a <- { a with N = int argv.[i+1] }
            | "--frames" when i + 1 < argv.Length -> a <- { a with Frames = int argv.[i+1] }
            | "--warmup" when i + 1 < argv.Length -> a <- { a with Warmup = int argv.[i+1] }
            | "--size"   when i + 1 < argv.Length -> a <- { a with Size = int argv.[i+1] }
            | "--assets" when i + 1 < argv.Length -> a <- { a with Assets = argv.[i+1] }
            | "--out"    when i + 1 < argv.Length -> a <- { a with Out = argv.[i+1] }
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

/// One unique object's geometry buffers; instances share these and
/// nothing else (the converter already emits compact per-part slices).
type Geometry =
    { Attrs : IAttributeProvider
      Index : BufferView
      FVC   : int }

type Assembly =
    { Geoms      : Geometry[]
      PartGeom   : int[]
      PartColor  : C4f[]
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

let private mkGeometry (positions: V3f[]) (normals: V3f[]) (index: int[]) =
    let bv (arr: Array) (t: Type) = BufferView(AVal.constant (ArrayBuffer(arr) :> IBuffer), t)
    { Attrs =
        AttributeProvider.ofList [
            DefaultSemantic.Positions, bv positions typeof<V3f>
            DefaultSemantic.Normals,   bv normals   typeof<V3f>
        ]
      Index = bv index typeof<int>
      FVC = index.Length }

let private synthetic (n: int) =
    let side = ceil (sqrt (float n)) |> int
    let bases =
        Array.init n (fun i ->
            Trafo3d.Translation(float (i % side) * 1.2, float (i / side) * 1.2, 0.0))
    let g = (IndexedGeometryPrimitives.Box.solidBox Box3d.Unit C4b.White).ToIndexed()
    let geom =
        mkGeometry
            (g.IndexedAttributes.[DefaultSemantic.Positions] |> unbox<V3f[]>)
            (g.IndexedAttributes.[DefaultSemantic.Normals]   |> unbox<V3f[]>)
            (g.IndexArray |> unbox<int[]>)
    let c = sqrt (float n) * 0.6
    { Geoms = [| geom |]
      PartGeom = Array.zeroCreate n
      PartColor = Array.create n (C4f(0.62, 0.68, 0.78, 1.0))
      Trafos = bases |> Array.map cval
      BaseTrafos = bases; Angles = Array.zeroCreate n
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

    let geometries =
        manifest.geoms |> Array.map (fun g ->
            let pos = Array.zeroCreate<V3f> g.vn
            let nrm = Array.zeroCreate<V3f> g.vn
            let idx = Array.zeroCreate<int> g.ic
            MemoryMarshal.Cast<byte, V3f>(ReadOnlySpan(posBytes, g.v0 * 12, g.vn * 12)).CopyTo(Span pos)
            MemoryMarshal.Cast<byte, V3f>(ReadOnlySpan(nrmBytes, g.v0 * 12, g.vn * 12)).CopyTo(Span nrm)
            Buffer.BlockCopy(idxBytes, g.i0 * 4, idx, 0, g.ic * 4)
            mkGeometry pos nrm idx)

    let bases =
        manifest.nodes |> Array.map (fun nd ->
            let m = M44d(nd.tm)
            Trafo3d(m, m.Inverse))
    { Geoms = geometries
      PartGeom = manifest.nodes |> Array.map (fun nd -> nd.g)
      PartColor = manifest.nodes |> Array.map (fun nd -> C4f(nd.c.[0], nd.c.[1], nd.c.[2], 1.0))
      Trafos = bases |> Array.map cval
      BaseTrafos = bases; Angles = Array.zeroCreate bases.Length
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
    if args.Heap then HeapConfig.Enabled <- true

    let assembly =
        match args.Model with
        | Some m -> loadModel (Path.Combine(args.Assets, m))
        | None -> synthetic args.N
    let n = assembly.N
    let modelName = args.Model |> Option.defaultValue "synthetic"
    Log.line "model=%s n=%d frames=%d warmup=%d size=%d heap=%b"
        modelName n args.Frames args.Warmup args.Size args.Heap

    use app = new Aardvark.Application.Slim.VulkanApplication(false)
    let runtime = app.Runtime :> IRuntime

    // camera matches the web bench's initial orbit position
    let center, dist = assembly.Center, assembly.Radius * 3.0
    let view = CameraView.lookAt (center + V3d(dist, 0.0, dist * 0.3)) center V3d.OOI |> CameraView.viewTrafo
    let proj = Frustum.perspective 60.0 (dist * 0.01) (dist * 100.0) 1.0 |> Frustum.projTrafo
    let viewProjU = AVal.constant ((view * proj).Forward |> M44f.op_Explicit) :> IAdaptiveValue

    let effect =
        Effect.compose [
            Effect.ofFunction Shaders.shade
            Effect.ofFunction Shaders.shadeFrag
        ]

    // one RenderObject per part — naive input, no instancing hints;
    // instances of the same unique object share its geometry buffers
    let mkRO (g: Geometry) (trafo: IAdaptiveValue) (color: C4f) =
        let ro = RenderObject()
        ro.Surface   <- Surface.Effect effect
        ro.Mode      <- IndexedGeometryMode.TriangleList
        ro.VertexAttributes <- g.Attrs
        ro.Indices   <- Some g.Index
        ro.DrawCalls <- DrawCalls.Direct (AVal.constant [| DrawCallInfo(FaceVertexCount = g.FVC, InstanceCount = 1) |])
        ro.Uniforms  <-
            UniformProvider.ofList [
                Symbol.Create "HeapModelTrafo", trafo
                Symbol.Create "HeapColor", (AVal.constant (color.ToV4f()) :> IAdaptiveValue)
                Symbol.Create "ViewProjTrafo", viewProjU
            ]
        ro :> IRenderObject

    let ros =
        Array.init n (fun i ->
            mkRO assembly.Geoms.[assembly.PartGeom.[i]]
                 (assembly.Trafos.[i] |> AVal.map (fun (t: Trafo3d) -> M44f.op_Explicit t.Forward) :> IAdaptiveValue)
                 assembly.PartColor.[i])

    // ── churn mode: per frame remove r + add r (population constant) —
    //    small shared geometry, so this measures the STRUCTURAL paths
    //    (RO prepare/dispose, draw-record add/remove, arena alloc/free)
    let churnSet = if args.Churn then Some (cset ros) else None
    let churnMirror = ResizeArray ros
    let gridSide = ceil (sqrt (float n)) |> int
    let mkFresh () =
        let p = Trafo3d.Translation(float (rnd gridSide) * 1.2, float (rnd gridSide) * 1.2, 0.0)
        mkRO assembly.Geoms.[rnd assembly.Geoms.Length]
             (AVal.constant (M44f.op_Explicit p.Forward) :> IAdaptiveValue)
             (C4f(0.62, 0.68, 0.78, 1.0))
    let doEdits (k: int) =
        match churnSet with
        | Some set ->
            for _ in 1 .. k do
                let i = rnd churnMirror.Count
                let dead = churnMirror.[i]
                churnMirror.[i] <- churnMirror.[churnMirror.Count - 1]
                churnMirror.RemoveAt(churnMirror.Count - 1)
                set.Remove dead |> ignore
                let fresh = mkFresh ()
                churnMirror.Add fresh
                set.Add fresh |> ignore
        | None ->
            for _ in 1 .. k do assembly.EditPart (rnd n)

    let objects =
        let set =
            match churnSet with
            | Some s -> s :> aset<IRenderObject>
            | None -> ASet.ofArray ros
        if args.Heap then
            Heap.ofRenderObjects runtime (Set.ofList [ "HeapModelTrafo"; "HeapColor" ]) set
        else set

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
            runtime.CompileRender(signature, objects)
        ]

    let gpuQuery = runtime.CreateTimeQuery()
    let token = { RenderToken.Empty with Queries = [ gpuQuery ] }

    let renderFrame () =
        task.Run(AdaptiveToken.Top, token, output)
        // blocking query read = frame sync (no device-wait needed)
        let gpu = gpuQuery.GetResult((), reset = true)
        gpu.TotalMilliseconds

    let sw = Stopwatch()
    let csv = Text.StringBuilder()
    csv.AppendLine("nParts,k,frameMs,editMs,gpuMs") |> ignore

    // first frame compiles shaders + uploads everything — report, exclude
    sw.Restart()
    let _ = renderFrame ()
    Log.line "first frame (compile+upload): %.0f ms" sw.Elapsed.TotalMilliseconds
    if args.Heap then Log.line "heap buckets: %d (of %d ROs)" Heap.lastBucketCount n

    let sweepKs =
        match args.Ks with
        | Some ks -> ks
        | None ->
        if args.Churn then
            (mkSweep n |> List.filter (fun k -> k <= n / 10)) @ [ n / 10 ]
            |> List.distinct
        else mkSweep n
    for k in sweepKs do
        for f in 1 .. args.Warmup + args.Frames do
            sw.Restart()
            transact (fun () -> doEdits k)
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
