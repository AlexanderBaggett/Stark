using System.Security.Cryptography;

namespace Stark.Compiler;

internal static class SdkIntegrityValidator
{
    public static bool ValidateFileChecksum(
        string packageId,
        string artifactLabel,
        string path,
        string? expectedSha256,
        string mismatchCode,
        List<SdkDiagnostic> diagnostics)
    {
        if (expectedSha256 is null)
        {
            return true;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            var actualDigest = SHA256.HashData(stream);
            var expectedDigest = Convert.FromHexString(expectedSha256);
            if (CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                return true;
            }

            diagnostics.Add(new SdkDiagnostic(
                mismatchCode,
                $"SDK package '{packageId}' {artifactLabel} checksum mismatch: expected {expectedSha256}, found {Convert.ToHexString(actualDigest).ToLowerInvariant()}.",
                path));
            return false;
        }
        catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new SdkDiagnostic(
                "STK7468",
                $"SDK package '{packageId}' {artifactLabel} checksum could not be calculated: {exception.Message}",
                path));
            return false;
        }
    }

    public static void ValidateNativePaths(
        SdkPackageIndex packageIndex,
        SdkPackageDescriptor package,
        List<SdkDiagnostic> diagnostics)
    {
        ValidateFiles(
            packageIndex,
            package.Id,
            package.Native.ArtifactPaths,
            "native artifact",
            "STK7470",
            diagnostics);
        ValidateDirectories(
            packageIndex,
            package.Id,
            package.Native.IncludeDirectories,
            "native include directory",
            "STK7471",
            diagnostics);
        ValidateDirectories(
            packageIndex,
            package.Id,
            package.Native.LibraryDirectories,
            "native library directory",
            "STK7472",
            diagnostics);
        ValidateFiles(
            packageIndex,
            package.Id,
            package.Native.RuntimeFiles,
            "native runtime file",
            "STK7473",
            diagnostics);
        ValidateFiles(
            packageIndex,
            package.Id,
            package.Native.LicenseFiles,
            "native license file",
            "STK7474",
            diagnostics);

        foreach (var checksum in package.Native.FileChecksums.OrderBy(static checksum => checksum.Path, StringComparer.Ordinal))
        {
            if (!SdkManifestPathValidator.TryResolvePath(
                    packageIndex.SdkRoot,
                    checksum.Path,
                    out var path,
                    out _))
            {
                // The manifest loader requires every checksum path to belong
                // to one of the native file declarations. Its kind-specific
                // validation above already emitted the precise diagnostic.
                continue;
            }

            if (!File.Exists(path))
            {
                // Every checksum path is tied to at least one file declaration by
                // the manifest loader, which already emitted the kind-specific
                // missing-file diagnostic above.
                continue;
            }

            ValidateFileChecksum(
                package.Id,
                $"native file '{checksum.Path}'",
                path,
                checksum.Sha256,
                "STK7475",
                diagnostics);
        }
    }

    private static void ValidateFiles(
        SdkPackageIndex packageIndex,
        string packageId,
        IReadOnlyList<string> relativePaths,
        string label,
        string code,
        List<SdkDiagnostic> diagnostics)
    {
        foreach (var relativePath in relativePaths.Order(StringComparer.Ordinal))
        {
            if (!TryResolveDeclaredPath(
                    packageIndex,
                    packageId,
                    relativePath,
                    label,
                    code,
                    diagnostics,
                    out var path))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                diagnostics.Add(new SdkDiagnostic(
                    code,
                    $"SDK package '{packageId}' {label} is missing or is not a file: '{path}'.",
                    path));
            }
        }
    }

    private static void ValidateDirectories(
        SdkPackageIndex packageIndex,
        string packageId,
        IReadOnlyList<string> relativePaths,
        string label,
        string code,
        List<SdkDiagnostic> diagnostics)
    {
        foreach (var relativePath in relativePaths.Order(StringComparer.Ordinal))
        {
            if (!TryResolveDeclaredPath(
                    packageIndex,
                    packageId,
                    relativePath,
                    label,
                    code,
                    diagnostics,
                    out var path))
            {
                continue;
            }

            if (!Directory.Exists(path))
            {
                diagnostics.Add(new SdkDiagnostic(
                    code,
                    $"SDK package '{packageId}' {label} is missing or is not a directory: '{path}'.",
                    path));
            }
        }
    }

    private static bool TryResolveDeclaredPath(
        SdkPackageIndex packageIndex,
        string packageId,
        string relativePath,
        string label,
        string code,
        List<SdkDiagnostic> diagnostics,
        out string path)
    {
        if (SdkManifestPathValidator.TryResolvePath(
                packageIndex.SdkRoot,
                relativePath,
                out path,
                out var resolutionError))
        {
            return true;
        }

        diagnostics.Add(new SdkDiagnostic(
            code,
            $"SDK package '{packageId}' {label} path '{relativePath}' could not be resolved safely: {resolutionError}",
            packageIndex.SdkRoot));
        return false;
    }
}
