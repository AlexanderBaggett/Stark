using System.IO.Enumeration;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class PrivateBackendBundleVerifier
{
    private static readonly HashSet<string> AllowedToolNames = new(
    [
        "clang", "clang++", "ld.lld", "ld64.lld", "lld", "llvm-ar", "llvm-ranlib",
        "clang.exe", "clang++.exe", "lld-link.exe", "lld.exe", "llvm-ar.exe",
        "llvm-lib.exe", "llvm-ranlib.exe",
    ], StringComparer.Ordinal);

    public static JsonObject Run(CommandLine command)
    {
        command.RejectUnknown("--root", "--target-id", "--toolchain-root", "--expected-manifest-sha256");
        var repositoryRoot = Path.GetFullPath(command.Optional("--root", Directory.GetCurrentDirectory()));
        var targetId = command.Required("--target-id");
        var toolchainRoot = Path.GetFullPath(command.Required("--toolchain-root"));
        var expectedManifestSha256 = command.OptionalNullable("--expected-manifest-sha256");
        return Verify(repositoryRoot, targetId, toolchainRoot, expectedManifestSha256);
    }

    internal static JsonObject Verify(
        string repositoryRoot,
        string targetId,
        string toolchainRoot,
        string? expectedManifestSha256 = null)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        toolchainRoot = Path.GetFullPath(toolchainRoot);
        var configuration = ReleaseConfiguration.Validate(repositoryRoot);
        var target = configuration.Targets.SingleOrDefault(item => item.Id == targetId)
            ?? throw new ReleaseToolException($"Unknown release target '{targetId}'.");
        Validation.Require(Directory.Exists(toolchainRoot), $"Private backend root '{toolchainRoot}' does not exist.");

        var dependencyDocument = JsonIO.LoadObject(
            Path.Combine(repositoryRoot, "eng", "release", "dependencies.json"),
            "dependencies.json");
        var dependency = dependencyDocument.RequiredArray("dependencies", "dependencies.json")
            .OfType<JsonObject>()
            .Single(item => item.RequiredString("kind", "dependency") == "compiler-private-backend");
        var selection = dependency.RequiredArray("selections", "compiler-private-backend")
            .OfType<JsonObject>()
            .Single(item => item.RequiredString("target", "private backend selection") == targetId);
        var acquisition = JsonIO.LoadObject(
            Path.Combine(repositoryRoot, dependency.RequiredString("acquisitionManifest", "compiler-private-backend")),
            "LLVM acquisition manifest");
        var platform = acquisition.RequiredObject("platforms", "LLVM acquisition manifest")
            .RequiredObject(targetId, "LLVM acquisition platforms");

        var manifestPath = Path.Combine(toolchainRoot, "manifest.json");
        var manifest = JsonIO.LoadObject(manifestPath, "private backend manifest");
        PrivateBackendQualifier.RequireExactProperties(manifest, PrivateBackendQualifier.ManifestProperties, "private backend manifest");
        Validation.Require(manifest.RequiredInt("schemaVersion", "private backend manifest") == 2, "Private backend manifest schemaVersion must be 2.");
        Validation.Require(manifest.RequiredString("payloadKind", "private backend manifest") == "stark-compiler-private-backend", "Private backend manifest payloadKind is invalid.");
        PrivateBackendQualifier.RequireEqual(manifest["llvmVersion"], acquisition["llvmVersion"], "LLVM version");
        PrivateBackendQualifier.RequireEqual(manifest["releaseTag"], acquisition["releaseTag"], "LLVM release tag");
        PrivateBackendQualifier.RequireEqual(manifest["releaseUrl"], acquisition["releaseUrl"], "LLVM release URL");
        Validation.Require(manifest.RequiredString("assetSuffix", "private backend manifest") == target.AssetSuffix, "Private backend asset suffix differs from the target.");
        Validation.Require(manifest.RequiredString("runtimeIdentifier", "private backend manifest") == target.RuntimeIdentifier, "Private backend RID differs from the target.");

        var acquisitionKind = selection.RequiredString("acquisition", $"private backend/{targetId}");
        Validation.Require(manifest.RequiredString("acquisitionKind", "private backend manifest") == acquisitionKind, "Private backend acquisition kind differs from dependencies.json.");
        PrivateBackendQualifier.RequireEqual(manifest["sourceArchive"], acquisition["sourceArchive"], "LLVM source archive");
        if (acquisitionKind == "upstream-archive")
        {
            Validation.Require(manifest["sourceBuild"] is null, "Upstream private backend must not declare source-build evidence.");
            PrivateBackendQualifier.RequireEqual(manifest["binaryArchive"], platform["archive"], "LLVM binary archive");
        }
        else
        {
            Validation.Require(acquisitionKind == "pinned-source-build", $"Unsupported private backend acquisition kind '{acquisitionKind}'.");
            Validation.Require(manifest["binaryArchive"] is null && manifest["sourceBuild"] is JsonObject, "Pinned source-built private backend has inconsistent source evidence.");
        }

        var requiredTools = PrivateBackendQualifier.ValidateStringArray(manifest, "requiredTools", nonEmpty: true);
        var expectedTools = Validation.Strings(platform["requiredTools"], $"LLVM/{targetId} requiredTools", nonEmpty: true);
        PrivateBackendQualifier.RequireEqualStringSet(requiredTools, expectedTools, "required tools");
        foreach (var tool in requiredTools)
        {
            Validation.Require(tool.StartsWith("bin/", StringComparison.Ordinal) && AllowedToolNames.Contains(Path.GetFileName(tool)), $"Private backend tool '{tool}' is outside the Stage0 allowlist.");
        }

        var expectedResourceRoots = platform["compilerResourceRoots"] is null
            ? new[] { "lib/clang" }
            : Validation.Strings(platform["compilerResourceRoots"], $"LLVM/{targetId} compilerResourceRoots", nonEmpty: true);
        var resourceRoots = PrivateBackendQualifier.ValidateStringArray(manifest, "compilerResourceRoots", nonEmpty: true);
        PrivateBackendQualifier.RequireEqualStringSet(resourceRoots, expectedResourceRoots, "compiler resource roots");

        var configuredPatterns = Validation.Strings(platform["requiredPatterns"], $"LLVM/{targetId} requiredPatterns");
        var runtimePatterns = configuredPatterns.Where(pattern => !IsDevelopmentOnlyPattern(pattern)).ToArray();
        var developmentPatterns = configuredPatterns.Where(IsDevelopmentOnlyPattern).ToArray();
        var requiredPatternMatches = PrivateBackendQualifier.ValidateStringArray(manifest, "requiredPatternMatches");
        var excludedDevelopmentPatterns = PrivateBackendQualifier.ValidateStringArray(manifest, "excludedDevelopmentPatterns");
        PrivateBackendQualifier.RequireEqualStringSet(excludedDevelopmentPatterns, developmentPatterns, "excluded development patterns");
        foreach (var match in requiredPatternMatches)
        {
            Validation.Require(runtimePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, match, ignoreCase: false)), $"Private backend runtime match '{match}' is not selected by a pinned runtime pattern.");
        }
        foreach (var pattern in runtimePatterns)
        {
            Validation.Require(requiredPatternMatches.Any(match => FileSystemName.MatchesSimpleExpression(pattern, match, ignoreCase: false)), $"Private backend runtime pattern '{pattern}' matched no declared closure path.");
        }

        PrivateBackendQualifier.ValidateHardlinkDeclaration(manifest.RequiredArray("hardlinkAliases", "private backend manifest"), platform["hardlinkAliases"] as JsonArray);
        var licenseFiles = PrivateBackendQualifier.ValidateStringArray(manifest, "licenseFiles", nonEmpty: true);
        PrivateBackendQualifier.ValidateRuntimeClosure(
            toolchainRoot,
            manifest.RequiredObject("runtimeClosure", "private backend manifest"),
            requiredTools,
            requiredPatternMatches,
            resourceRoots,
            licenseFiles,
            acquisition.RequiredObject("sourceArchive", "LLVM acquisition manifest"));

        var manifestSha256 = JsonIO.Sha256File(manifestPath);
        if (expectedManifestSha256 is not null)
        {
            PrivateBackendQualifier.RequireLowerSha256(expectedManifestSha256, "Expected private backend manifest");
            Validation.Require(manifestSha256 == expectedManifestSha256, $"Private backend manifest SHA-256 is '{manifestSha256}', expected '{expectedManifestSha256}'.");
        }

        var closure = manifest.RequiredObject("runtimeClosure", "private backend manifest");
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = "verified",
            ["targetId"] = targetId,
            ["llvmVersion"] = manifest.RequiredString("llvmVersion", "private backend manifest"),
            ["acquisitionKind"] = acquisitionKind,
            ["manifestSha256"] = manifestSha256,
            ["fileCount"] = closure.RequiredInt("fileCount", "private backend runtimeClosure"),
        };
    }

    private static bool IsDevelopmentOnlyPattern(string pattern)
    {
        var normalized = pattern.ToLowerInvariant();
        return normalized.StartsWith("include/", StringComparison.Ordinal)
            || normalized.StartsWith("share/", StringComparison.Ordinal)
            || normalized.Contains("/cmake/", StringComparison.Ordinal)
            || normalized.Contains("/pkgconfig/", StringComparison.Ordinal)
            || normalized.EndsWith(".a", StringComparison.Ordinal)
            || normalized.EndsWith(".lib", StringComparison.Ordinal);
    }
}
