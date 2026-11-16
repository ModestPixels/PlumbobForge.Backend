using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

/// <summary>One CAS part or object of the library together with the size of its mesh.</summary>
public sealed class MeshReportRow
{
    public long MetaEntityId { get; init; }
    public long? SetId { get; init; }
    public string SetName { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public bool IsVault { get; init; }

    public InspectedObjectKind Kind { get; init; }
    public string? Name { get; init; }
    public string? ClothingType { get; init; }
    public string CategoryKey { get; init; } = string.Empty;
    public bool IsOff { get; init; }
    public TGI_Key ResourceKey { get; init; }
    /// <summary>The item's smallest thumbnail as stored in the package, when it has one.</summary>
    public byte[]? Thumbnail { get; set; }

    public int Triangles { get; init; }
    public int Vertices { get; init; }
    public int LodCount { get; init; }

    public bool IsCas => Kind == InspectedObjectKind.Cas;
    public MeshWeightLevel Level => MeshWeight.Of(IsCas, Triangles);
}

public sealed class MeshReportSummary
{
    public List<MeshReportRow> Rows { get; } = new();
    public int PackagesScanned { get; set; }
    public int PackagesUnreadable { get; set; }
    /// <summary>Objects that use the game's own meshes and so could not be counted.</summary>
    public int ExternalMeshObjects { get; set; }
}

/// <summary>Counts the triangles of everything in the library. Read-only: no package is touched.</summary>
public class MeshReportService
{
    private readonly AppDbContext _db;
    private readonly PackageInspectorService _inspector;

    public MeshReportService(AppDbContext db, PackageInspectorService inspector)
    {
        _db = db;
        _inspector = inspector;
    }

    private const int MaxThumbnailRows = 3000;
    private const int MaxThumbnailBytes = 200 * 1024;

    private static byte[]? ReadThumbnail(PackageInspection inspection, InspectedObject obj)
    {
        var smallest = obj.Thumbnails.OrderBy(t => t.SizeIndex).FirstOrDefault();
        if (smallest == null) return null;
        try
        {
            var bytes = inspection.Read(smallest.Resource);
            return bytes.Length is > 0 and <= MaxThumbnailBytes ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    // Returns how many objects use a mesh from another package (and so have no row).
    private static int CollectRows(MetaEntity meta, PackageInspection inspection, ISet<(long, TGI_Key)> disabled, Action<MeshReportRow> add)
    {
        int external = 0;
        foreach (var obj in inspection.Objects)
        {
            var mesh = obj.Mesh;
            if (mesh == null) continue;
            if (!mesh.HasData)
            {
                if (mesh.HasExternalMesh) external++;
                continue;
            }
            add(new MeshReportRow
            {
                MetaEntityId = meta.Id,
                SetId = meta.SetsEntityId,
                SetName = meta.SetsEntity?.Name ?? string.Empty,
                FileName = meta.FileName,
                IsVault = meta.IsVaultManaged,
                Kind = obj.Kind,
                Name = obj.Name ?? obj.InternalName,
                ClothingType = obj.ClothingType,
                CategoryKey = obj.CategoryKey,
                IsOff = disabled.Contains((meta.Id, obj.Resource.Key)),
                ResourceKey = obj.Resource.Key,
                Thumbnail = ReadThumbnail(inspection, obj),
                Triangles = mesh.Triangles,
                Vertices = mesh.Vertices,
                LodCount = mesh.Lods.Count
            });
        }
        return external;
    }

    // Rows for one item, after a tool changed its meshes; null when the item is gone or unreadable.
    public async Task<List<MeshReportRow>?> ScanItemAsync(long metaEntityId, CancellationToken ct = default)
    {
        var meta = await _db.MetaEntities.AsNoTracking()
            .Include(m => m.SetsEntity)
            .FirstOrDefaultAsync(m => m.Id == metaEntityId, ct);
        if (meta == null) return null;

        var disabled = (await _db.DisabledObjects.AsNoTracking()
                .Where(d => d.MetaEntityId == metaEntityId)
                .Select(d => new { d.Type, d.Group, d.Instance })
                .ToListAsync(ct))
            .Select(d => (metaEntityId, new TGI_Key(d.Type, d.Group, d.Instance)))
            .ToHashSet();

        try
        {
            using var inspection = await _inspector.InspectAsync(meta.Id, ct);
            var rows = new List<MeshReportRow>();
            await Task.Run(() =>
            {
                MeshStatsService.Analyse(inspection, ct);
                CollectRows(meta, inspection, disabled, rows.Add);
            }, ct);
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<MeshReportSummary> ScanAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        // A vault Sims3Pack's own packages are items of their own; they are read through the parent.
        var metas = await _db.MetaEntities.AsNoTracking()
            .Include(m => m.SetsEntity)
            .Where(m => m.ParentMetaEntityId == null)
            .ToListAsync(ct);

        var disabled = (await _db.DisabledObjects.AsNoTracking()
                .Select(d => new { d.MetaEntityId, d.Type, d.Group, d.Instance })
                .ToListAsync(ct))
            .Select(d => (d.MetaEntityId, new TGI_Key(d.Type, d.Group, d.Instance)))
            .ToHashSet();

        var summary = new MeshReportSummary();
        var rows = new ConcurrentBag<MeshReportRow>();
        int done = 0, unreadable = 0, external = 0;
        int total = metas.Count;

        void Report() => progress?.Report((Interlocked.Increment(ref done), total));

        void Collect(MetaEntity meta, PackageInspection inspection)
        {
            int ext = CollectRows(meta, inspection, disabled, rows.Add);
            if (ext > 0) Interlocked.Add(ref external, ext);
        }

        await Task.Run(() =>
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = ct
            };

            Parallel.ForEach(metas.Where(m => !m.IsVaultManaged), options, meta =>
            {
                try
                {
                    if (!File.Exists(meta.CompleteFileName)) { Interlocked.Increment(ref unreadable); return; }
                    using var inspection = PackageInspectorService.InspectFile(meta.CompleteFileName, ct);
                    MeshStatsService.Analyse(inspection, ct);
                    Collect(meta, inspection);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref unreadable);
                }
                finally
                {
                    Report();
                }
            });
        }, ct);

        // Vault items go through the database, which is not shared between threads.
        foreach (var meta in metas.Where(m => m.IsVaultManaged))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var inspection = await _inspector.InspectAsync(meta.Id, ct);
                await Task.Run(() => MeshStatsService.Analyse(inspection, ct), ct);
                Collect(meta, inspection);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                unreadable++;
            }
            Report();
        }

        summary.Rows.AddRange(rows.OrderByDescending(r => r.Triangles).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase));
        // Thumbnails are kept in memory, so only the first rows (the ones that matter) get one.
        for (int i = MaxThumbnailRows; i < summary.Rows.Count; i++) summary.Rows[i].Thumbnail = null;
        summary.PackagesScanned = total;
        summary.PackagesUnreadable = unreadable;
        summary.ExternalMeshObjects = external;
        return summary;
    }
}
