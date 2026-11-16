using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public sealed record GeomVertexFormat(uint Usage, uint DataType, byte Size)
{
    public const uint UsagePosition = 1, UsageNormal = 2, UsageUv = 3, UsageBoneIndices = 4, UsageBoneWeights = 5,
        UsageTangent = 6, UsageColor = 7, UsageVertexId = 10;
    public const uint DataTypeFloat = 1;
}

// TS3 GEOM mesh. Unchanged data is kept byte for byte. Ported from the web CC optimizer.
public sealed class GeomMesh
{
    public const uint ResourceType = 0x015A1849u;
    // Shader hashes: hair strands use simhair.
    public const uint SimHairShader = 0x84FD7152u;

    private uint _rcolVersion, _publicChunks, _unused;
    private byte[][] _internalKeys = Array.Empty<byte[]>(); // 16 raw bytes each
    private byte[][] _externalKeys = Array.Empty<byte[]>();
    private byte[][] _otherChunks = Array.Empty<byte[]>();   // chunks after the GEOM one, untouched

    public uint Version { get; private set; }
    public uint Shader { get; private set; }
    public byte[]? Material { get; private set; } // MTNF block, present when Shader != 0
    public uint MergeGroup { get; private set; }
    public uint SortOrder { get; private set; }
    public int VertexCount { get; private set; }
    public IReadOnlyList<GeomVertexFormat> Formats { get; private set; } = Array.Empty<GeomVertexFormat>();
    public int Stride { get; private set; }
    public byte[] VertexData { get; private set; } = Array.Empty<byte>();
    public uint[] Indices { get; private set; } = Array.Empty<uint>();
    public int SkinIndex { get; private set; }
    public uint[] BoneHashes { get; private set; } = Array.Empty<uint>();
    private byte[][] _tgis = Array.Empty<byte[]>(); // type, group, instance: 16 raw bytes each

    public int Triangles => Indices.Length / 3;
    public bool IsHairShader => Shader == SimHairShader;

