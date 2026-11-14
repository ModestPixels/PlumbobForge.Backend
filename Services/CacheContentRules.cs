using System;
using System.Text;
using System.Text.RegularExpressions;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// What the game is allowed to load. Library caches and vault indexes follow the same rules.
public static class CacheContentRules
{
    // "Doll Dressed": this OBJD crashes the game to desktop. A package containing it is never loaded.
    public static readonly TGI_Key CrashingObjectKey = new(832458525u, 0u, 4064452635095512314uL);

    // Sims3Pack manifest. The game has no use for it; it stays stored so an item can be exported unchanged.
    public const uint ManifestType = 0x73E93EEB;

    public static bool IsLoadableType(uint type) => type != ManifestType;
}

public static class PatternKeyRepair
{
    private static readonly Regex BrokenKeyRegex = new(
        @"((?:key:|<reskey>\s*\d+\s*:)\s*[0-9a-fA-F]{8}\s*:\s*[0-9a-fA-F]{8}\s*):\s+([0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly byte[] ColonSpace = [(byte)':', (byte)' '];

    // _XML, PTRN and the Sims3Pack manifest can all carry pattern reskeys.
    public static bool AppliesTo(uint type) => type is 0x0333406Cu or 0xD4D9FBE5u or CacheContentRules.ManifestType;

    public static MatchCollection FindBrokenKeys(string text) => BrokenKeyRegex.Matches(text);

    public static bool TryRepair(byte[] plain, out byte[] repaired)
    {
        repaired = plain;
        // Every broken key contains ": "; most resources never do, so they are not decoded at all.
        if (plain.AsSpan().IndexOf(ColonSpace) < 0) return false;

        string text = Encoding.UTF8.GetString(plain);
        if (!BrokenKeyRegex.IsMatch(text)) return false;

        repaired = Encoding.UTF8.GetBytes(BrokenKeyRegex.Replace(text, "$1:$2"));
        return true;
    }
}
