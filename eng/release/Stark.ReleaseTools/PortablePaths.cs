namespace Stark.ReleaseTools;

internal static class PortablePaths
{
    public const int MaximumPathBytes = 4096;

    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            .. Enumerable.Range(1, 9).Select(index => $"COM{index}"),
            .. Enumerable.Range(1, 9).Select(index => $"LPT{index}"),
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string Validate(string path, string label, bool trimDirectorySlash = false, bool allowDotRoot = false)
    {
        if (trimDirectorySlash)
        {
            path = path.TrimEnd('/');
        }

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (allowDotRoot && path == ".")
        {
            return path;
        }

        if (string.IsNullOrEmpty(path) || path[0] == '/' || path.Contains('\\') || path.Contains(':') || path.Contains('\0') || !path.All(character => character <= 0x7f))
        {
            throw new ReleaseToolException($"{label} has non-portable relative path '{path}'.");
        }

        if (System.Text.Encoding.ASCII.GetByteCount(path) > MaximumPathBytes)
        {
            throw new ReleaseToolException($"{label} path '{path}' exceeds {MaximumPathBytes} bytes.");
        }

        foreach (var segment in path.Split('/'))
        {
            ValidateSegment(segment, label, path);
        }

        return path;
    }

    public static void ValidateSegment(string segment, string label, string path)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or "..")
        {
            throw new ReleaseToolException($"{label} has an empty or traversal segment in '{path}'.");
        }

        if (!segment.All(character => character <= 0x7f) || System.Text.Encoding.ASCII.GetByteCount(segment) > 255)
        {
            throw new ReleaseToolException($"{label} has a non-ASCII or oversized segment in '{path}'.");
        }

        if (segment[^1] is ' ' or '.')
        {
            throw new ReleaseToolException($"{label} has a Windows-ambiguous segment in '{path}'.");
        }

        if (segment.Any(character => character < 0x20 || character == 0x7f || "<>\"|?*".Contains(character)))
        {
            throw new ReleaseToolException($"{label} has a non-portable character in '{path}'.");
        }

        if (WindowsReservedNames.Contains(segment.Split('.', 2)[0]))
        {
            throw new ReleaseToolException($"{label} has a reserved Windows segment in '{path}'.");
        }
    }

    public static string ResolveLinkTarget(string linkPath, string target, string requiredRoot, string label)
    {
        if (string.IsNullOrEmpty(target) || target[0] == '/' || target.Contains('\\') || target.Contains(':') || target.Contains('\0') || !target.All(character => character <= 0x7f))
        {
            throw new ReleaseToolException($"{label} symbolic link '{linkPath}' has unsafe target '{target}'.");
        }

        var parts = linkPath.Split('/').SkipLast(1).ToList();
        foreach (var segment in target.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0 || (!string.IsNullOrEmpty(requiredRoot) && parts.Count <= 1))
                {
                    throw new ReleaseToolException($"{label} symbolic link '{linkPath}' escapes through '{target}'.");
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            ValidateSegment(segment, $"{label} symbolic-link target", target);
            parts.Add(segment);
        }

        if (parts.Count == 0)
        {
            throw new ReleaseToolException($"{label} symbolic link '{linkPath}' resolves to the extraction root.");
        }

        var resolved = string.Join('/', parts);
        if (!string.IsNullOrEmpty(requiredRoot) && resolved != requiredRoot && !resolved.StartsWith(requiredRoot + "/", StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"{label} symbolic link '{linkPath}' resolves outside required root '{requiredRoot}'.");
        }

        return resolved;
    }

    public static string SafeDestination(string root, string portablePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, portablePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ReleaseToolException($"Archive path '{portablePath}' escapes '{root}'.");
        }

        return destination;
    }
}
