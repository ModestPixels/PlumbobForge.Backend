using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// One GEOM in a package and the level of detail its VPXY puts it in.
public sealed class CcMesh
{
    public const int Unlisted = -1;

    public required InspectedResource Resource { get; init; }
    public required GeomMesh Geom { get; init; }
    // Level of detail (0 = most detailed), or Unlisted when no VPXY in its part lists it.
    public int Lod { get; init; }
    // Position within its LOD entry.
    public int Slot { get; init; }
    // Saving keeps a single copy of a key, so a mesh whose key appears twice is never changed.
    public bool HasDuplicateKey { get; init; }

    public TGI_Key Key => Resource.Key;
    public int Triangles => Geom.Triangles;
    public int Vertices => Geom.VertexCount;
    // One- or two-triangle dummy meshes that converters leave in; never reduced.
    public bool IsPlaceholder => Triangles <= 2;
    public bool CanReduce => !IsPlaceholder && !HasDuplicateKey;

    private CcMeshSimplifier.Layers? _layers;
    private (double Ratio, bool Lock, CcSimplifyResult Result)? _last;

    public CcMeshSimplifier.Layers Layers => _layers ??= CcMeshSimplifier.AnalyzeLayers(Geom);
    public bool IsDoubleSided => Layers.DoubleSided;

    // The mesh reduced to ratio; the last result is remembered so live previews stay cheap.
    public CcSimplifyResult Reduce(double ratio, bool lockEdges)
    {
        if (!CanReduce || ratio >= 0.999) return new CcSimplifyResult(Geom, Triangles, Vertices, _layers?.DoubleSided ?? false);
        if (_last is { } l && l.Ratio == ratio && l.Lock == lockEdges) return l.Result;
        var r = CcMeshSimplifier.Simplify(Geom, ratio, lockEdges, Layers);
        _last = (ratio, lockEdges, r);
        return r;
    }
}

public sealed class CcAnalysis
{
    public required IReadOnlyList<CcMesh> Meshes { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<int> Lods => Meshes.Select(m => m.Lod).Distinct().ToList();

    // The two most detailed levels; lower ones are tiny and saved as they are.
    public IReadOnlyList<int> EditableLods
    {
        get
        {
            var all = Lods;
            var listed = all.Where(l => l != CcMesh.Unlisted).ToList();
            return (listed.Count > 0 ? listed : all).Take(2).ToList();
        }
    }

    public IEnumerable<CcMesh> MeshesIn(int lod) => Meshes.Where(m => m.Lod == lod);
    public int TrianglesIn(int lod) => MeshesIn(lod).Sum(m => m.Triangles);
    public int RealTrianglesIn(int lod) => MeshesIn(lod).Where(m => !m.IsPlaceholder).Sum(m => m.Triangles);

    // Clothing has vertex IDs (for morphs) and open edges that meet the body, so those are locked by default.
    public bool SuggestLockEdges => Meshes.Any(m => m.Geom.HasVertexIds);

    // Converters often copy the top level into the second one; suggest linking when they are about the same.
    public bool SuggestLinkSecondLod
    {
        get
        {
            var lods = EditableLods;
            return lods.Count >= 2 && RealTrianglesIn(lods[1]) >= RealTrianglesIn(lods[0]) * 0.7;
        }
    }
}

// What to keep of each level of detail.
public sealed class CcOptimizePlan
{
    // Fraction of triangles to keep per LOD (missing = 1, untouched).
    public Dictionary<int, double> Ratios { get; } = new();
    // Reduce the second level to half of what the first one ends up with.
    public bool LinkSecondLod { get; set; }
    public bool LockEdges { get; set; } = true;

    public static CcOptimizePlan SuggestedFor(CcAnalysis analysis) => new()
    {
        LockEdges = analysis.SuggestLockEdges,
        LinkSecondLod = analysis.SuggestLinkSecondLod,
    };

    public bool IsLinked(CcAnalysis analysis, int lod)
    {
        var lods = analysis.EditableLods;
        return LinkSecondLod && lods.Count >= 2 && lod == lods[1];
    }

