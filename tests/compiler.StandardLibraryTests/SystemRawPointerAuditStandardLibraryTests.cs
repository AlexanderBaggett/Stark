namespace compiler.StandardLibraryTests;

public sealed class SystemRawPointerAuditStandardLibraryTests : StandardLibraryTestSuite
{
    private static readonly string[] DocumentedRawPointerFiles =
    [
        "stdlib/src/System/C.stark",
        "stdlib/src/System/Collections.stark",
        "stdlib/src/System/Console.stark",
        "stdlib/src/System/Cryptography/Sha256.stark",
        "stdlib/src/System/FileSystem.stark",
        "stdlib/src/System/IO/File.stark",
        "stdlib/src/System/IO/Path.stark",
        "stdlib/src/System/Json.stark",
        "stdlib/src/System/Memory.stark",
        "stdlib/src/System/Net/Tcp.stark",
        "stdlib/src/System/Process.stark",
        "stdlib/src/System/Runtime.stark",
        "stdlib/src/System/Runtime/Buffer.stark",
        "stdlib/src/System/Runtime/ConsoleInput.stark",
        "stdlib/src/System/Runtime/Platform.stark",
        "stdlib/src/System/Runtime/Platform/Linux.stark",
        "stdlib/src/System/Runtime/Platform/MacOS.stark",
        "stdlib/src/System/Runtime/Platform/Windows.stark",
        "stdlib/src/System/Testing/HostCompiler.stark",
        "stdlib/src/System/Text.stark",
        "stdlib/src/System/Threading.stark",
        "stdlib/src/System/Toml.stark"
    ];

    private static readonly string[] PublicRawPointerSurfaceFiles =
    [
        "stdlib/src/System/C.stark",
        "stdlib/src/System/Memory.stark",
        "stdlib/src/System/Text.stark"
    ];

    private static readonly string[] SystemMemoryAllowedPublicRawPointerPrefixes =
    [
        "public unsafe inline finite MemoryStatus InitializeUnsignedBytesFromPointerDisjoint("
    ];

    private static readonly string[] SystemTextAllowedPublicRawPointerPrefixes =
    [
        "public unsafe finite bool TryConcat",
        "public unsafe finite bool TryFormat",
        "public unsafe fn bool TryFormat"
    ];

