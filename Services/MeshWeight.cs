namespace PlumbobForge.Backend.Services;

public enum MeshWeightLevel
{
    Light,
    Medium,
    Heavy,
    VeryHeavy
}

/// <summary>
/// Rough rules of thumb for how demanding a mesh is, from the most detailed level's triangle count.
/// CAS parts are drawn many times at once (every Sim on screen), so their limits are tighter in spirit
/// but higher in number than those of a single placed object.
/// </summary>
public static class MeshWeight
{
    public static MeshWeightLevel Of(bool isCas, int triangles)
    {
        var (medium, heavy, veryHeavy) = Limits(isCas);
        if (triangles >= veryHeavy) return MeshWeightLevel.VeryHeavy;
        if (triangles >= heavy) return MeshWeightLevel.Heavy;
        if (triangles >= medium) return MeshWeightLevel.Medium;
        return MeshWeightLevel.Light;
    }

    public static bool IsHeavy(bool isCas, int triangles) => Of(isCas, triangles) >= MeshWeightLevel.Heavy;

    /// <summary>Triangle counts where an item becomes medium, heavy and very heavy.</summary>
    public static (int Medium, int Heavy, int VeryHeavy) Limits(bool isCas) =>
        isCas ? (6_000, 15_000, 30_000) : (3_000, 8_000, 20_000);
}
