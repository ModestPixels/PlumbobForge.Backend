using System;
using System.Collections.Generic;
using System.Linq;
using Evergine.Bindings.MeshOptimizer;

namespace PlumbobForge.Backend.Services;

public sealed record CcSimplifyResult(GeomMesh Geom, int Triangles, int Vertices, bool DoubleSided);

// Polygon reduction of one GEOM with meshoptimizer, ported from the web CC optimizer (simplify.js).
// meshoptimizer only collapses edges onto existing vertices and never invents new ones, so the reduced mesh is a
// subset of the original vertices: bone weights, vertex IDs (used by fat/fit/thin morphs) and UVs stay valid.
// Double-sided meshes: converted TS4 alpha hair is usually made double-sided by duplicating every strand with
// flipped normals. The copy shares positions but not vertices, which the simplifier would treat as seams. That
// layout is detected, only the front layer is reduced, and the back layer is rebuilt as its exact mirror.
public static class CcMeshSimplifier
{
    private static readonly float[] AttributeWeights = { 0.5f, 0.5f, 0.5f, 1f, 1f }; // normal xyz, uv

    public sealed class Layers
    {
        public bool DoubleSided { get; init; }
        public required int[] Front { get; init; }
        public required int[] Single { get; init; }
        public required int[] Twin { get; init; }
        public bool FlipWinding { get; init; }
        public int MirroredTriangles { get; init; }
    }

    // ------------------------------------------------------------------ double-sided detection

    public static Layers AnalyzeLayers(GeomMesh geom)
    {
        var pos = geom.GetFloats(GeomVertexFormat.UsagePosition, out _) ?? throw new InvalidOperationException("The mesh has no positions");
        var nrm = geom.GetFloats(GeomVertexFormat.UsageNormal, out int nrmN);
        if (nrmN != 3) nrm = null;
        var I = geom.Indices;
        int triCount = I.Length / 3, n = geom.VertexCount;
        var remap = PositionRemap(pos, n);

        // Connected components by vertex index (front and back layers don't share vertices).
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < I.Length; i += 3)
        {
            int a = Find((int)I[i]);
            for (int j = 1; j <= 2; j++) { int b = Find((int)I[i + j]); if (a != b) parent[b] = a; }
        }

        bool IsMirror(int t, int u)
        {
            uint t0 = I[t * 3], t1 = I[t * 3 + 1], t2 = I[t * 3 + 2];
            uint u0 = I[u * 3], u1 = I[u * 3 + 1], u2 = I[u * 3 + 2];
            if (t0 == u0 || t0 == u1 || t0 == u2 || t1 == u0 || t1 == u1 || t1 == u2 || t2 == u0 || t2 == u1 || t2 == u2) return false;
            if (nrm == null) return true;
            uint a = t0;
            uint b = remap[u0] == remap[a] ? u0 : remap[u1] == remap[a] ? u1 : u2;
            return nrm[a * 3] * nrm[b * 3] + nrm[a * 3 + 1] * nrm[b * 3 + 1] + nrm[a * 3 + 2] * nrm[b * 3 + 2] < 0;
        }

        // Match triangles that cover the same positions with disjoint vertices and opposite normals.
        var byKey = new Dictionary<(uint, uint, uint), List<int>>();
        var mirrorOf = new int[triCount];
        Array.Fill(mirrorOf, -1);
        for (int t = 0; t < triCount; t++)
        {
            var key = SortedKey(remap[I[t * 3]], remap[I[t * 3 + 1]], remap[I[t * 3 + 2]]);
            if (!byKey.TryGetValue(key, out var list)) { byKey[key] = new List<int> { t }; continue; }
            int partner = -1;
            foreach (int u in list)
            {
                if (mirrorOf[u] == -1 && IsMirror(t, u)) { partner = u; break; }
            }
            if (partner >= 0) { mirrorOf[t] = partner; mirrorOf[partner] = t; }
            else list.Add(t);
        }

        // Pick a consistent "front" per component pair.
        const byte Front = 1, Back = 2, Mixed = 3;
        var side = new Dictionary<int, byte>();
        for (int t = 0; t < triCount; t++)
        {
            int u = mirrorOf[t]; if (u < 0) continue;
            int ct = Find((int)I[t * 3]), cu = Find((int)I[u * 3]);
            if (ct == cu) { side[ct] = Mixed; continue; }
            if (!side.ContainsKey(ct) && !side.ContainsKey(cu)) { side[ct] = Front; side[cu] = Back; }
        }
        byte SideOf(int root) => side.TryGetValue(root, out var s) ? s : (byte)0;

        var front = new List<int>();
        var single = new List<int>();
        var twin = new int[n];
        Array.Fill(twin, -1);
        int reversed = 0, same = 0;
        Span<uint> uv = stackalloc uint[3];
        Span<uint> tw = stackalloc uint[3];
        for (int t = 0; t < triCount; t++)
        {
            int c = Find((int)I[t * 3]);
            int u = mirrorOf[t];
            if (u >= 0 && SideOf(c) == Front && SideOf(Find((int)I[u * 3])) == Back)
            {
                front.Add(t);
                uv[0] = I[u * 3]; uv[1] = I[u * 3 + 1]; uv[2] = I[u * 3 + 2];
                for (int j = 0; j < 3; j++)
                {
                    uint target = remap[I[t * 3 + j]];
                    tw[j] = remap[uv[0]] == target ? uv[0] : remap[uv[1]] == target ? uv[1] : uv[2];
                    twin[I[t * 3 + j]] = (int)tw[j];
                }
                // Is the back triangle wound the opposite way? (cyclic order of tw vs uv)
                int k = uv.IndexOf(tw[0]);
                if (uv[(k + 1) % 3] == tw[1]) same++; else reversed++;
            }
            else if (u >= 0 && SideOf(c) == Back && SideOf(Find((int)I[u * 3])) == Front)
            {
                // regenerated from its front partner
            }
            else single.Add(t);
        }

