using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlumbobForge.Backend.Services;

// An RGBA8 image, rows top to bottom.
public sealed record RgbaImage(int Width, int Height, byte[] Rgba, string Label = "");

// DDS decoding on the CPU: top mip level of DXT1/3/5 and 24/32-bit RGB(A). Ported from the web CC optimizer (textures.js).
public static class DdsDecoder
{
    public static RgbaImage Decode(byte[] bytes)
    {
        if (bytes.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x20534444) throw new InvalidDataException("Not a DDS file");
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidDataException($"Implausible DDS size {width}x{height}");
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80));
        string fourCC = Encoding.ASCII.GetString(bytes, 84, 4);
        var o = new byte[width * height * 4];
        int p = 128;

        if ((pfFlags & 4) != 0)
        {
            if (fourCC is not ("DXT1" or "DXT3" or "DXT5")) throw new InvalidDataException($"Unsupported DDS format {fourCC}");
            int bw = Math.Max(1, (width + 3) >> 2), bh = Math.Max(1, (height + 3) >> 2);
            int blockBytes = fourCC == "DXT1" ? 8 : 16;
            if (p + (long)bw * bh * blockBytes > bytes.Length) throw new InvalidDataException("The DDS file ends early");
            var pal = new byte[16];
            var alpha = new byte[16];
            var lut = new byte[8];
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                if (fourCC == "DXT3")
                {
                    for (int i = 0; i < 16; i++) alpha[i] = (byte)(((bytes[p + (i >> 1)] >> ((i & 1) * 4)) & 15) * 17);
                    p += 8;
                }
                else if (fourCC == "DXT5")
                {
                    int a0 = bytes[p], a1 = bytes[p + 1];
                    lut[0] = (byte)a0; lut[1] = (byte)a1;
                    if (a0 > a1) for (int i = 1; i < 7; i++) lut[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                    else
                    {
                        for (int i = 1; i < 5; i++) lut[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                        lut[6] = 0; lut[7] = 255;
                    }
                    int bits = 0, nbits = 0, q = p + 2;
                    for (int i = 0; i < 16; i++)
                    {
                        if (nbits < 3) { bits |= bytes[q++] << nbits; nbits += 8; }
                        alpha[i] = lut[bits & 7]; bits >>= 3; nbits -= 3;
                    }
                    p += 8;
                }
                else Array.Fill(alpha, (byte)255);

                int c0 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p)), c1 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 2));
                Rgb565(c0, pal, 0); Rgb565(c1, pal, 4);
                pal[3] = pal[7] = pal[11] = pal[15] = 255;
                if (c0 > c1 || fourCC != "DXT1")
                {
                    for (int k = 0; k < 3; k++) { pal[8 + k] = (byte)((2 * pal[k] + pal[4 + k]) / 3); pal[12 + k] = (byte)((pal[k] + 2 * pal[4 + k]) / 3); }
                }
                else
                {
                    for (int k = 0; k < 3; k++) { pal[8 + k] = (byte)((pal[k] + pal[4 + k]) >> 1); pal[12 + k] = 0; }
                    pal[15] = 0;
                }
                uint idx = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4));
                p += 8;
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                    if (x >= width || y >= height) continue;
                    int c = (int)((idx >> (i * 2)) & 3), d = (y * width + x) * 4;
                    o[d] = pal[c * 4]; o[d + 1] = pal[c * 4 + 1]; o[d + 2] = pal[c * 4 + 2];
                    o[d + 3] = fourCC == "DXT1" ? pal[c * 4 + 3] : alpha[i];
                }
            }
        }
        else
        {
            int bpp = (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88)) / 8);
            if (bpp is not (3 or 4)) throw new InvalidDataException($"Unsupported DDS bit depth {bpp * 8}");
            if (p + (long)width * height * bpp > bytes.Length) throw new InvalidDataException("The DDS file ends early");
            var masks = new[] { 92, 96, 100, 104 }.Select(off => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off))).ToArray();
            var shifts = masks.Select(m => m == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(m)).ToArray();
            bool hasAlpha = (pfFlags & 1) != 0 && masks[3] != 0;
            for (int i = 0; i < width * height; i++, p += bpp)
            {
                uint v = bpp == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)) : (uint)(bytes[p] | (bytes[p + 1] << 8) | (bytes[p + 2] << 16));
                for (int k = 0; k < 3; k++) o[i * 4 + k] = shifts[k] < 0 ? (byte)0 : (byte)((v & masks[k]) >> shifts[k]);
                o[i * 4 + 3] = hasAlpha ? (byte)((v & masks[3]) >> shifts[3]) : (byte)255;
            }
        }
        return new RgbaImage(width, height, o, (pfFlags & 4) != 0 ? fourCC : "RGB");
    }

    private static void Rgb565(int c, byte[] o, int at)
    {
        o[at] = (byte)(((c >> 11) & 31) * 255 / 31);
        o[at + 1] = (byte)(((c >> 5) & 63) * 255 / 63);
        o[at + 2] = (byte)((c & 31) * 255 / 31);
    }
}