    [Fact]
    public void StdLibRawPointerUseStaysInDocumentedBoundaryFiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stdlibRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var allowedFiles = new HashSet<string>(DocumentedRawPointerFiles, StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(stdlibRoot, "*.stark", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(repositoryRoot, path);
            var source = StripCommentsAndStrings(File.ReadAllText(path));
            if (!ContainsRawPointerToken(source) || allowedFiles.Contains(relativePath))
            {
                continue;
            }

            violations.Add($"{relativePath}: raw pointer use is not documented in docs/Internals/StandardLibraryRawPointerBoundaries.md.");
        }

        Assert.True(
            violations.Count == 0,
            "Standard-library raw pointer use must stay inside documented FFI, platform, runtime, or audited unsafe boundaries."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void StdLibPublicRawPointerSurfaceStaysExplicitlyUnsafeAndAllowlisted()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stdlibRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var allowedFiles = new HashSet<string>(PublicRawPointerSurfaceFiles, StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(stdlibRoot, "*.stark", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(repositoryRoot, path);
            var lines = StripCommentsAndStrings(File.ReadAllText(path)).Split('\n');

            foreach (var declaration in EnumeratePublicSurfaceDeclarations(lines))
            {
                if (!ContainsRawPointerToken(declaration.Text))
                {
                    continue;
                }

                if (!allowedFiles.Contains(relativePath))
                {
                    violations.Add($"{relativePath}:{declaration.Line}: public raw pointer surface is not allowlisted: {declaration.Text.Trim()}");
                    continue;
                }

                if (!ContainsUnsafeToken(declaration.Text))
                {
                    violations.Add($"{relativePath}:{declaration.Line}: public raw pointer surface must be explicitly unsafe: {declaration.Text.Trim()}");
                    continue;
                }

                if (relativePath.Equals("stdlib/src/System/Text.stark", StringComparison.OrdinalIgnoreCase)
                    && !SystemTextAllowedPublicRawPointerPrefixes.Any(prefix => declaration.Text.TrimStart().StartsWith(prefix, StringComparison.Ordinal)))
                {
                    violations.Add($"{relativePath}:{declaration.Line}: System.Text public raw pointer surface must stay limited to fixed-buffer concat/format helpers: {declaration.Text.Trim()}");
                }

                if (relativePath.Equals("stdlib/src/System/Memory.stark", StringComparison.OrdinalIgnoreCase)
                    && !SystemMemoryAllowedPublicRawPointerPrefixes.Any(prefix => declaration.Text.TrimStart().StartsWith(prefix, StringComparison.Ordinal)))
                {
                    violations.Add($"{relativePath}:{declaration.Line}: System.Memory public raw pointer surface must stay limited to the bounded native-byte import helper: {declaration.Text.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Public standard-library APIs should expose raw pointers only in explicitly unsafe low-level compatibility or compiler-known surfaces."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void StdLibRootDoesNotReExportPublicRawPointerSurfaceModules()
    {
        var repositoryRoot = FindRepositoryRoot();
        var systemSource = File.ReadAllText(Path.Combine(repositoryRoot, "stdlib", "src", "System.stark"));

        Assert.DoesNotContain("export import System.Text", systemSource, StringComparison.Ordinal);
        Assert.Contains("export import System.IO.File", systemSource, StringComparison.Ordinal);
        Assert.Contains("export import System.Collections", systemSource, StringComparison.Ordinal);
    }

    private static IEnumerable<(int Line, string Text)> EnumeratePublicSurfaceDeclarations(string[] lines)
    {
        var depth = 0;
        var publicStructDepths = new Stack<int>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var trimmed = line.Trim();

            while (publicStructDepths.Count > 0 && depth < publicStructDepths.Peek())
            {
                publicStructDepths.Pop();
            }

            if (trimmed.StartsWith("public ", StringComparison.Ordinal))
            {
                yield return (index + 1, CollectDeclaration(lines, index));
            }

            if (trimmed.StartsWith("public struct ", StringComparison.Ordinal) && trimmed.Contains('{', StringComparison.Ordinal))
            {
                publicStructDepths.Push(depth + 1);
            }
            else if (publicStructDepths.Count > 0
                && depth == publicStructDepths.Peek()
                && !trimmed.StartsWith("internal ", StringComparison.Ordinal)
                && ContainsRawPointerToken(trimmed)
                && LooksLikeMemberDeclaration(trimmed))
            {
                yield return (index + 1, CollectDeclaration(lines, index));
            }

            depth += CountBraceDelta(line);
        }
    }

    private static string CollectDeclaration(string[] lines, int startIndex)
    {
        var parts = new List<string>();
        for (var index = startIndex; index < lines.Length; index++)
        {
            var part = lines[index].Trim();
            parts.Add(part);
            if (part.Contains(';', StringComparison.Ordinal) || part.Contains('{', StringComparison.Ordinal))
            {
                break;
            }
        }

        return string.Join(" ", parts);
    }

    private static bool LooksLikeMemberDeclaration(string trimmed)
    {
        return trimmed.Contains(" fn ", StringComparison.Ordinal)
            || trimmed.StartsWith("fn ", StringComparison.Ordinal)
            || trimmed.StartsWith("unsafe fn ", StringComparison.Ordinal)
            || trimmed.Contains(" law ", StringComparison.Ordinal)
            || trimmed.EndsWith(';');
    }

    private static bool ContainsRawPointerToken(string text)
    {
        return text.Contains("rawptr<", StringComparison.Ordinal)
            || text.Contains("rawmutptr<", StringComparison.Ordinal);
    }

    private static bool ContainsUnsafeToken(string text)
    {
        return text.Split([' ', '\t', '\r', '\n', '(', ')', '{', '}', ';', '<', '>', ','], StringSplitOptions.RemoveEmptyEntries)
            .Contains("unsafe", StringComparer.Ordinal);
    }

    private static int CountBraceDelta(string line)
    {
        var delta = 0;
        foreach (var character in line)
        {
            if (character == '{')
            {
                delta++;
            }
            else if (character == '}')
            {
                delta--;
            }
        }

        return delta;
    }

    private static string NormalizeRelativePath(string repositoryRoot, string path)
    {
        return Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string StripCommentsAndStrings(string source)
    {
        var chars = source.ToCharArray();
        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var index = 0; index < chars.Length; index++)
        {
            if (inLineComment)
            {
                if (chars[index] == '\r' || chars[index] == '\n')
                {
                    inLineComment = false;
                }
                else
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inBlockComment)
            {
                if (chars[index] == '*' && index + 1 < chars.Length && chars[index + 1] == '/')
                {
                    chars[index] = ' ';
                    chars[++index] = ' ';
                    inBlockComment = false;
                    continue;
                }

                if (chars[index] != '\r' && chars[index] != '\n')
                {
                    chars[index] = ' ';
                }

                continue;
            }

            if (inString)
            {
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

                continue;
            }

            if (chars[index] == '/' && index + 1 < chars.Length && chars[index + 1] == '/')
            {
                chars[index] = ' ';
                chars[++index] = ' ';
                inLineComment = true;
                continue;
            }

            if (chars[index] == '/' && index + 1 < chars.Length && chars[index + 1] == '*')
            {
                chars[index] = ' ';
                chars[++index] = ' ';
                inBlockComment = true;
                continue;
            }

            if (chars[index] == '"')
            {
                chars[index] = ' ';
                inString = true;
            }
        }

        return new string(chars);
    }
}
