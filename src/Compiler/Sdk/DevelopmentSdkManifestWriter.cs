using System.Runtime.InteropServices;
using System.Text.Json;

namespace Stark.Compiler;

/// <summary>
/// Writes the source-only SDK manifest used by the repository launcher. This
/// is deliberately separate from release assembly: it describes the current
/// host and opts into only the repository source roots.
/// </summary>
internal static class DevelopmentSdkManifestWriter
{
    public const string CommandOption = "--write-development-sdk-manifest";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static bool TryWrite(string sdkRoot, out string manifestPath, out string error)
    {
        manifestPath = string.Empty;
        error = string.Empty;

        try
        {
            var canonicalRoot = SdkRootResolver.CanonicalizeRootPath(sdkRoot);
            Directory.CreateDirectory(canonicalRoot);
            if (!TryCreateHostTarget(out var target, out error))
            {
                return false;
            }

            var document = new DevelopmentSdkDocument(
                SchemaVersion: SdkManifestLoader.SupportedSchemaVersion,
                Kind: "development",
                SdkVersion: "development",
                CompilerCompatibility: SdkCompilerCompatibility.SupportedLine,
                PackageFormatVersion: (int)PackageImageBinaryFormat.CurrentFormatVersion,
                Target: target,
                Modules: [],
                Packages: [],
                DevelopmentSourceRoots: ["stdlib/src", "stdlib/templates", "vendor/src"]);
            var json = JsonSerializer.Serialize(document, SerializerOptions) + Environment.NewLine;
            manifestPath = Path.Combine(canonicalRoot, SdkRootResolver.ManifestFileName);

            if (File.Exists(manifestPath)
                && string.Equals(File.ReadAllText(manifestPath), json, StringComparison.Ordinal))
            {
                return true;
            }

            var temporaryPath = $"{manifestPath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, manifestPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryCreateHostTarget(out DevelopmentSdkTarget target, out string error)
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (Triple: "x86_64", Id: "x64"),
            Architecture.X86 => (Triple: "i686", Id: "x86"),
            Architecture.Arm64 => (Triple: "arm64", Id: "arm64"),
            Architecture.Arm => (Triple: "armv7", Id: "arm"),
            _ => default
        };
        if (architecture.Triple is null)
        {
            target = default!;
            error = $"The repository development SDK does not support host architecture '{RuntimeInformation.ProcessArchitecture}'.";
            return false;
        }

        string targetId;
        string triple;
        string operatingSystem;
        string abi;
        if (OperatingSystem.IsMacOS())
        {
            targetId = $"macos-{architecture.Id}";
            triple = $"{architecture.Triple}-apple-macosx";
            operatingSystem = "macos";
            abi = "darwin";
        }
        else if (OperatingSystem.IsLinux())
        {
            targetId = $"linux-{architecture.Id}";
            triple = $"{architecture.Triple}-unknown-linux-gnu";
            operatingSystem = "linux";
            abi = "gnu";
        }
        else if (OperatingSystem.IsWindows())
        {
            targetId = $"windows-{architecture.Id}";
            triple = $"{architecture.Triple}-pc-windows-msvc";
            operatingSystem = "windows";
            abi = "msvc";
        }
        else
        {
            target = default!;
            error = $"The repository development SDK does not support host OS '{RuntimeInformation.OSDescription}'.";
            return false;
        }

        target = new DevelopmentSdkTarget(
            Id: targetId,
            LlvmTriple: triple,
            Architecture: architecture.Triple,
            OperatingSystem: operatingSystem,
            Abi: abi,
            PointerBitWidth: IntPtr.Size * 8,
            Endianness: BitConverter.IsLittleEndian ? "little" : "big",
            DataLayout: null,
            BaselineCpu: "generic",
            BaselineFeatures: [],
            RelocationModel: "default",
            CodeModel: null,
            CDataModel: null,
            MinimumOperatingSystemVersion: null);
        error = string.Empty;
        return true;
    }

    private sealed record DevelopmentSdkDocument(
        int SchemaVersion,
        string Kind,
        string SdkVersion,
        string CompilerCompatibility,
        int PackageFormatVersion,
        DevelopmentSdkTarget Target,
        IReadOnlyList<object> Modules,
        IReadOnlyList<object> Packages,
        IReadOnlyList<string> DevelopmentSourceRoots);

    private sealed record DevelopmentSdkTarget(
        string Id,
        string LlvmTriple,
        string Architecture,
        string OperatingSystem,
        string Abi,
        int PointerBitWidth,
        string Endianness,
        string? DataLayout,
        string? BaselineCpu,
        IReadOnlyList<string> BaselineFeatures,
        string RelocationModel,
        string? CodeModel,
        string? CDataModel,
        string? MinimumOperatingSystemVersion);
}