public sealed record CasPattern(string Name, double[]? Color, bool Enabled);

// A colour variation of a CAS part, stored as XML inside the CASP.
public sealed record CasPreset(string Complate, IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, CasPattern> Patterns)
{
    public bool IsHair => Complate.Contains("hair", StringComparison.OrdinalIgnoreCase)
        || (Values.ContainsKey("Root Color") && Values.ContainsKey("Diffuse Map"));

    private static readonly Regex PresetRx = new(@"<preset>[\s\S]*?</preset>", RegexOptions.Compiled);
    private static readonly Regex ComplateRx = new(@"<complate name=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex PatternRx = new(@"<pattern ([^>]*)>([\s\S]*?)</pattern>", RegexOptions.Compiled);
    private static readonly Regex AttrRx = new(@"(\w+)=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex ValueRx = new(@"<value ([^>]*?)/?>", RegexOptions.Compiled);

    public static List<CasPreset> ReadAll(byte[] casp)
    {
        string text = Encoding.Unicode.GetString(casp);
        var list = new List<CasPreset>();
        foreach (Match m in PresetRx.Matches(text))
        {
            string xml = m.Value;
            string complate = ComplateRx.Match(xml) is { Success: true } c ? c.Groups[1].Value : "";
            var patterns = new Dictionary<string, (string Name, double[]? Color)>();
            // Pattern blocks first, so their inner values do not leak into the top-level values.
            string top = PatternRx.Replace(xml, pm =>
            {
                var at = Attrs(pm.Groups[1].Value);
                var v = ParseValues(pm.Groups[2].Value);
                string variable = at.GetValueOrDefault("variable") ?? "";
                patterns[variable] = (at.GetValueOrDefault("name") ?? "", ParseColor(v.GetValueOrDefault("Color") ?? v.GetValueOrDefault("Color 0")));
                return "";
            });
            var values = ParseValues(top);
            var withState = patterns.ToDictionary(p => p.Key, p => new CasPattern(p.Value.Name, p.Value.Color,
                string.Equals(values.GetValueOrDefault(p.Key + " Enabled") ?? "true", "true", StringComparison.OrdinalIgnoreCase)));
            list.Add(new CasPreset(complate, values, withState));
        }
        return list;
    }

    private static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in AttrRx.Matches(s)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    private static Dictionary<string, string> ParseValues(string s)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in ValueRx.Matches(s))
        {
            var a = Attrs(m.Groups[1].Value);
            if (a.TryGetValue("key", out var k)) d[k] = a.GetValueOrDefault("value") ?? "";
        }
        return d;
    }

    internal static double[]? ParseColor(string? s)
    {
        if (s == null) return null;
        // Like Number() in the web tool: blank is 0, anything unreadable is NaN.
        return s.Split(',').Take(3).Select(x => string.IsNullOrWhiteSpace(x) ? 0
            : double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
    }
}

// Approximate "in-game look" of a CAS part for the preview. The game composites CAS textures with shaders and pattern
// files from the game install, so this is an approximation (ported from the web CC optimizer, material.js):
//   clothing: Multiplier x pattern colours chosen by the Mask's R/G/B/A, then the Overlay alpha-blended on top;
//   hair: greyscale Diffuse Map tinted with the preset's base/root/highlight/tip colours via the Control Map.
public static class CcMaterial
{
    public const uint TextureType = 0x00B2D882u;
    public const string InGame = "ingame";

    private static readonly string[] RoleOrder = { "Diffuse Map", "Multiplier", "Overlay", "Mask", "Control Map", "Clothing Specular", "Specular Map" };

