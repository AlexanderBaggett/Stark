using System.Text;
using System.Text.RegularExpressions;

namespace Stark.Compiler;

/// <summary>
/// Normalizes LLVM module text down to a structural skeleton so two emissions
/// of the same program can be compared per function across naming,
/// attribute, and metadata differences: comments, debug info, and metadata
/// attachments are stripped; calling conventions and parameter/function
/// attributes are removed; registers and block labels are renumbered in
/// first-appearance order. What survives is the instruction structure —
/// opcodes, types, constants, and control flow — which is the part two
/// correct emitters must agree on. Used by the stage0/stage1 differential
/// harness; metadata CONTENT (e.g. !range facts) is deliberately out of
/// scope here and covered by execution parity and the ported-fact suites.
/// </summary>
public static partial class LlvmTextNormalizer
{
    [GeneratedRegex(@"^define\b[^@]*@([A-Za-z0-9_.$]+|""[^""]+"")\s*\(")]
    private static partial Regex DefinePattern();

    [GeneratedRegex(@",?\s*!(?!range)[A-Za-z_.]+\s+!\d+")]
    private static partial Regex MetadataAttachmentPattern();

    [GeneratedRegex(@",?\s*!range\s+!\d+")]
    private static partial Regex RangeAttachmentPattern();

    [GeneratedRegex(@"\s#\d+\b")]
    private static partial Regex AttributeGroupPattern();

    [GeneratedRegex(@"%[A-Za-z0-9_.$]+")]
    private static partial Regex RegisterPattern();

    [GeneratedRegex(@"^([A-Za-z0-9_.$]+):\s*$")]
    private static partial Regex BlockLabelPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunPattern();

    // Token-level attributes that never change what a function computes.
    // Calling conventions and value attributes differ legitimately between
    // stage0's fully attributed output and stage1's minimal one.
    private static readonly string[] StrippedTokens =
    [
        "fastcc", "tailcc", "coldcc",
        "noundef", "nonnull", "noalias", "nocapture", "nofree",
        "readonly", "readnone", "writeonly", "willreturn", "nounwind",
        "inlinehint", "noinline", "alwaysinline", "norecurse", "mustprogress",
        "signext", "zeroext", "internal", "private", "external", "dso_local",
        "local_unnamed_addr", "unnamed_addr", "tail", "musttail", "notail"
    ];

    [GeneratedRegex(@"\b(?:dereferenceable(?:_or_null)?|align|allocsize)\(\d+\)|\balign \d+\b|\bcaptures\([^)]*\)|\bmemory\([^)]*\)|\binitializes\(\([^)]*\)\)|\brange\([^)]*\)")]
    private static partial Regex ParameterizedAttributePattern();

    /// <summary>
    /// Extracts every <c>define</c> in the module and returns its normalized
    /// body keyed by symbol name (module-name prefixes like
    /// <c>Demo_</c>/<c>Demo.</c> are not stripped; align corpus fixtures so
    /// both stages emit the same symbol, or match by suffix at the call
    /// site).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ExtractNormalizedFunctions(string moduleText)
    {
        var functions = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = moduleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var define = DefinePattern().Match(lines[index]);
            if (!define.Success)
            {
                continue;
            }

            var name = define.Groups[1].Value.Trim('"');
            var bodyLines = new List<string> { lines[index] };
            index++;
            while (index < lines.Length && lines[index].TrimEnd() != "}")
            {
                bodyLines.Add(lines[index]);
                index++;
            }

            functions[name] = NormalizeFunction(bodyLines);
        }

        return functions;
    }

    /// <summary>
    /// Renders a compact per-function comparison of two modules: functions
    /// present on only one side, and unified before/after text for functions
    /// whose normalized skeletons differ. Empty string when equivalent.
    /// </summary>
    public static string DiffModules(string expectedModuleText, string actualModuleText, string expectedLabel = "stage0", string actualLabel = "stage1")
    {
        var expected = ExtractNormalizedFunctions(expectedModuleText);
        var actual = ExtractNormalizedFunctions(actualModuleText);
        var report = new StringBuilder();

        foreach (var name in expected.Keys.Union(actual.Keys, StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal))
        {
            var inExpected = expected.TryGetValue(name, out var expectedBody);
            var inActual = actual.TryGetValue(name, out var actualBody);

            if (!inExpected)
            {
                report.AppendLine($"@{name}: only in {actualLabel}");
                continue;
            }

            if (!inActual)
            {
                report.AppendLine($"@{name}: only in {expectedLabel}");
                continue;
            }

            if (!string.Equals(expectedBody, actualBody, StringComparison.Ordinal))
            {
                report.AppendLine($"@{name}: skeletons differ");
                report.AppendLine($"--- {expectedLabel}");
                report.AppendLine(expectedBody);
                report.AppendLine($"+++ {actualLabel}");
                report.AppendLine(actualBody);
            }
        }

        return report.ToString();
    }

    private static string NormalizeFunction(List<string> rawLines)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = new List<string>();

        foreach (var rawLine in rawLines)
        {
            var line = rawLine;

            var comment = line.IndexOf(';', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = MetadataAttachmentPattern().Replace(line, string.Empty);
            line = RangeAttachmentPattern().Replace(line, string.Empty);
            line = AttributeGroupPattern().Replace(line, string.Empty);
            line = ParameterizedAttributePattern().Replace(line, string.Empty);

            foreach (var token in StrippedTokens)
            {
                line = Regex.Replace(line, $@"\b{token}\b ?", string.Empty);
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var label = BlockLabelPattern().Match(line);
            if (label.Success)
            {
                line = $"{Rename(renames, label.Groups[1].Value)}:";
            }
            else
            {
                line = RegisterPattern().Replace(line, match => $"%{Rename(renames, match.Value[1..])}");
            }

            line = WhitespaceRunPattern().Replace(line, " ");
            line = line.Replace(" ,", ",", StringComparison.Ordinal).Replace("( ", "(", StringComparison.Ordinal);
            normalized.Add(line);
        }

        return string.Join('\n', normalized);
    }

    private static string Rename(Dictionary<string, string> renames, string original)
    {
        // Struct type references (%Item) share the sigil with registers; type
        // names are stable across stages, so leave capitalized non-numeric
        // names that look like types untouched only when they appear in type
        // position is unknowable lexically — renumber everything uniformly
        // instead, which stays consistent because both sides rename the same
        // way in first-appearance order.
        if (!renames.TryGetValue(original, out var renamed))
        {
            renamed = $"x{renames.Count}";
            renames[original] = renamed;
        }

        return renamed;
    }
}