    // Texture keys named by the mesh's material, by parameter hash.
    public Dictionary<uint, TGI_Key> MaterialTextures()
    {
        var o = new Dictionary<uint, TGI_Key>();
        var c = Material;
        if (c == null) return o;
        int block = c.AsSpan().IndexOf("MTNF"u8);
        if (block < 0 || block + 16 > c.Length) return o;
        int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(block + 12)), 1024u);
        for (int i = 0; i < count && block + 32 + i * 16 <= c.Length; i++)
        {
            int e = block + 16 + i * 16;
            if (BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(e + 4)) != 4) continue;
            int off = block + (int)BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(e + 12));
            if (off < 0 || off + 4 > c.Length) continue;
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(off));
            if (index >= _tgis.Length) continue;
            var t = _tgis[index];
            o[BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(e))] = new TGI_Key(
                BinaryPrimitives.ReadUInt32LittleEndian(t), BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(4)), BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(8)));
        }
        return o;
    }
    public bool HasVertexIds => Formats.Any(f => f.Usage == GeomVertexFormat.UsageVertexId);

    private GeomMesh() { }

    public static GeomMesh Read(byte[] resource)
    {
        var g = new GeomMesh();
        var r = new Reader(resource, 0);
        g._rcolVersion = r.U32();
        g._publicChunks = r.U32();
        g._unused = r.U32();
        int extCount = r.Count(r.U32());
        int intCount = r.Count(r.U32());
        if (intCount == 0) throw new InvalidDataException("The mesh resource has no chunks");
        g._internalKeys = Enumerable.Range(0, intCount).Select(_ => r.Bytes(16)).ToArray();
        g._externalKeys = Enumerable.Range(0, extCount).Select(_ => r.Bytes(16)).ToArray();
        var chunks = Enumerable.Range(0, intCount).Select(_ => (Offset: (int)r.U32(), Size: (int)r.U32())).ToArray();
        foreach (var c in chunks)
        {
            if (c.Offset < 0 || c.Size < 0 || (long)c.Offset + c.Size > resource.Length) throw new InvalidDataException("A mesh chunk lies outside the resource");
        }
        g._otherChunks = chunks.Skip(1).Select(c => resource.AsSpan(c.Offset, c.Size).ToArray()).ToArray();
        g.ReadChunk(resource.AsSpan(chunks[0].Offset, chunks[0].Size).ToArray());
        return g;
    }

    private void ReadChunk(byte[] chunk)
    {
        var r = new Reader(chunk, 0);
        if (r.Tag() != "GEOM") throw new InvalidDataException("Missing GEOM tag");
        Version = r.U32();
        uint tgiOffset = r.U32();
        int tgiBase = r.Pos + (int)tgiOffset;
        r.U32(); // tgi block size, recomputed on write

        Shader = r.U32();
        if (Shader != 0) Material = r.Bytes(r.Count(r.U32(), chunk.Length));
        MergeGroup = r.U32();
        SortOrder = r.U32();
        VertexCount = r.Count(r.U32(), 1 << 20);
        int fmtCount = r.Count(r.U32(), 64);
        var formats = new List<GeomVertexFormat>(fmtCount);
        for (int i = 0; i < fmtCount; i++) formats.Add(new GeomVertexFormat(r.U32(), r.U32(), r.U8()));
        Formats = formats;
        Stride = formats.Sum(f => f.Size);
        VertexData = r.Bytes(checked(VertexCount * Stride));

        uint itemCount = r.U32();
        if (itemCount != 1) throw new InvalidDataException($"Unexpected GEOM item count {itemCount}");
        byte indexSize = r.U8();
        if (indexSize != 2) throw new InvalidDataException($"Unexpected index size {indexSize}");
        int idxCount = r.Count(r.U32(), chunk.Length / 2);
        Indices = new uint[idxCount];
        for (int i = 0; i < idxCount; i++) Indices[i] = r.U16();

        SkinIndex = (int)r.U32();
        int boneCount = r.Count(r.U32(), 4096);
        BoneHashes = Enumerable.Range(0, boneCount).Select(_ => r.U32()).ToArray();

        r.Pos = tgiBase;
        int tgiCount = r.Count(r.U32(), 4096);
        _tgis = Enumerable.Range(0, tgiCount).Select(_ => r.Bytes(16)).ToArray();
    }

    public byte[] Write()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("GEOM"u8);
        w.Write(Version);
        w.Write(0u); // tgi offset, patched below
        w.Write((uint)(4 + _tgis.Length * 16));
        w.Write(Shader);
        if (Shader != 0)
        {
            w.Write((uint)Material!.Length);
            w.Write(Material);
        }
        w.Write(MergeGroup);
        w.Write(SortOrder);
        w.Write((uint)VertexCount);
        w.Write((uint)Formats.Count);
        foreach (var f in Formats) { w.Write(f.Usage); w.Write(f.DataType); w.Write(f.Size); }
        w.Write(VertexData);
        w.Write(1u);
        w.Write((byte)2);
        w.Write((uint)Indices.Length);
        foreach (uint i in Indices) w.Write(checked((ushort)i));
        w.Write(SkinIndex);
        w.Write((uint)BoneHashes.Length);
        foreach (uint b in BoneHashes) w.Write(b);
        long beforeTgi = ms.Position;
        w.Write((uint)_tgis.Length);
        foreach (var t in _tgis) w.Write(t);
        w.Flush();
        var chunk = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8), (uint)(beforeTgi - 12));
        return WriteRcol(chunk);
    }

    private byte[] WriteRcol(byte[] geomChunk)
    {
        var chunks = new[] { geomChunk }.Concat(_otherChunks).ToArray();
        int n = chunks.Length;
        int total = 20 + (n + _externalKeys.Length) * 16 + n * 8;
        var offsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            total = (total + 3) & ~3;
            offsets[i] = total;
            total += chunks[i].Length;
        }
        var outBytes = new byte[total];
        var w = new BinaryWriter(new MemoryStream(outBytes));
        w.Write(_rcolVersion); w.Write(_publicChunks); w.Write(_unused);
        w.Write((uint)_externalKeys.Length); w.Write((uint)n);
        foreach (var k in _internalKeys) w.Write(k);
        foreach (var k in _externalKeys) w.Write(k);
        for (int i = 0; i < n; i++) { w.Write((uint)offsets[i]); w.Write((uint)chunks[i].Length); }
        for (int i = 0; i < n; i++) chunks[i].CopyTo(outBytes, offsets[i]);
        return outBytes;
    }

    // Stand-in for object meshes; never written.
    public static GeomMesh FromArrays(float[] positions, float[]? normals, float[]? uvs, uint[] indices)
    {
        int n = positions.Length / 3;
        var formats = new List<GeomVertexFormat> { new(GeomVertexFormat.UsagePosition, GeomVertexFormat.DataTypeFloat, 12) };
        if (normals != null) formats.Add(new(GeomVertexFormat.UsageNormal, GeomVertexFormat.DataTypeFloat, 12));
        if (uvs != null) formats.Add(new(GeomVertexFormat.UsageUv, GeomVertexFormat.DataTypeFloat, 8));
        int stride = formats.Sum(f => f.Size);
        var data = new byte[n * stride];
        for (int v = 0; v < n; v++)
        {
            int p = v * stride;
            for (int k = 0; k < 3; k++, p += 4) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(p), positions[v * 3 + k]);
            if (normals != null) for (int k = 0; k < 3; k++, p += 4) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(p), normals[v * 3 + k]);
            if (uvs != null) for (int k = 0; k < 2; k++, p += 4) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(p), uvs[v * 2 + k]);
        }
        return new GeomMesh { VertexCount = n, Formats = formats, Stride = stride, VertexData = data, Indices = indices };
    }

    public GeomMesh WithGeometry(IReadOnlyList<int> keepVertices, uint[] newIndices)
    {
        var data = new byte[keepVertices.Count * Stride];
        for (int i = 0; i < keepVertices.Count; i++)
        {
            Buffer.BlockCopy(VertexData, keepVertices[i] * Stride, data, i * Stride, Stride);
        }
        var copy = (GeomMesh)MemberwiseClone();
        copy.VertexCount = keepVertices.Count;
        copy.VertexData = data;
        copy.Indices = newIndices;
        copy._attributes = null;
        return copy;
    }

    private Dictionary<uint, float[]>? _attributes;

    public float[]? GetFloats(uint usage, out int components)
    {
        components = 0;
        int offset = 0;
        foreach (var f in Formats)
        {
            if (f.Usage == usage)
            {
                if (f.DataType != GeomVertexFormat.DataTypeFloat) return null;
                components = f.Size / 4;
                _attributes ??= new();
                if (_attributes.TryGetValue(usage, out var cached)) return cached;
                var arr = new float[VertexCount * components];
                for (int v = 0; v < VertexCount; v++)
                {
                    for (int k = 0; k < components; k++)
                    {
                        arr[v * components + k] = BinaryPrimitives.ReadSingleLittleEndian(VertexData.AsSpan(v * Stride + offset + k * 4));
                    }
                }
                _attributes[usage] = arr;
                return arr;
            }
            offset += f.Size;
        }
        return null;
    }

    public byte[]? GetRaw(uint usage, out int size)
    {
        size = 0;
        int offset = 0;
        foreach (var f in Formats)
        {
            if (f.Usage == usage)
            {
                size = f.Size;
                var arr = new byte[VertexCount * size];
                for (int v = 0; v < VertexCount; v++) Buffer.BlockCopy(VertexData, v * Stride + offset, arr, v * size, size);
                return arr;
            }
            offset += f.Size;
        }
        return null;
    }

    private sealed class Reader
    {
        private readonly byte[] _b;
        public int Pos;
        public Reader(byte[] b, int pos) { _b = b; Pos = pos; }
        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + n > _b.Length) throw new InvalidDataException("The mesh resource ends early");
            var s = _b.AsSpan(Pos, n);
            Pos += n;
            return s;
        }
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public byte[] Bytes(int n) => Take(n).ToArray();
        public string Tag() => System.Text.Encoding.ASCII.GetString(Take(4));
        public int Count(uint value, int max = 65536)
        {
            if (value > (uint)max) throw new InvalidDataException($"Implausible count {value} in mesh");
            return (int)value;
        }
    }
}