    // Texture roles of a preset that point at a texture inside the package.
    public static Dictionary<string, InspectedResource> PresetTextures(PackageInspection inspection, CasPreset preset)
    {
        var byInstance = new Dictionary<ulong, InspectedResource>();
        foreach (var r in inspection.Resources.Where(r => r.Key.Type == TextureType)) byInstance.TryAdd(r.Key.Instance, r);
        var o = new Dictionary<string, InspectedResource>();
        foreach (var (role, value) in preset.Values)
        {
            if (!value.StartsWith("key:00b2d882:", StringComparison.OrdinalIgnoreCase) || value.Length < 16) continue;
            if (!ulong.TryParse(value[^16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var instance)) continue;
            if (byInstance.TryGetValue(instance, out var res)) o[role] = res;
        }
        return o;
    }

    // Roles that can be shown as a single raw texture, in a sensible order.
    public static List<string> TextureRoles(PackageInspection inspection, CasPreset preset) =>
        PresetTextures(inspection, preset).Keys
            .OrderBy(r => Array.IndexOf(RoleOrder, r) is var i && i >= 0 ? i + 1 : 99)
            .ThenBy(r => r, StringComparer.Ordinal)
            .ToList();

    // mode: InGame, or a role name (raw texture with alpha forced opaque). Null when the package has no usable texture.
    public static RgbaImage? PreviewTexture(PackageInspection inspection, CasPreset preset, string mode = InGame, Dictionary<InspectedResource, RgbaImage>? cache = null)
    {
        var roles = PresetTextures(inspection, preset);
        RgbaImage Decode(InspectedResource r)
        {
            if (cache != null && cache.TryGetValue(r, out var hit)) return hit;
            var img = DdsDecoder.Decode(inspection.Read(r));
            cache?.TryAdd(r, img);
            return img;
        }

        if (mode != InGame)
        {
            if (!roles.TryGetValue(mode, out var e)) return null;
            var img = Decode(e);
            var rgba = (byte[])img.Rgba.Clone();
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            return new RgbaImage(img.Width, img.Height, rgba, mode);
        }
        var result = preset.IsHair ? Hair(preset, roles, Decode) : Clothing(preset, roles, Decode);
        if (result != null) BleedTransparent(result.Rgba);
        return result;
    }

    // Transparent texels usually hold white or garbage colour, which bleeds into strand edges as a bright fringe once
    // the texture is filtered. Fill them with the average opaque colour.
    private static void BleedTransparent(byte[] p)
    {
        double s0 = 0, s1 = 0, s2 = 0;
        long n = 0;
        for (int o = 0; o < p.Length; o += 4) if (p[o + 3] >= 128) { s0 += p[o]; s1 += p[o + 1]; s2 += p[o + 2]; n++; }
        if (n == 0) return;
        double a0 = s0 / n, a1 = s1 / n, a2 = s2 / n;
        for (int o = 0; o < p.Length; o += 4)
        {
            double a = p[o + 3] / 255.0;
            if (a >= 0.5) continue;
            double k = a * 2;
            p[o] = ToByte(a0 + (p[o] - a0) * k);
            p[o + 1] = ToByte(a1 + (p[o + 1] - a1) * k);
            p[o + 2] = ToByte(a2 + (p[o + 2] - a2) * k);
        }
    }

    // Matches assigning a number to a Uint8Array in the web tool (truncate), clamped.
    private static byte ToByte(double v) => double.IsNaN(v) ? (byte)0 : (byte)Math.Clamp((int)v, 0, 255);

    // Nearest-neighbour lookup so textures of different sizes can be combined.
    private static Func<int, int>? Sampler(RgbaImage? img, int w, int h)
    {
        if (img == null) return null;
        if (img.Width == w && img.Height == h) return i => i * 4;
        double sx = (double)img.Width / w, sy = (double)img.Height / h;
        return i => ((int)Math.Floor((i / w) * sy) * img.Width + (int)Math.Floor((i % w) * sx)) * 4;
    }

    private static double[]? Col(IReadOnlyDictionary<string, string> v, string key) => CasPreset.ParseColor(v.GetValueOrDefault(key));

    private static RgbaImage? Hair(CasPreset preset, Dictionary<string, InspectedResource> roles, Func<InspectedResource, RgbaImage> decode)
    {
        var dEntry = roles.GetValueOrDefault("Diffuse Map") ?? roles.GetValueOrDefault("Multiplier");
        if (dEntry == null) return null;
        var diff = decode(dEntry);
        var ctrl = roles.TryGetValue("Control Map", out var ce) ? decode(ce) : null;
        int w = diff.Width, h = diff.Height;
        var cs = Sampler(ctrl, w, h);
        var v = preset.Values;
        var baseColor = Col(v, "Diffuse Color") ?? new[] { 0.4, 0.3, 0.2 };
        var tints = new[] { Col(v, "Root Color"), Col(v, "Highlight Color"), Col(v, "Tip Color") };
        var o = new byte[w * h * 4];
        var c = new double[3];
        for (int i = 0; i < w * h; i++)
        {
            int d = i * 4;
            c[0] = baseColor[0]; c[1] = baseColor[1]; c[2] = baseColor[2];
            if (cs != null)
            {
                int q = cs(i);
                for (int t = 0; t < 3; t++)
                {
                    double k = ctrl!.Rgba[q + t] / 255.0;
                    var tint = tints[t];
                    if (tint != null && k > 0) for (int j = 0; j < 3; j++) c[j] += (tint[j] - c[j]) * k;
                }
            }
            double g = diff.Rgba[d] / 255.0;
            for (int j = 0; j < 3; j++) o[d + j] = ToByte(Math.Min(255, c[j] * g * 255 * 1.6));
            o[d + 3] = diff.Rgba[d + 3];
        }
        return new RgbaImage(w, h, o, "Hair colour (approx.)");
    }

    private static RgbaImage? Clothing(CasPreset preset, Dictionary<string, InspectedResource> roles, Func<InspectedResource, RgbaImage> decode)
    {
        var mEntry = roles.GetValueOrDefault("Multiplier") ?? roles.GetValueOrDefault("Diffuse Map") ?? roles.Values.FirstOrDefault();
        if (mEntry == null) return null;
        var mult = decode(mEntry);
        int w = mult.Width, h = mult.Height;
        var mask = roles.TryGetValue("Mask", out var me) ? decode(me) : null;
        var over = roles.TryGetValue("Overlay", out var oe) ? decode(oe) : null;
        var ms = Sampler(mask, w, h);
        var os = Sampler(over, w, h);
        // Mask channel R/G/B/A -> Pattern A/B/C/D colour (only enabled patterns tint)
        var pats = new[] { "Pattern A", "Pattern B", "Pattern C", "Pattern D" }
            .Select(n => preset.Patterns.TryGetValue(n, out var p) && p.Enabled && p.Color != null ? p.Color : null)
            .ToArray();
        var o = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int d = i * 4;
            double r = 1, g = 1, b = 1;
            if (ms != null)
            {
                int q = ms(i);
                double wsum = 0, tr = 0, tg = 0, tb = 0;
                for (int k = 0; k < 4; k++)
                {
                    var p = pats[k];
                    if (p == null) continue;
                    double wk = (k == 3 ? mask!.Rgba[q + 3] : mask!.Rgba[q + k]) / 255.0;
                    if (k == 3 && mask.Rgba[q + 3] == 255) continue; // opaque alpha = no channel D
                    wsum += wk; tr += p[0] * wk; tg += p[1] * wk; tb += p[2] * wk;
                }
                if (wsum > 1) { tr /= wsum; tg /= wsum; tb /= wsum; wsum = 1; }
                r = 1 - wsum + tr; g = 1 - wsum + tg; b = 1 - wsum + tb;
            }
            double R = mult.Rgba[d] * r, G = mult.Rgba[d + 1] * g, B = mult.Rgba[d + 2] * b;
            if (os != null)
            {
                int q = os(i);
                double a = over!.Rgba[q + 3] / 255.0;
                R += (over.Rgba[q] - R) * a; G += (over.Rgba[q + 1] - G) * a; B += (over.Rgba[q + 2] - B) * a;
            }
            o[d] = ToByte(R); o[d + 1] = ToByte(G); o[d + 2] = ToByte(B); o[d + 3] = mult.Rgba[d + 3];
        }
        return new RgbaImage(w, h, o, "Preset colours (approx.)");
    }

