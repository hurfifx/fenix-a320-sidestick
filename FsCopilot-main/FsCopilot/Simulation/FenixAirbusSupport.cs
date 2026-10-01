namespace FsCopilot.Simulation;

public static class FenixAirbusSupport
{
    public const string SharedDefinitionName = "FNX_32X";
    public const string DisplayName = "Fenix A319 / A320 / A321";

    private static readonly string[] KnownNames =
    [
        "FNX_32X",
        "FNX_A319",
        "FNX_A320",
        "FNX_A321",
        "FENIX A319",
        "FENIX A320",
        "FENIX A321"
    ];

    public static bool IsSupportedAircraft(string? aircraftName)
    {
        if (string.IsNullOrWhiteSpace(aircraftName))
            return false;

        var normalized = aircraftName.Trim();
        if (KnownNames.Any(name => normalized.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return true;

        return normalized.Contains("FENIX", StringComparison.OrdinalIgnoreCase) &&
               (normalized.Contains("A319", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("A320", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("A321", StringComparison.OrdinalIgnoreCase));
    }

    public static string GetDefinitionProfileName(string? aircraftName) =>
        IsSupportedAircraft(aircraftName)
            ? SharedDefinitionName
            : aircraftName ?? string.Empty;
}
