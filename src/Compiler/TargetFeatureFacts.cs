namespace Stark.Compiler;

/// <summary>
/// Parses LLVM target-feature switches without changing their command-line
/// order. A target description may mention a feature at most once; accepting
/// repeated switches would make a manifest's meaning depend on a later
/// consumer preserving last-switch-wins semantics exactly.
/// </summary>
internal static class TargetFeatureFacts
{
    public static bool TryNormalizeDistinct(
        IReadOnlyList<string>? values,
        out IReadOnlyList<string> normalized,
        out string error)
    {
        var result = new List<string>(values?.Count ?? 0);
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            if (!TryParse(value, out var featureName, out var enabled, out error))
            {
                normalized = Array.Empty<string>();
                return false;
            }

            if (states.TryGetValue(featureName, out var previousState))
            {
                error = previousState == enabled
                    ? $"target feature '{featureName}' is declared more than once"
                    : $"target feature '{featureName}' has conflicting enable and disable switches";
                normalized = Array.Empty<string>();
                return false;
            }

            states.Add(featureName, enabled);
            result.Add(value.Trim());
        }

        normalized = result.ToArray();
        error = string.Empty;
        return true;
    }

    public static IReadOnlyDictionary<string, bool> GetStates(IReadOnlyList<string>? values)
    {
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            if (TryParse(value, out var featureName, out var enabled, out _))
            {
                // Active compiler options are processed in command-line order.
                // Validation rejects repeats before backend use, but retaining
                // deterministic last-switch behavior keeps diagnostics stable.
                states[featureName] = enabled;
            }
        }

        return states;
    }

    public static IReadOnlyList<string> GetMissingEnabledFeatures(
        IReadOnlyList<string>? required,
        IReadOnlyList<string>? available)
    {
        var requiredStates = GetStates(required);
        var availableStates = GetStates(available);
        return requiredStates
            .Where(static entry => entry.Value)
            .Select(static entry => entry.Key)
            .Where(feature => !availableStates.TryGetValue(feature, out var enabled) || !enabled)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<string> GetEnabledFeatures(IReadOnlyList<string>? values)
    {
        return GetStates(values)
            .Where(static entry => entry.Value)
            .Select(static entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryParse(
        string? value,
        out string featureName,
        out bool enabled,
        out string error)
    {
        featureName = string.Empty;
        enabled = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "target feature switches must not be empty";
            return false;
        }

        var normalized = value.Trim();
        enabled = normalized[0] != '-';
        var nameStart = normalized[0] == '+'
            || normalized[0] == '-'
                ? 1
                : 0;
        if (nameStart == normalized.Length)
        {
            error = $"target feature switch '{normalized}' does not name a feature";
            return false;
        }

        var name = normalized[nameStart..];
        if (name.Any(char.IsWhiteSpace))
        {
            error = $"target feature switch '{normalized}' contains whitespace in its feature name";
            return false;
        }

        featureName = name.ToLowerInvariant();
        error = string.Empty;
        return true;
    }
}
