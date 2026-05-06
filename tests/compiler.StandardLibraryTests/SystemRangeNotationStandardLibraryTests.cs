using System.Text.RegularExpressions;

namespace compiler.StandardLibraryTests;

public sealed class SystemRangeNotationStandardLibraryTests : StandardLibraryTestSuite
{
    private static readonly string[] ForbiddenRangeSpellings =
    [
        "i8[-128 127]",
        "i16[-32768 32767]",
        "i24[-8388608 8388607]",
        "i32[-2147483648 2147483647]",
        "i48[-140737488355328 140737488355327]",
        "i64[-9223372036854775808 9223372036854775807]",
        "u8[0 255]",
        "u16[0 65535]",
        "u16[0 8192]",
        "u8[0 127]"
    ];

    private static readonly Regex ForbiddenBoundaryLiteralPattern = new(
        @"(?<![\w])(?:-9223372036854775808|9223372036854775807|-2147483648|2147483647|-140737488355328|140737488355327|-8388608|8388607|-32768|32767|65535|65536)(?![\w])",
        RegexOptions.Compiled);

    private static readonly Regex ParenthesizedPositivePowerPattern = new(
        @"(?<![\)-])\(\s*2\s*\*\*\s*\d+\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex CompactExponentPattern = new(
        @"(?<![\w])2\*\*\d+(?![\w])",
        RegexOptions.Compiled);

    [Fact]
    public void StdLibSourceUsesCanonicalRangeNotation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stdlibRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(stdlibRoot, "*.stark", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, path);
            var source = File.ReadAllText(path);
            var codeOnly = StripStringLiterals(source);

            foreach (var forbiddenRange in ForbiddenRangeSpellings)
            {
                if (codeOnly.Contains(forbiddenRange, StringComparison.Ordinal))
                {
                    failures.Add($"{relativePath}: replace `{forbiddenRange}` with `[min max]`, `[0 max]`, or exponent notation.");
                }
            }

            foreach (Match match in ForbiddenBoundaryLiteralPattern.Matches(codeOnly))
            {
                failures.Add($"{relativePath}: replace decimal boundary literal `{match.Value}` with exponent notation.");
            }

            foreach (Match match in ParenthesizedPositivePowerPattern.Matches(codeOnly))
            {
                failures.Add($"{relativePath}: remove unnecessary parentheses around exponent `{match.Value}`.");
            }

            foreach (Match match in CompactExponentPattern.Matches(codeOnly))
            {
                failures.Add($"{relativePath}: write exponent `{match.Value}` with spaces as `2 ** n`.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string StripStringLiterals(string source)
    {
        var chars = source.ToCharArray();
        var inString = false;
        for (var index = 0; index < chars.Length; index++)
        {
            if (!inString)
            {
                if (chars[index] == '"')
                {
                    inString = true;
                    chars[index] = ' ';
                }

                continue;
            }

            if (chars[index] == '\\' && index + 1 < chars.Length)
            {
                chars[index] = ' ';
                chars[++index] = ' ';
                continue;
            }

            if (chars[index] == '"')
            {
                inString = false;
            }

            if (chars[index] != '\r' && chars[index] != '\n')
            {
                chars[index] = ' ';
            }
        }

        return new string(chars);
    }
}
