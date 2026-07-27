using System.Text.RegularExpressions;

namespace Stark.Compiler;

public enum LlvmExternalVerifyStatus
{
    Verified,
    Failed,
    ToolUnavailable
}

public readonly record struct LlvmExternalVerifyResult(LlvmExternalVerifyStatus Status, string Detail);

/// <summary>
/// Mechanical invariant checks over emitted LLVM module text. Every lint here
/// encodes a bug class that once shipped and was only caught far downstream:
/// <c>dereferenceable(0)</c> is rejected by LLVM itself, and a call whose
/// <c>!noalias</c> set contains its own result's fresh scope licenses the
/// optimizer to forward stale pre-call memory over the call's own write.
/// Cheap text scans, run at emission time so the bug surfaces where it is
/// made instead of as distant runtime misbehavior.
/// </summary>
public static partial class LlvmModuleLint
{
    [GeneratedRegex(@"dereferenceable(?:_or_null)?\(0\)")]
    private static partial Regex ZeroDereferenceablePattern();

    [GeneratedRegex(@"initializes\(((?:\(\s*-?\d+\s*,\s*-?\d+\s*\)\s*,?\s*)+)\)")]
    private static partial Regex InitializesPattern();

    [GeneratedRegex(@"\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)")]
    private static partial Regex InitializesRangePattern();

    [GeneratedRegex(@"^!(\d+) = (?:distinct )?!\{(.*)\}\s*$")]
    private static partial Regex MetadataNodePattern();

    [GeneratedRegex(@"!""([^""]*)""")]
    private static partial Regex MetadataStringPattern();

    [GeneratedRegex(@"!(\d+)")]
    private static partial Regex MetadataReferencePattern();

    [GeneratedRegex(@"!noalias !(\d+)")]
    private static partial Regex CallNoAliasPattern();

    [GeneratedRegex(@"^\s*%([A-Za-z0-9_.$]+) = ")]
    private static partial Regex CallResultPattern();

    [GeneratedRegex(@"sret\([^)]*\)[^%,)]*%([A-Za-z0-9_.$]+)")]
    private static partial Regex SretArgumentPattern();

    private const string FreshScopeMarker = ".fresh.";

    /// <summary>
    /// True when emitted modules should be linted: always in debug builds
    /// (which is what test suites run), overridable either way with
    /// STARK_LLVM_LINT=1/0.
    /// </summary>
    public static bool ShouldRun => Environment.GetEnvironmentVariable("STARK_LLVM_LINT") switch
    {
        "0" => false,
        "1" => true,
        _ => IsDebugBuild
    };

    /// <summary>
    /// True when emitted modules should additionally round-trip through the
    /// real LLVM verifier (`opt -passes=verify`). Opt-in via
    /// STARK_LLVM_VERIFY=1: a subprocess per emitted module is too costly for
    /// the default inner test loop but right for CI and harness runs.
    /// </summary>
    public static bool ShouldExternalVerify =>
        Environment.GetEnvironmentVariable("STARK_LLVM_VERIFY") == "1";

    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public static IReadOnlyList<string> Check(string moduleText)
    {
        var violations = new List<string>();
        var lines = moduleText.Split('\n');

        CheckAttributeExtents(lines, violations);
        CheckFreshResultScopes(moduleText, lines, violations);

        return violations;
    }

    private static void CheckAttributeExtents(string[] lines, List<string> violations)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (ZeroDereferenceablePattern().IsMatch(line))
            {
                violations.Add($"dereferenceable(0) is invalid LLVM (line {index + 1}): {line.Trim()}");
            }

            foreach (Match initializes in InitializesPattern().Matches(line))
            {
                foreach (Match range in InitializesRangePattern().Matches(initializes.Groups[1].Value))
                {
                    var low = long.Parse(range.Groups[1].Value);
                    var high = long.Parse(range.Groups[2].Value);
                    if (low >= high)
                    {
                        violations.Add($"initializes range ({low}, {high}) is empty or inverted (line {index + 1}): {line.Trim()}");
                    }
                }
            }
        }
    }

    private static void CheckFreshResultScopes(string moduleText, string[] lines, List<string> violations)
    {
        if (!moduleText.Contains(FreshScopeMarker, StringComparison.Ordinal))
        {
            return;
        }

        var nodeBodies = new Dictionary<int, string>();
        var freshScopeRegisters = new Dictionary<int, string>();

        foreach (var line in lines)
        {
            var node = MetadataNodePattern().Match(line);
            if (!node.Success)
            {
                continue;
            }

            var id = int.Parse(node.Groups[1].Value);
            var body = node.Groups[2].Value;
            nodeBodies[id] = body;

            foreach (Match text in MetadataStringPattern().Matches(body))
            {
                var name = text.Groups[1].Value;
                var marker = name.LastIndexOf(FreshScopeMarker, StringComparison.Ordinal);
                if (marker >= 0)
                {
                    freshScopeRegisters[id] = name[(marker + FreshScopeMarker.Length)..];
                }
            }
        }

        if (freshScopeRegisters.Count == 0)
        {
            return;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.Contains("call ", StringComparison.Ordinal))
            {
                continue;
            }

            var noAlias = CallNoAliasPattern().Match(line);
            if (!noAlias.Success)
            {
                continue;
            }

            var resultRegisters = new List<string>();
            var directResult = CallResultPattern().Match(line);
            if (directResult.Success)
            {
                resultRegisters.Add(directResult.Groups[1].Value);
            }

            var sretArgument = SretArgumentPattern().Match(line);
            if (sretArgument.Success)
            {
                resultRegisters.Add(sretArgument.Groups[1].Value);
            }

            if (resultRegisters.Count == 0)
            {
                continue;
            }

            var listId = int.Parse(noAlias.Groups[1].Value);
            foreach (var scopeId in ResolveScopeMembers(listId, nodeBodies))
            {
                if (freshScopeRegisters.TryGetValue(scopeId, out var freshRegister)
                    && resultRegisters.Contains(freshRegister))
                {
                    violations.Add(
                        $"call !noalias list !{listId} contains the fresh scope of its own result %{freshRegister} "
                        + $"(scope !{scopeId}) — LLVM may forward stale pre-call memory over the call's write (line {index + 1}): {line.Trim()}");
                }
            }
        }
    }

    private static IEnumerable<int> ResolveScopeMembers(int listId, Dictionary<int, string> nodeBodies)
    {
        if (!nodeBodies.TryGetValue(listId, out var body))
        {
            yield break;
        }

        yield return listId;
        foreach (Match reference in MetadataReferencePattern().Matches(body))
        {
            yield return int.Parse(reference.Groups[1].Value);
        }
    }

    /// <summary>
    /// Round-trips the module through `opt -passes=verify`. Tool discovery:
    /// STARK_LLVM_OPT, else `opt` on PATH.
    /// </summary>
    public static LlvmExternalVerifyResult ExternalVerify(string moduleText)
    {
        var optTool = Environment.GetEnvironmentVariable("STARK_LLVM_OPT");
        if (string.IsNullOrWhiteSpace(optTool))
        {
            optTool = "opt";
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = optTool,
                Arguments = "-passes=verify -disable-output -",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            process.Start();
            process.StandardInput.Write(moduleText);
            process.StandardInput.Close();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                ? new LlvmExternalVerifyResult(LlvmExternalVerifyStatus.Verified, string.Empty)
                : new LlvmExternalVerifyResult(LlvmExternalVerifyStatus.Failed, standardError.Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new LlvmExternalVerifyResult(LlvmExternalVerifyStatus.ToolUnavailable, $"'{optTool}' was not found");
        }
    }
}