    // Should the texture's alpha cut holes in these meshes? TS3 textures are atlases whose alpha is often just "unused
    // area", so only when a real share of the meshes' own UVs land on transparent pixels. Samples triangle centres,
    // since vertices sit on UV island edges where alpha often fades out.
    public static bool UsesCutout(RgbaImage tex, IEnumerable<GeomMesh> geoms)
    {
        long total = 0, clear = 0;
        foreach (var g in geoms)
        {
            var uv = g.GetFloats(GeomVertexFormat.UsageUv, out int n);
            if (uv == null || n < 2) continue;
            var I = g.Indices;
            int tris = I.Length / 3;
            int step = Math.Max(1, tris / 5000);
            for (int t = 0; t < tris; t += step)
            {
                uint a = I[t * 3], b = I[t * 3 + 1], c = I[t * 3 + 2];
                double u = (uv[a * n] + uv[b * n] + uv[c * n]) / 3.0, v = (uv[a * n + 1] + uv[b * n + 1] + uv[c * n + 1]) / 3.0;
                int x = Math.Min(tex.Width - 1, Math.Max(0, (int)Math.Floor(u * tex.Width)));
                int y = Math.Min(tex.Height - 1, Math.Max(0, (int)Math.Floor(v * tex.Height)));
                total++;
                if (tex.Rgba[(y * tex.Width + x) * 4 + 3] < 128) clear++;
            }
        }
        return total > 0 && (double)clear / total > 0.1;
    }
}