        int mirroredTris = front.Count * 2;
        return new Layers
        {
            DoubleSided = mirroredTris > triCount * 0.5,
            Front = front.ToArray(),
            Single = single.ToArray(),
            Twin = twin,
            FlipWinding = reversed >= same,
            MirroredTriangles = mirroredTris,
        };
    }

    private static (uint, uint, uint) SortedKey(uint a, uint b, uint c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    // ------------------------------------------------------------------ simplification

    // ratio: 0..1, fraction of triangles to keep.
    // lockEdges: Keep open edges (necklines, cuffs, waistbands) exactly in place.
    // layers: Result of AnalyzeLayers for this mesh, when already known.
    public static CcSimplifyResult Simplify(GeomMesh geom, double ratio, bool lockEdges = true, Layers? layers = null)
    {
        int triangles = geom.Triangles;
        layers ??= AnalyzeLayers(geom);
        if (ratio >= 0.999 || triangles < 4) return new CcSimplifyResult(geom, triangles, geom.VertexCount, layers.DoubleSided);

        var pos = geom.GetFloats(GeomVertexFormat.UsagePosition, out _)!;
        var nrm = geom.GetFloats(GeomVertexFormat.UsageNormal, out int nrmN);
        var uvs = geom.GetFloats(GeomVertexFormat.UsageUv, out int uvN);
        int n = geom.VertexCount;
        var extra = new float[n * 5];
        for (int v = 0; v < n; v++)
        {
            if (nrm != null && nrmN >= 3) { extra[v * 5] = nrm[v * nrmN]; extra[v * 5 + 1] = nrm[v * nrmN + 1]; extra[v * 5 + 2] = nrm[v * nrmN + 2]; }
            if (uvs != null && uvN >= 2) { extra[v * 5 + 3] = uvs[v * uvN]; extra[v * 5 + 4] = uvs[v * uvN + 1]; }
        }

        uint[] idx;
        if (layers.DoubleSided)
        {
            var f = Reduce(Pick(geom.Indices, layers.Front), pos, n, extra, ratio, lockEdges);
            var s = Reduce(Pick(geom.Indices, layers.Single), pos, n, extra, ratio, lockEdges);
            idx = new uint[f.Length * 2 + s.Length];
            // Interleave each front triangle with its back copy so a strand's two sides stay next to each other.
            for (int i = 0; i < f.Length; i += 3)
            {
                uint a = (uint)layers.Twin[f[i]], b = (uint)layers.Twin[f[i + 1]], c = (uint)layers.Twin[f[i + 2]];
                idx[i * 2] = f[i]; idx[i * 2 + 1] = f[i + 1]; idx[i * 2 + 2] = f[i + 2];
                idx[i * 2 + 3] = a;
                idx[i * 2 + 4] = layers.FlipWinding ? c : b;
                idx[i * 2 + 5] = layers.FlipWinding ? b : c;
            }
            Array.Copy(s, 0, idx, f.Length * 2, s.Length);
        }
        else
        {
            idx = Reduce(geom.Indices, pos, n, extra, ratio, lockEdges);
        }

        // Compact: keep only referenced vertices, preserving their original order.
        var used = new bool[n];
        foreach (uint i in idx) used[i] = true;
        var keep = new List<int>();
        var remap = new int[n];
        for (int v = 0; v < n; v++)
        {
            remap[v] = -1;
            if (!used[v]) continue;
            remap[v] = keep.Count;
            keep.Add(v);
        }
        var newIdx = new uint[idx.Length];
        for (int i = 0; i < idx.Length; i++) newIdx[i] = (uint)remap[idx[i]];
        return new CcSimplifyResult(geom.WithGeometry(keep, newIdx), newIdx.Length / 3, keep.Count, layers.DoubleSided);
    }

    private static uint[] Pick(uint[] indices, int[] tris)
    {
        var o = new uint[tris.Length * 3];
        for (int i = 0; i < tris.Length; i++)
        {
            o[i * 3] = indices[tris[i] * 3]; o[i * 3 + 1] = indices[tris[i] * 3 + 1]; o[i * 3 + 2] = indices[tris[i] * 3 + 2];
        }
        return o;
    }

    private static unsafe uint[] Reduce(uint[] indices, float[] pos, int vertexCount, float[] extra, double ratio, bool lockEdges)
    {
        if (indices.Length < 12) return indices;
        int target = Math.Max(3, (int)Math.Floor(indices.Length / 3.0 * ratio) * 3);
        // Sparse: only consider vertices referenced by these triangles (the hidden back layer shares positions with
        // the front and would otherwise look like seams).
        uint options = (uint)SimplifyOptions.Sparse | (lockEdges ? (uint)SimplifyOptions.LockBorder : 0u);
        var dest = new uint[indices.Length];
        nuint count;
        float error;
        fixed (uint* d = dest, src = indices)
        fixed (float* p = pos, a = extra, w = AttributeWeights)
        {
            count = MeshOptimizer.SimplifyWithAttributes(d, src, (nuint)indices.Length, p, (nuint)vertexCount, 12,
                a, 20, w, (nuint)AttributeWeights.Length, null, (nuint)target, 1.0f, options, &error);
        }
        return dest.AsSpan(0, (int)count).ToArray();
    }

    private static unsafe uint[] PositionRemap(float[] pos, int vertexCount)
    {
        var remap = new uint[vertexCount];
        fixed (uint* r = remap)
        fixed (float* p = pos)
        {
            MeshOptimizer.GeneratePositionRemap(r, p, (nuint)vertexCount, 12);
        }
        return remap;
    }
}
