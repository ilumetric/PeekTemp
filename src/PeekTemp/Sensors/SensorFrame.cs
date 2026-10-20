namespace PeekTemp.Sensors;

/// <summary>Mutable bag of readings that sources fill in priority order (first non-null value wins).</summary>
internal sealed class SensorFrame
{
    public float? CpuTemp;
    public float? CpuLoad;
    public float? CpuPower;
    public string? CpuName;

    public float? GpuTemp;
    public float? GpuLoad;
    public float? GpuPower;
    public float? GpuMemUsedMb;
    public float? GpuMemTotalMb;
    public string? GpuName;

    public readonly List<string> Sources = new();

    public static void Fill(ref float? field, float? value)
    {
        if (field == null && value is { } v && !float.IsNaN(v) && !float.IsInfinity(v))
            field = v;
    }

    public static void Fill(ref string? field, string? value)
    {
        if (string.IsNullOrEmpty(field) && !string.IsNullOrWhiteSpace(value))
            field = value.Trim();
    }
}

/// <summary>Immutable result shown by the UI.</summary>
internal sealed record SensorSnapshot(
    float? CpuTemp, float? CpuLoad, float? CpuPower, string? CpuName,
    float? GpuTemp, float? GpuLoad, float? GpuPower, float? GpuMemUsedMb, float? GpuMemTotalMb, string? GpuName,
    string Source, string? CpuTempIssue)
{
    public static readonly SensorSnapshot Empty =
        new(null, null, null, null, null, null, null, null, null, null, "", null);
}

internal interface ISensorSource : IDisposable
{
    string Name { get; }

    /// <summary>Names of GPUs this source can see (for the GPU picker).</summary>
    IReadOnlyList<string> GpuNames { get; }

    /// <summary>Fills missing values of the frame. Returns false when the source is not usable right now.</summary>
    bool Read(SensorFrame frame, string? preferredGpu);
}

internal static class GpuPicker
{
    /// <summary>Ranks GPUs so that a discrete card wins over an integrated one.</summary>
    public static int Score(string name, string vendorHint = "")
    {
        var n = (name + " " + vendorHint).ToLowerInvariant();
        int score = 0;
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx")) score += 30;
        else if (n.Contains("amd") || n.Contains("radeon")) score += 20;
        else if (n.Contains("intel")) score += 10;

        // Integrated GPUs: "AMD Radeon(TM) Graphics", "Radeon 780M", "Intel UHD/Iris Xe Graphics".
        bool discreteHint = n.Contains(" rx ") || n.Contains("radeon pro") || n.Contains(" arc") ||
                            n.Contains("geforce") || n.Contains("rtx") || n.Contains("quadro");
        bool integratedHint = n.Contains("radeon(tm) graphics") || n.Contains("radeon graphics") ||
                              n.Contains(" uhd") || n.Contains("iris") || n.Contains("hd graphics") ||
                              System.Text.RegularExpressions.Regex.IsMatch(n, @"radeon(\(tm\))? \d{3}m\b");
        if (discreteHint) score += 15;
        if (integratedHint) score -= 15;
        return score;
    }

    public static bool Matches(string candidate, string? preferred) =>
        !string.IsNullOrEmpty(preferred) &&
        (candidate.Contains(preferred, StringComparison.OrdinalIgnoreCase) ||
         preferred.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
