namespace Stark.Compiler;

internal sealed class SdkPackageIndex
{
    private readonly SortedDictionary<string, SdkPackageDescriptor> _packages;
    private readonly SortedDictionary<string, SdkModuleOwnership> _modules;

    internal SdkPackageIndex(
        string sdkRoot,
        IEnumerable<SdkPackageDescriptor> packages,
        IEnumerable<SdkModuleOwnership> modules,
        int packageFormatVersion = (int)PackageImageBinaryFormat.CurrentFormatVersion)
    {
        SdkRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot);
        PackageFormatVersion = packageFormatVersion;
        _packages = new SortedDictionary<string, SdkPackageDescriptor>(StringComparer.Ordinal);
        _modules = new SortedDictionary<string, SdkModuleOwnership>(StringComparer.Ordinal);

        foreach (var package in packages)
        {
            _packages.Add(package.Id, package);
        }

        foreach (var module in modules)
        {
            _modules.Add(module.ModuleName, module);
        }

        Packages = _packages.Values.ToArray();
        Modules = _modules.Values.ToArray();
    }

    public string SdkRoot { get; }

    public int PackageFormatVersion { get; }

    public IReadOnlyList<SdkPackageDescriptor> Packages { get; }

    public IReadOnlyList<SdkModuleOwnership> Modules { get; }

    public bool TryGetPackage(string packageId, out SdkPackageDescriptor package)
    {
        return _packages.TryGetValue(packageId, out package!);
    }

    public bool TryGetPackageForModule(
        string moduleName,
        out SdkPackageDescriptor package,
        out SdkModuleOwnership ownership)
    {
        if (_modules.TryGetValue(moduleName, out ownership!)
            && _packages.TryGetValue(ownership.PackageId, out package!))
        {
            return true;
        }

        package = default!;
        ownership = default!;
        return false;
    }

    public string ResolvePath(string relativePath)
    {
        return SdkManifestPathValidator.ResolvePath(SdkRoot, relativePath);
    }

    public string GetPackageImagePath(SdkPackageDescriptor package) => ResolvePath(package.ImagePath);

    public string? GetPackageLibraryPath(SdkPackageDescriptor package) =>
        package.LibraryPath is null ? null : ResolvePath(package.LibraryPath);
}

internal static class SdkManifestPathValidator
{
    public static bool TryValidate(
        string sdkRoot,
        string? relativePath,
        string label,
        List<SdkDiagnostic> diagnostics,
        string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7410",
                $"SDK {label} path must not be empty.",
                manifestPath));
            return false;
        }

        if (!string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7415",
                $"SDK {label} path '{relativePath}' must not contain leading or trailing whitespace.",
                manifestPath));
            return false;
        }

        if (relativePath.Contains('\\'))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7411",
                $"SDK {label} path '{relativePath}' must use forward slashes.",
                manifestPath));
            return false;
        }

        if (Path.IsPathRooted(relativePath)
            || relativePath.StartsWith("/", StringComparison.Ordinal)
            || (relativePath.Length >= 2
                && char.IsAsciiLetter(relativePath[0])
                && relativePath[1] == ':'))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7412",
                $"SDK {label} path '{relativePath}' must be relative to the SDK root.",
                manifestPath));
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment => segment is "" or "." or ".."))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7413",
                $"SDK {label} path '{relativePath}' contains an empty, current-directory, or parent-directory segment.",
                manifestPath));
            return false;
        }

        if (!TryResolvePath(sdkRoot, relativePath, out _, out var resolutionError))
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7414",
                $"SDK {label} path '{relativePath}' is invalid: {resolutionError}",
                manifestPath));
            return false;
        }

        return true;
    }

    public static bool TryResolvePath(
        string sdkRoot,
        string relativePath,
        out string resolvedPath,
        out string errorMessage)
    {
        try
        {
            resolvedPath = ResolvePath(sdkRoot, relativePath);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            resolvedPath = string.Empty;
            errorMessage = exception.Message;
            return false;
        }
    }

    public static string ResolvePath(string sdkRoot, string relativePath)
    {
        var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot);
        var resolved = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.EndsInDirectorySeparator(canonicalRoot)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolved.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException(
                $"SDK path '{relativePath}' resolves outside SDK root '{canonicalRoot}'.",
                nameof(relativePath));
        }

        // Lexical containment is not sufficient for an installed SDK. A
        // package path such as `vendor/dist/native/libraylib.a` could traverse
        // a child symlink whose target is outside the SDK even though its text
        // contains no parent segment. Release SDKs do not need child links for
        // their indexed artifacts, so reject every existing reparse/symlink
        // component. This is repeated whenever a path is resolved, which also
        // closes the gap between manifest loading and later package use.
        RejectLinkedPathComponents(canonicalRoot, relativePath);

        return resolved;
    }

    private static void RejectLinkedPathComponents(string canonicalRoot, string relativePath)
    {
        var currentPath = canonicalRoot;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!IsSymbolicLinkOrReparsePoint(currentPath))
            {
                continue;
            }

            throw new ArgumentException(
                $"SDK path '{relativePath}' traverses symbolic link or reparse point '{currentPath}'.",
                nameof(relativePath));
        }
    }

    private static bool IsSymbolicLinkOrReparsePoint(string path)
    {
        FileSystemInfo item = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        try
        {
            item.Refresh();
            if (item.LinkTarget is not null)
            {
                return true;
            }

            return item.Exists
                && (item.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            // A not-yet-created component cannot redirect path traversal. A
            // later resolution rechecks it before any indexed artifact use.
            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                $"SDK path component '{path}' could not be inspected safely: {exception.Message}",
                nameof(path),
                exception);
        }
    }
}