    public double RatioFor(CcAnalysis analysis, int lod)
    {
        if (IsLinked(analysis, lod))
        {
            int first = analysis.EditableLods[0];
            double target = analysis.MeshesIn(first).Where(m => !m.IsPlaceholder)
                .Sum(m => m.Reduce(RatioFor(analysis, first), LockEdges).Triangles) / 2.0;
            int own = analysis.RealTrianglesIn(lod);
            return own > 0 ? Math.Clamp(Math.Floor(target / own * 100 + 0.5) / 100, 0.05, 1) : 1;
        }
        return Ratios.TryGetValue(lod, out var r) ? r : 1;
    }

    public int TrianglesAfter(CcAnalysis analysis, int lod)
    {
        double ratio = RatioFor(analysis, lod);
        return analysis.MeshesIn(lod).Sum(m => m.Reduce(ratio, LockEdges).Triangles);
    }
}

public sealed record CcMeshChange(TGI_Key Key, int Lod, int Before, int After);

public sealed record CcOptimizeResult(IReadOnlyList<CcMeshChange> Changes)
{
    public int TrianglesBefore => Changes.Sum(c => c.Before);
    public int TrianglesAfter => Changes.Sum(c => c.After);
}

// The CC optimizer: lowers the polygon count of CAS part meshes (typically TS4 conversions) per level of detail.
// Only GEOM resources are replaced; CAS parts, textures, morphs and bones are left untouched. Ported from the
// web CC optimizer (cc.js / export.js).
public class CcOptimizerService
{
    private const uint Vpxy = 0x736884F1u;

    private readonly AppDbContext _db;
    private readonly PackageInspectorService _inspector;
    private readonly PackageEditService _editor;
    private readonly CacheBuilderService _cacheBuilder;

    public CcOptimizerService(AppDbContext db, PackageInspectorService inspector, PackageEditService editor, CacheBuilderService cacheBuilder)
    {
        _db = db;
        _inspector = inspector;
        _editor = editor;
        _cacheBuilder = cacheBuilder;
    }

    public static CcAnalysis Analyse(PackageInspection inspection, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // LOD and slot of every mesh, from the VPXY resources of the same part. Meshes are matched by instance.
        var lodOf = new Dictionary<(int Part, ulong Instance), (int Lod, int Slot)>();
        foreach (var res in inspection.Resources.Where(r => r.Key.Type == Vpxy))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var (lod, keys) in ReadVpxy(inspection.Read(res)))
                {
                    for (int slot = 0; slot < keys.Count; slot++)
                    {
                        if (keys[slot].Type == GeomMesh.ResourceType) lodOf[(res.PartIndex, keys[slot].Instance)] = (lod, slot);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read the LOD table {res.Key}: {ex.Message}");
            }
        }

        var keyCounts = inspection.Resources
            .GroupBy(r => (r.PartIndex, r.Key))
            .ToDictionary(g => g.Key, g => g.Count());

        var meshes = new List<CcMesh>();
        foreach (var res in inspection.Resources.Where(r => r.Key.Type == GeomMesh.ResourceType))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var geom = GeomMesh.Read(inspection.Read(res));
                var where = lodOf.TryGetValue((res.PartIndex, res.Key.Instance), out var w) ? w : (Lod: CcMesh.Unlisted, Slot: 0);
                meshes.Add(new CcMesh
                {
                    Resource = res,
                    Geom = geom,
                    Lod = where.Lod,
                    Slot = where.Slot,
                    HasDuplicateKey = keyCounts[(res.PartIndex, res.Key)] > 1,
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"Mesh {res.Key} could not be read: {ex.Message}");
            }
        }

