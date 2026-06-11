#!/usr/bin/env python3
"""Convert an nvpro-samples cadscene (.csf, v6) into web-loadable buffers:
  assets/<name>/positions.bin  f32 x,y,z per vertex (all geometries concatenated)
  assets/<name>/normals.bin    f32 x,y,z per vertex (channel 0)
  assets/<name>/indices.bin    u32 (solid triangles, all geometries concatenated)
  assets/<name>/manifest.json  geometry slices, per-node {geo, color, tm(row-major)}, bbox

CSF matrices are column-major (GL); we emit ROW-major (matching wombat M44d.fromArray).
usage: csf_convert.py assets/geforce.csf assets/geforce
"""
import struct, sys, json, os

def i32(b, o): return struct.unpack_from("<i", b, o)[0]
def u64(b, o): return struct.unpack_from("<Q", b, o)[0]
def f32s(b, o, n): return struct.unpack_from("<%df" % n, b, o)
def u32s(b, o, n): return struct.unpack_from("<%dI" % n, b, o)

GEOM_SIZE, NODE_SIZE, NODEPART_SIZE, MAT_SIZE = 128, 160, 12, 168

def main(csf_path, out_dir):
    b = open(csf_path, "rb").read()
    assert i32(b, 0) == 0x5d6a86f3 and i32(b, 4) >= 2
    nGeo, nMat, nNode = i32(b, 16), i32(b, 20), i32(b, 24)
    geomsOff, matsOff, nodesOff = u64(b, 40), u64(b, 48), u64(b, 56)

    # materials: color RGBA at +132 (name[128], flags i32)
    mat_colors = []
    for m in range(nMat):
        o = matsOff + m * MAT_SIZE
        mat_colors.append(list(f32s(b, o + 132, 4)))

    pos, nrm, idx = bytearray(), bytearray(), bytearray()
    geoms = []
    for g in range(nGeo):
        o = geomsOff + g * GEOM_SIZE
        numNormCh = i32(b, o + 16)
        numParts, numVerts, numIdxSolid = i32(b, o + 64), i32(b, o + 68), i32(b, o + 72)
        vOff, nOff = u64(b, o + 80), u64(b, o + 88)
        iOff = u64(b, o + 104)
        v0, i0 = len(pos) // 12, len(idx) // 4
        pos += b[vOff : vOff + numVerts * 12]
        if numNormCh >= 1 and nOff:
            nrm += b[nOff : nOff + numVerts * 12]          # channel 0
        else:
            nrm += b"\x00" * (numVerts * 12)
        idx += b[iOff : iOff + numIdxSolid * 4]
        # geometry-local bbox (for scene bounds)
        xs = f32s(b, vOff, numVerts * 3) if numVerts else (0.0,)
        mn = [min(xs[0::3] or [0]), min(xs[1::3] or [0]), min(xs[2::3] or [0])]
        mx = [max(xs[0::3] or [0]), max(xs[1::3] or [0]), max(xs[2::3] or [0])]
        geoms.append(dict(v0=v0, vn=numVerts, i0=i0, ic=numIdxSolid, bb=[mn, mx]))

    # The file's worldTM block is uninitialized garbage in this asset (the
    # nvpro loader recomputes world = parent.world · objectTM); walk the
    # hierarchy from rootIDX composing OBJECT transforms (column-major).
    rootIDX = i32(b, 28)
    def node_raw(n):
        o = nodesOff + n * NODE_SIZE
        return dict(
            obj=f32s(b, o, 16),
            gi=i32(b, o + 128), numParts=i32(b, o + 132),
            numChildren=i32(b, o + 136),
            partsOff=u64(b, o + 144), childrenOff=u64(b, o + 152))

    IDENT = (1.0,0,0,0, 0,1.0,0,0, 0,0,1.0,0, 0,0,0,1.0)
    def matmul_cm(P, L):                       # column-major W = P · L
        return tuple(
            sum(P[k*4+r] * L[c*4+k] for k in range(4))
            for c in range(4) for r in range(4))

    nodes = []
    inf = float("inf")
    smin, smax = [inf]*3, [-inf]*3
    stack = [(rootIDX if rootIDX >= 0 else 0, IDENT)] if rootIDX >= 0 else             [(n, IDENT) for n in range(nNode)]
    visited = 0
    while stack:
        n, parentW = stack.pop()
        nd = node_raw(n)
        world = matmul_cm(parentW, nd["obj"])
        visited += 1
        for ci in range(nd["numChildren"]):
            child = i32(b, nd["childrenOff"] + ci * 4)
            stack.append((child, world))
        gi = nd["gi"]
        if gi < 0: continue
        counts = {}
        for p in range(nd["numParts"]):
            mi = i32(b, nd["partsOff"] + p * NODEPART_SIZE + 4)
            counts[mi] = counts.get(mi, 0) + 1
        mi = max(counts, key=counts.get) if counts else 0
        col = mat_colors[mi][:3] if 0 <= mi < nMat else [0.7, 0.7, 0.7]
        rm = [world[c * 4 + r] for r in range(4) for c in range(4)]
        nodes.append(dict(g=gi, c=[round(x, 4) for x in col],
                          tm=[round(x, 6) for x in rm]))
        for d, w in enumerate((world[12], world[13], world[14])):
            smin[d] = min(smin[d], w); smax[d] = max(smax[d], w)

    center = [(smin[d] + smax[d]) / 2 for d in range(3)]
    radius = max(smax[d] - smin[d] for d in range(3)) / 2

    os.makedirs(out_dir, exist_ok=True)
    open(os.path.join(out_dir, "positions.bin"), "wb").write(pos)
    open(os.path.join(out_dir, "normals.bin"), "wb").write(nrm)
    open(os.path.join(out_dir, "indices.bin"), "wb").write(idx)
    manifest = dict(
        geoms=[{k: g[k] for k in ("v0", "vn", "i0", "ic")} for g in geoms],
        nodes=nodes, center=[round(c, 4) for c in center], radius=round(radius, 4))
    open(os.path.join(out_dir, "manifest.json"), "w").write(json.dumps(manifest))
    tris = len(idx) // 12
    print(f"geoms={nGeo} drawNodes={len(nodes)} uniqueVerts={len(pos)//12} "
          f"uniqueTris={tris} center={center} radius={radius:.1f}")
    print(f"sizes: pos={len(pos)//1024}K nrm={len(nrm)//1024}K idx={len(idx)//1024}K "
          f"manifest={os.path.getsize(os.path.join(out_dir,'manifest.json'))//1024}K")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
