#!/usr/bin/env python3
"""Convert an nvpro-samples cadscene (.csf, v6) into web-loadable buffers:
  assets/<name>/positions.bin  f32 x,y,z per vertex (all geometries concatenated)
  assets/<name>/normals.bin    f32 x,y,z per vertex (channel 0)
  assets/<name>/indices.bin    u32 (solid triangles, all geometries concatenated)
  assets/<name>/manifest.json  geometry slices, per-node {geo, color, tm(row-major)}, bbox

CSF matrices are column-major (GL); we emit ROW-major (matching wombat M44d.fromArray).
usage: csf_convert.py assets/geforce.csf assets/geforce [--parts]

--parts splits every node into its geometry's PARTS (consecutive solid-index
sub-ranges with their own material): one manifest geometry entry per
(geometry, part) and one node entry per (node, part). Each part gets a
COMPACT vertex slice (used vertices gathered, indices remapped) so that
on load every unique object owns exactly its own buffers — truly naive
input, no cross-object sharing. (Sharing the parent geometry's full
vertex range per part would also replicate it per part on the GPU and
overflow the attribute arena chunk.)
"""
import struct, sys, json, os
import numpy as np

def i32(b, o): return struct.unpack_from("<i", b, o)[0]
def u64(b, o): return struct.unpack_from("<Q", b, o)[0]
def f32s(b, o, n): return struct.unpack_from("<%df" % n, b, o)
def u32s(b, o, n): return struct.unpack_from("<%dI" % n, b, o)

GEOM_SIZE, NODE_SIZE, NODEPART_SIZE, MAT_SIZE = 128, 160, 12, 168

def main(csf_path, out_dir, split_parts=False):
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
    geoms, geom_data = [], []
    for g in range(nGeo):
        o = geomsOff + g * GEOM_SIZE
        numNormCh = i32(b, o + 16)
        numParts, numVerts, numIdxSolid = i32(b, o + 64), i32(b, o + 68), i32(b, o + 72)
        vOff, nOff = u64(b, o + 80), u64(b, o + 88)
        iOff = u64(b, o + 104)
        v0, i0 = len(pos) // 12, len(idx) // 4
        vbytes = b[vOff : vOff + numVerts * 12]
        nbytes = (b[nOff : nOff + numVerts * 12] if numNormCh >= 1 and nOff
                  else b"\x00" * (numVerts * 12))
        ibytes = b[iOff : iOff + numIdxSolid * 4]
        if split_parts:
            # keep raw arrays; compact per-part slices are emitted on
            # demand during the node walk
            geom_data.append((np.frombuffer(vbytes, np.float32).reshape(-1, 3),
                              np.frombuffer(nbytes, np.float32).reshape(-1, 3),
                              np.frombuffer(ibytes, np.uint32)))
        else:
            pos += vbytes; nrm += nbytes; idx += ibytes
        # per-part solid-index sub-ranges (CSFGeometryPart, 12B: numVertices,
        # numIndexSolid, numIndexWire; parts are consecutive in the buffer)
        partsOff = u64(b, o + 120)
        part_ranges, acc = [], 0
        for p in range(numParts):
            pic = i32(b, partsOff + p * 12 + 4)
            part_ranges.append((acc, pic)); acc += pic
        geoms.append(dict(v0=v0, vn=numVerts, i0=i0, ic=numIdxSolid,
                          parts=part_ranges))

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
    out_geoms = [] if split_parts else geoms
    part_geom_idx = {}
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
        rm = [round(world[c * 4 + r], 6) for r in range(4) for c in range(4)]
        node_mats = [i32(b, nd["partsOff"] + p * NODEPART_SIZE + 4)
                     for p in range(nd["numParts"])]
        def col(mi):
            c = mat_colors[mi][:3] if 0 <= mi < nMat else [0.7, 0.7, 0.7]
            # some assets ship uninitialized material blocks (NaN/garbage)
            if not all(0.0 <= x <= 1.0 for x in c): c = [0.7, 0.7, 0.7]
            return [round(x, 4) for x in c]
        if split_parts:
            # one leaf per (node, part); geometry entry per (geom, part),
            # created on demand (vertex slice repeats; the app dedupes)
            for p, mi in enumerate(node_mats):
                key = (gi, p)
                if key not in part_geom_idx:
                    g = geoms[gi]
                    pi0, pic = g["parts"][p]
                    if pic == 0: part_geom_idx[key] = -1
                    else:
                        # compact slice: gather used vertices, remap indices
                        gv, gn, gidx = geom_data[gi]
                        pidx = gidx[pi0 : pi0 + pic]
                        used, remapped = np.unique(pidx, return_inverse=True)
                        v0p, i0p = len(pos) // 12, len(idx) // 4
                        pos += gv[used].tobytes()
                        nrm += gn[used].tobytes()
                        idx += remapped.astype(np.uint32).tobytes()
                        part_geom_idx[key] = len(out_geoms)
                        out_geoms.append(dict(v0=v0p, vn=len(used),
                                              i0=i0p, ic=pic))
                pg = part_geom_idx[key]
                if pg >= 0:
                    nodes.append(dict(g=pg, c=col(mi), tm=rm))
        else:
            counts = {}
            for mi in node_mats: counts[mi] = counts.get(mi, 0) + 1
            mi = max(counts, key=counts.get) if counts else 0
            nodes.append(dict(g=gi, c=col(mi), tm=rm))
        for d, w in enumerate((world[12], world[13], world[14])):
            smin[d] = min(smin[d], w); smax[d] = max(smax[d], w)

    center = [(smin[d] + smax[d]) / 2 for d in range(3)]
    radius = max(smax[d] - smin[d] for d in range(3)) / 2

    os.makedirs(out_dir, exist_ok=True)
    open(os.path.join(out_dir, "positions.bin"), "wb").write(pos)
    open(os.path.join(out_dir, "normals.bin"), "wb").write(nrm)
    open(os.path.join(out_dir, "indices.bin"), "wb").write(idx)
    manifest = dict(
        geoms=[{k: g[k] for k in ("v0", "vn", "i0", "ic")} for g in out_geoms],
        nodes=nodes, center=[round(c, 4) for c in center], radius=round(radius, 4))
    open(os.path.join(out_dir, "manifest.json"), "w").write(json.dumps(manifest))
    tris = len(idx) // 12
    print(f"geoms={len(out_geoms)} drawNodes={len(nodes)} uniqueVerts={len(pos)//12} "
          f"uniqueTris={tris} center={center} radius={radius:.1f}")
    print(f"sizes: pos={len(pos)//1024}K nrm={len(nrm)//1024}K idx={len(idx)//1024}K "
          f"manifest={os.path.getsize(os.path.join(out_dir,'manifest.json'))//1024}K")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], split_parts="--parts" in sys.argv[3:])
