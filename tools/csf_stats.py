#!/usr/bin/env python3
"""Stats for NVIDIA nvpro-samples cadscene (.csf) files, v6 layout (64-bit offsets).
Struct layout transcribed from nvpro_core/fileformats/cadscenefile.h."""
import struct, sys, json

def u32(b, o): return struct.unpack_from("<i", b, o)[0]
def u64(b, o): return struct.unpack_from("<Q", b, o)[0]

def main(path):
    b = open(path, "rb").read()
    magic, version = u32(b, 0), u32(b, 4)
    assert magic == 0x5d6a86f3, hex(magic)
    numGeometries, numMaterials, numNodes, rootIDX = u32(b,16), u32(b,20), u32(b,24), u32(b,28)
    geomsOff, nodesOff = u64(b, 40), u64(b, 56)

    GEOM_SIZE, NODE_SIZE = 128, 160
    geoms = []
    for i in range(numGeometries):
        o = geomsOff + i * GEOM_SIZE
        numParts, numVertices, numIndexSolid, numIndexWire = (u32(b,o+64), u32(b,o+68), u32(b,o+72), u32(b,o+76))
        geoms.append(dict(parts=numParts, verts=numVertices, idxSolid=numIndexSolid, idxWire=numIndexWire))

    inst_count = [0]*numGeometries
    total_node_parts = 0
    max_children = 0
    leaf_nodes = 0
    for i in range(numNodes):
        o = nodesOff + i * NODE_SIZE
        geometryIDX, numParts, numChildren = u32(b,o+128), u32(b,o+132), u32(b,o+136)
        if geometryIDX >= 0:
            inst_count[geometryIDX] += 1
            total_node_parts += numParts
        if numChildren == 0: leaf_nodes += 1
        max_children = max(max_children, numChildren)

    geo_tris  = sum(g["idxSolid"] for g in geoms) // 3
    geo_verts = sum(g["verts"] for g in geoms)
    geo_parts = sum(g["parts"] for g in geoms)
    inst_tris  = sum((geoms[g]["idxSolid"]//3) * c for g, c in enumerate(inst_count))
    inst_verts = sum(geoms[g]["verts"] * c for g, c in enumerate(inst_count))
    used = sum(1 for c in inst_count if c > 0)

    print(json.dumps(dict(
        version=version, numGeometries=numGeometries, numMaterials=numMaterials,
        numNodes=numNodes, rootIDX=rootIDX,
        uniqueGeometriesUsed=used,
        uniqueTriangles=geo_tris, uniqueVertices=geo_verts, uniqueParts=geo_parts,
        instancedTriangles=inst_tris, instancedVertices=inst_verts,
        drawableParts=total_node_parts,
        instancingRatioTris=round(inst_tris/max(1,geo_tris),2),
        maxInstancesOfOneGeometry=max(inst_count),
        leafNodes=leaf_nodes, maxChildren=max_children), indent=1))

if __name__ == "__main__":
    main(sys.argv[1])
