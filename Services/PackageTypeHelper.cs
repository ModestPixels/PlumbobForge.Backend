using System;
using System.Collections.Generic;

namespace PlumbobForge.Backend.Services;

public static class PackageTypeHelper
{
    // Canonical Type Constants
    public const string CAS = "CAS";
    public const string BuildBuy = "BuildBuy";
    public const string Pattern = "Pattern";
    public const string Lot = "Lot";
    public const string Sim = "Sim";
    public const string World = "World";
    public const string Other = "Other";

    public const uint TypeOBJD = 0x319E4F1D;

    // Kept as a list so EF Core can translate it to SQL (m.PackageType IN (...)).
    public static readonly string[] BuildBuyTypes =
    [
        "BuildBuy",
        "Build/Buy",
        "Object",
        "Objects",
        "Build-Buy",
        "buildbuy",
        "object"
    ];

    // Same names IsCas accepts, as a list EF Core can translate to SQL.
    public static readonly string[] CasTypes =
    [
        "CAS", "cas", "Hair", "Clothing", "Accessories", "Details", "Skin", "Skins", "Sliders", "Presets"
    ];

    public static bool IsBuildBuy(string? packageType)
    {
        if (string.IsNullOrWhiteSpace(packageType)) return false;

        return packageType.Equals("BuildBuy", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Build/Buy", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Object", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Objects", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Build-Buy", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCas(string? packageType)
    {
        if (string.IsNullOrWhiteSpace(packageType)) return false;

        return packageType.Equals("CAS", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Hair", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Clothing", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Accessories", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Details", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Skin", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Skins", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Sliders", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Presets", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPattern(string? packageType)
    {
        if (string.IsNullOrWhiteSpace(packageType)) return false;

        return packageType.Equals("Pattern", StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals("Patterns", StringComparison.OrdinalIgnoreCase);
    }

    // The game only reads lots, Sims and worlds from its own folders, never through Resource.cfg,
    // so they are never merged into a cache or stored in a vault shard.
    public static bool IsLibraryOnlyType(string? packageType)
    {
        if (string.IsNullOrWhiteSpace(packageType)) return false;

        return packageType.Equals(Lot, StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals(Sim, StringComparison.OrdinalIgnoreCase) ||
               packageType.Equals(World, StringComparison.OrdinalIgnoreCase);
    }
}
