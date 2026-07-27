using System.Globalization;
using System.Text;

namespace Stark.Compiler;

/// <summary>
/// Renders the decoded SDK contract as a stable, line-oriented fact summary.
/// This is a cross-stage compatibility/debugging surface, not a persisted SDK
/// format or a cryptographic identity encoding.
/// </summary>
internal static class SdkManifestNormalization
{
    public static string RenderSummary(SdkManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var builder = new StringBuilder();
        AppendRow(
            builder,
            "manifest",
            manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            RenderKind(manifest.Kind),
            manifest.SdkVersion,
            manifest.CompilerCompatibility,
            manifest.PackageFormatVersion.ToString(CultureInfo.InvariantCulture));

        var target = manifest.Target;
        AppendRow(
            builder,
            "target",
            target.Id,
            target.LlvmTriple,
            target.Architecture,
            target.OperatingSystem,
            target.Abi,
            target.PointerBitWidth.ToString(CultureInfo.InvariantCulture),
            target.Endianness == SdkEndianness.Little ? "little" : "big",
            target.DataLayout,
            target.BaselineCpu,
            target.RelocationModel,
            target.CodeModel,
            target.CDataModel,
            target.MinimumOperatingSystemVersion);
        foreach (var feature in target.BaselineFeatures)
        {
            AppendRow(builder, "feature", feature);
        }

        foreach (var module in manifest.Modules)
        {
            AppendRow(builder, "module", module.ModuleName, module.PackageId);
        }

        foreach (var package in manifest.Packages)
        {
            AppendRow(
                builder,
                "package",
                package.Id,
                package.Version,
                package.Profile,
                package.ImagePath,
                package.LibraryPath,
                package.ApiHash,
                package.ContentHash,
                package.ImageSha256,
                package.LibrarySha256);
            foreach (var dependency in package.Dependencies)
            {
                AppendRow(
                    builder,
                    "dependency",
                    package.Id,
                    dependency.PackageId,
                    dependency.ApiHash,
                    dependency.ContentHash);
            }

            foreach (var path in package.Native.ArtifactPaths)
            {
                AppendRow(builder, "native-artifact", package.Id, path);
            }

            foreach (var path in package.Native.IncludeDirectories)
            {
                AppendRow(builder, "native-include", package.Id, path);
            }

            foreach (var path in package.Native.LibraryDirectories)
            {
                AppendRow(builder, "native-library-directory", package.Id, path);
            }

            foreach (var path in package.Native.RuntimeFiles)
            {
                AppendRow(builder, "native-runtime", package.Id, path);
            }

            foreach (var path in package.Native.LicenseFiles)
            {
                AppendRow(builder, "native-license", package.Id, path);
            }

            foreach (var checksum in package.Native.FileChecksums)
            {
                AppendRow(builder, "native-checksum", package.Id, checksum.Path, checksum.Sha256);
            }

            foreach (var library in package.Native.Libraries)
            {
                AppendRow(builder, "native-library", package.Id, library);
            }

            foreach (var argument in package.Native.LinkArguments)
            {
                AppendRow(builder, "native-link-argument", package.Id, argument);
            }
        }

        foreach (var sourceRoot in manifest.DevelopmentSourceRoots)
        {
            AppendRow(builder, "source-root", sourceRoot);
        }

        return builder.ToString();
    }

    private static string RenderKind(SdkDistributionKind kind) => kind switch
    {
        SdkDistributionKind.Release => "release",
        SdkDistributionKind.Development => "development",
        SdkDistributionKind.Stage => "stage",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SDK distribution kind.")
    };

    private static void AppendRow(StringBuilder builder, params string?[] fields)
    {
        for (var index = 0; index < fields.Length; index++)
        {
            if (index != 0)
            {
                builder.Append('|');
            }

            builder.Append(fields[index]);
        }

        builder.Append('\n');
    }
}
