using System;
using System.IO;

namespace PlumbobForge.Backend.Configuration;

public static class AppPaths
{
    public const string DataDirVariable = "PLUMBOBFORGE_DATA_DIR";

    private static readonly string? _override = ReadOverride();

    public static bool IsSandboxed => _override != null;

    public static string AppDataDir => _override
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "plumbobforge-app");

    private static string? ReadOverride()
    {
        var value = Environment.GetEnvironmentVariable(DataDirVariable);
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
    }
}