        var ordered = meshes
            .OrderBy(m => m.Lod == CcMesh.Unlisted ? 99 : m.Lod)
            .ThenBy(m => m.Slot)
            .ToList();
        return new CcAnalysis { Meshes = ordered, Warnings = warnings };
    }

    // The replaced meshes, grouped the way PackageEditService.ApplyAsync takes them.
    public static List<PartEdits> BuildEdits(PackageInspection inspection, CcAnalysis analysis, CcOptimizePlan plan, out CcOptimizeResult result)
    {
        var changes = new List<CcMeshChange>();
        var byPart = new Dictionary<int, List<ResourceEdit>>();
        foreach (var lod in analysis.Lods)
        {
            double ratio = plan.RatioFor(analysis, lod);
            if (ratio >= 0.999) continue;
            foreach (var m in analysis.MeshesIn(lod))
            {
                if (!m.CanReduce) continue;
                var r = m.Reduce(ratio, plan.LockEdges);
                if (r.Triangles >= m.Triangles) continue;
                int part = m.Resource.PartIndex;
                if (!byPart.TryGetValue(part, out var list)) byPart[part] = list = new List<ResourceEdit>();
                list.Add(new ResourceEdit(m.Key, r.Geom.Write()));
                changes.Add(new CcMeshChange(m.Key, lod, m.Triangles, r.Triangles));
            }
        }
        result = new CcOptimizeResult(changes);
        return byPart
            .Select(p => new PartEdits(p.Key, inspection.Parts.FirstOrDefault(x => x.Index == p.Key)?.MetaEntityId, p.Value))
            .ToList();
    }

    // Opens the item, reduces it with configure's plan and saves it (library files go to the Recycle Bin first).
    public async Task<CcOptimizeResult> OptimizeAsync(long itemId, Action<CcAnalysis, CcOptimizePlan> configure, CancellationToken ct = default)
    {
        List<PartEdits> edits;
        CcOptimizeResult result;
        using (var inspection = await _inspector.InspectAsync(itemId, ct))
        {
            (edits, result) = await Task.Run(() =>
            {
                var analysis = Analyse(inspection, ct);
                var plan = CcOptimizePlan.SuggestedFor(analysis);
                configure(analysis, plan);
                var e = BuildEdits(inspection, analysis, plan, out var r);
                return (e, r);
            }, ct);
        }
        await SaveAsync(itemId, edits);
        return result;
    }

    // Saves edits from BuildEdits. Any inspection of the item must be disposed first.
    public async Task SaveAsync(long itemId, IReadOnlyList<PartEdits> edits)
    {
        if (edits.Count == 0) return;
        await _editor.ApplyAsync(itemId, edits);
        if (await _db.MetaEntities.Where(m => m.Id == itemId).Select(m => m.IsVaultManaged).FirstOrDefaultAsync())
        {
            _cacheBuilder.OnGameContentChanged();
        }
    }

    // VPXY chunk: tag, version, table offset (from byte 12), table size, entry count, entries, then the key table
    // (type, group, instance). Entry type 0 is a LOD: level, key count, key indices. Entry type 1 is a single index.
    internal static List<(int Lod, List<TGI_Key> Keys)> ReadVpxy(byte[] rcol)
    {
        if (rcol.Length < 20) throw new InvalidDataException("VPXY is too short");
        uint intCount = BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(16));
        uint extCount = BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(12));
        if (intCount == 0 || intCount > 4096 || extCount > 4096) throw new InvalidDataException("VPXY has no chunk");
        int table = 20 + (int)(intCount + extCount) * 16;
        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(table));
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(rcol.AsSpan(table + 4));
        if (start < 0 || size < 16 || (long)start + size > rcol.Length) throw new InvalidDataException("VPXY chunk lies outside the resource");
        var c = rcol.AsSpan(start, size);
        if (c[0] != 'V' || c[1] != 'P' || c[2] != 'X' || c[3] != 'Y') throw new InvalidDataException("Missing VPXY tag");

        int p = 8;
        int keyBase = 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(c[p..]);
        p = 16;
        int count = c[p++];
        var entries = new List<(int Lod, List<int> Idx)>();
        for (int i = 0; i < count; i++)
        {
            int kind = c[p++];
            if (kind == 0)
            {
                int lod = c[p++], n = c[p++];
                var idx = new List<int>(n);
                for (int k = 0; k < n; k++, p += 4) idx.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(c[p..]));
                entries.Add((lod, idx));
            }
            else if (kind == 1) p += 4;
            else throw new InvalidDataException($"Unknown VPXY entry type {kind}");
        }

        p = keyBase;
        int keyCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(c[p..]), 4096u);
        p += 4;
        var keys = new List<TGI_Key>(keyCount);
        for (int i = 0; i < keyCount; i++, p += 16)
        {
            keys.Add(new TGI_Key(
                BinaryPrimitives.ReadUInt32LittleEndian(c[p..]),
                BinaryPrimitives.ReadUInt32LittleEndian(c[(p + 4)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(c[(p + 8)..])));
        }
        return entries
            .Select(e => (e.Lod, e.Idx.Where(i => i >= 0 && i < keys.Count).Select(i => keys[i]).ToList()))
            .ToList();
    }
}
