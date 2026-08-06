using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Stark.ReleaseTools;

internal sealed record ReleaseTarget(
    string Id,
    string OperatingSystem,
    string Architecture,
    string RuntimeIdentifier,
    string TargetTriple,
    string GitHubRunner,
    string AssetSuffix,
    string ArchiveKind,
    string CompilerExecutable,
    string StandardLibrary,
    string PrivateBackendSelection,
    string SupportTier,
    bool ReleaseEnabled,
    JsonObject Source);

internal sealed record ReleaseConfigurationResult(
    IReadOnlyList<ReleaseTarget> Targets,
    IReadOnlyList<string> Warnings,
    string DotNetVersion,
    string DotNetRuntimeVersion,
    string NuGetConfig,
    IReadOnlyDictionary<string, string> NuGetLockFiles,
    string LlvmVersion,
    string LlvmManifest,
    IReadOnlyDictionary<string, string> PrivateBackendAcquisitions,
    string RaylibManifest);

internal static class ReleaseConfiguration
{
    private static readonly IReadOnlyDictionary<string, (string OS, string Architecture, string Rid, string Triple)> ExpectedTargets =
        new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal)
        {
            ["linux-x64"] = ("linux", "x64", "linux-x64", "x86_64-unknown-linux-gnu"),
            ["linux-arm64"] = ("linux", "arm64", "linux-arm64", "aarch64-unknown-linux-gnu"),
            ["windows-x64"] = ("windows", "x64", "win-x64", "x86_64-pc-windows-msvc"),
            ["windows-arm64"] = ("windows", "arm64", "win-arm64", "aarch64-pc-windows-msvc"),
            ["macos-x64"] = ("macos", "x64", "osx-x64", "x86_64-apple-macosx11.0.0"),
            ["macos-arm64"] = ("macos", "arm64", "osx-arm64", "arm64-apple-macosx11.0.0"),
        };

    private static readonly HashSet<string> ExpectedVendorPackages =
    [
        "Vendor.Raylib", "Vendor.Raymath", "Vendor.Rlgl", "Vendor.STB.Image", "Vendor.Miniaudio",
        "Vendor.Cgltf", "Vendor.GLFW", "Vendor.SDL3", "Vendor.SQLite",
    ];

    private static readonly string[] ExpectedMacOsX64LlvmCMakeOptions =
    [
        "-DBUILD_SHARED_LIBS=OFF",
        "-DCLANG_INCLUDE_DOCS=OFF",
        "-DCLANG_INCLUDE_TESTS=OFF",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DCMAKE_OSX_ARCHITECTURES=x86_64",
        "-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0",
        "-DLLVM_APPEND_VC_REV=OFF",
        "-DLLVM_BUILD_BENCHMARKS=OFF",
        "-DLLVM_BUILD_EXAMPLES=OFF",
        "-DLLVM_BUILD_LLVM_DYLIB=OFF",
        "-DLLVM_BUILD_TESTS=OFF",
        "-DLLVM_DISTRIBUTION_COMPONENTS=clang;clang-resource-headers;lld;llvm-ar;llvm-ranlib",
        "-DLLVM_ENABLE_ASSERTIONS=OFF",
        "-DLLVM_ENABLE_BINDINGS=OFF",
        "-DLLVM_ENABLE_CURL=OFF",
        "-DLLVM_ENABLE_EH=OFF",
        "-DLLVM_ENABLE_LIBEDIT=OFF",
        "-DLLVM_ENABLE_LIBXML2=OFF",
        "-DLLVM_ENABLE_LTO=Thin",
        "-DLLVM_ENABLE_PIC=ON",
        "-DLLVM_ENABLE_RTTI=OFF",
        "-DLLVM_ENABLE_ZLIB=OFF",
        "-DLLVM_ENABLE_ZSTD=OFF",
        "-DLLVM_INCLUDE_BENCHMARKS=OFF",
        "-DLLVM_INCLUDE_DOCS=OFF",
        "-DLLVM_INCLUDE_EXAMPLES=OFF",
        "-DLLVM_INCLUDE_TESTS=OFF",
        "-DLLVM_LINK_LLVM_DYLIB=OFF",
    ];

    private static readonly HashSet<string> RequiredMetadataOutputs =
    [
        "releaseVersion", "starkVersion", "compilerVersion", "gitCommit", "source", "workflowIdentity",
        "buildIdentity", "buildOptions", "configuration", "schemas", "targetId", "targetFacts",
        "runtimeIdentifier", "defaultTargetTriple", "assetSuffix", "archiveKind", "minimumOs",
        "minimumOsPolicyStatus", "hostPrerequisite", "installerKind", "supportTier", "privateBackend",
        "paths", "compilerPrivateBackend", "contentChecksumManifest", "files", "contentIdentities",
        "sdk", "packages", "packageSchemaFacts", "dependencies", "vendorCatalog",
    ];

    public static JsonObject Run(CommandLine command)
    {
        command.RejectUnknown("--root", "--emit-matrix", "--include-planned", "--github-output", "--quiet");
        var root = Path.GetFullPath(command.Optional("--root", Directory.GetCurrentDirectory()));
        var result = Validate(root);
        var matrix = GenerateMatrix(result, command.HasFlag("--include-planned"));
        var compact = JsonIO.Compact(matrix);
        var githubOutput = command.OptionalNullable("--github-output");
        if (githubOutput is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(githubOutput))!);
            File.AppendAllText(githubOutput, $"matrix={compact}\n", new UTF8Encoding(false));
        }

        if (!command.HasFlag("--quiet"))
        {
            var destination = command.HasFlag("--emit-matrix") ? Console.Error : Console.Out;
            destination.WriteLine($"Validated {result.Targets.Count} release targets ({result.Targets.Count(target => target.ReleaseEnabled)} enabled) and {ExpectedVendorPackages.Count} official Vendor package images across seven upstream families.");
            foreach (var warning in result.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }
        }

        return command.HasFlag("--emit-matrix") ? matrix : new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = "valid",
            ["targets"] = result.Targets.Count,
            ["enabledTargets"] = result.Targets.Count(target => target.ReleaseEnabled),
            ["vendorPackages"] = ExpectedVendorPackages.Count,
        };
    }

    public static ReleaseConfigurationResult Validate(string root)
    {
        var configRoot = Path.Combine(root, "eng", "release");
        var documents = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["targets"] = JsonIO.LoadObject(Path.Combine(configRoot, "targets.json"), "targets.json"),
            ["dependencies"] = JsonIO.LoadObject(Path.Combine(configRoot, "dependencies.json"), "dependencies.json"),
            ["build-tools"] = JsonIO.LoadObject(Path.Combine(configRoot, "build-tools.json"), "build-tools.json"),
            ["managed-licenses"] = JsonIO.LoadObject(Path.Combine(configRoot, "managed-license-evidence.json"), "managed-license-evidence.json"),
            ["vendor"] = JsonIO.LoadObject(Path.Combine(configRoot, "vendor-packages.json"), "vendor-packages.json"),
            ["archive"] = JsonIO.LoadObject(Path.Combine(configRoot, "archive-content.json"), "archive-content.json"),
            ["archive-schema"] = JsonIO.LoadObject(Path.Combine(configRoot, "archive-content.schema.json"), "archive-content.schema.json"),
            ["metadata-template"] = JsonIO.LoadObject(Path.Combine(configRoot, "release-metadata.template.json"), "release-metadata.template.json"),
        };
        foreach (var document in documents)
        {
            Validation.NoPlaceholders(document.Key, document.Value);
        }

        var targets = ValidateTargets(documents["targets"]);
        var warnings = new List<string>();
        if (documents["targets"].RequiredString("minimumOsPolicyStatus", "targets.json") != "locked")
        {
            warnings.Add("minimum OS values are provisional");
        }

        var managed = ValidateDependencies(documents["dependencies"], documents["build-tools"], targets, warnings, root);
        ValidateBuildTools(documents["build-tools"], targets);
        ManagedLicenseEvidence.ValidateDeclaration(root);
        ValidateVendor(documents["vendor"], targets, root);
        ValidateArchiveContent(documents["archive"], targets, root);
        ValidateRepositoryContent(documents["archive"], root);
        ValidateMetadataTemplate(documents["metadata-template"]);

        var dependencies = documents["dependencies"].RequiredArray("dependencies", "dependencies.json").OfType<JsonObject>().ToArray();
        var llvm = dependencies.Single(item => item.RequiredString("kind", "dependency") == "compiler-private-backend");
        var privateBackendAcquisitions = llvm.RequiredArray("selections", "LLVM dependency")
            .OfType<JsonObject>()
            .ToDictionary(
                item => item.RequiredString("target", "LLVM dependency selection"),
                item => item.RequiredString("acquisition", "LLVM dependency selection"),
                StringComparer.Ordinal);
        var raylib = documents["vendor"].RequiredArray("packages", "vendor-packages.json").OfType<JsonObject>().Single(item => item.RequiredString("id", "Vendor package") == "Vendor.Raylib");
        return new ReleaseConfigurationResult(
            targets,
            warnings,
            managed.DotNetVersion,
            managed.DotNetRuntimeVersion,
            managed.NuGetConfig,
            managed.LockFiles,
            llvm.RequiredString("version", "LLVM dependency"),
            llvm.RequiredString("acquisitionManifest", "LLVM dependency"),
            privateBackendAcquisitions,
            raylib.RequiredString("acquisitionManifest", "Vendor.Raylib"));
    }

    public static JsonObject GenerateMatrix(ReleaseConfigurationResult result, bool includePlanned)
    {
        var include = new JsonArray();
        foreach (var target in result.Targets.Where(target => includePlanned || target.ReleaseEnabled))
        {
            include.Add(new JsonObject
            {
                ["target_id"] = target.Id,
                ["os"] = target.GitHubRunner,
                ["operating_system"] = target.OperatingSystem,
                ["rid"] = target.RuntimeIdentifier,
                ["asset_suffix"] = target.AssetSuffix,
                ["archive_kind"] = target.ArchiveKind,
                ["stdlib_library"] = target.StandardLibrary,
                ["target_triple"] = target.TargetTriple,
                ["architecture"] = target.Architecture,
                ["support_tier"] = target.SupportTier,
                ["release_enabled"] = target.ReleaseEnabled,
                ["dotnet_version"] = result.DotNetVersion,
                ["dotnet_runtime_version"] = result.DotNetRuntimeVersion,
                ["nuget_config"] = result.NuGetConfig,
                ["nuget_lock_file"] = result.NuGetLockFiles[target.RuntimeIdentifier],
                ["llvm_version"] = result.LlvmVersion,
                ["llvm_manifest"] = result.LlvmManifest,
                ["private_backend_selection"] = target.PrivateBackendSelection,
                ["private_backend_acquisition"] = result.PrivateBackendAcquisitions[target.Id],
                ["raylib_manifest"] = result.RaylibManifest,
            });
        }

        return new JsonObject { ["include"] = include };
    }

    private static List<ReleaseTarget> ValidateTargets(JsonObject document)
    {
        Validation.Require(document.RequiredInt("schemaVersion", "targets.json") == 1, "targets.json schemaVersion must be 1.");
        Validation.Require(document.RequiredString("architecturePolicy", "targets.json") == "64-bit-only", "targets.json must declare the 64-bit-only policy.");
        var policy = document.RequiredString("minimumOsPolicyStatus", "targets.json");
        Validation.Require(policy is "provisional" or "locked", "targets.json minimum OS policy status is invalid.");
        var values = document.RequiredArray("targets", "targets.json").OfType<JsonObject>().ToArray();
        Validation.Require(values.Length == ExpectedTargets.Count, "targets.json must contain exactly six target objects.");
        var ids = values.Select(value => value.RequiredString("id", "target")).ToArray();
        Validation.Unique(ids, "target IDs");
        Validation.Require(ids.ToHashSet(StringComparer.Ordinal).SetEquals(ExpectedTargets.Keys), "targets.json must contain exactly the intended six 64-bit target IDs.");

        foreach (var field in new[] { "runtimeIdentifier", "targetTriple", "assetSuffix" })
        {
            Validation.Unique(values.Select(value => value.RequiredString(field, $"target {field}")), $"target {field} values");
        }

        var targets = new List<ReleaseTarget>();
        foreach (var value in values)
        {
            var id = value.RequiredString("id", "target");
            Validation.Require(Validation.IsIdentifier(id), $"Invalid target ID '{id}'.");
            var expected = ExpectedTargets[id];
            var os = value.RequiredString("operatingSystem", id);
            var architecture = value.RequiredString("architecture", id);
            var rid = value.RequiredString("runtimeIdentifier", id);
            var triple = value.RequiredString("targetTriple", id);
            Validation.Require((os, architecture, rid, triple) == expected, $"{id} platform facts are inconsistent.");
            Validation.Require(value.RequiredString("assetSuffix", id) == id, $"{id} assetSuffix must equal its stable ID.");
            Validation.Require(value.RequiredString("privateBackendSelection", id) == $"llvm-22.1.8/{id}", $"{id} private backend mapping is inconsistent.");
            var archiveKind = value.RequiredString("archiveKind", id);
            var executable = value.RequiredString("compilerExecutable", id);
            var standardLibrary = value.RequiredString("standardLibrary", id);
            if (os == "windows")
            {
                Validation.Require(archiveKind == "zip" && value.RequiredString("archiveExtension", id) == ".zip" && executable == "stark.exe" && standardLibrary == "System.lib" && value.RequiredString("installerKind", id) == "powershell", $"{id} Windows archive facts are inconsistent.");
            }
            else
            {
                Validation.Require(archiveKind == "targz" && value.RequiredString("archiveExtension", id) == ".tar.gz" && executable == "stark" && standardLibrary == "libSystem.a" && value.RequiredString("installerKind", id) == "posix-shell", $"{id} Unix archive facts are inconsistent.");
            }

            targets.Add(new ReleaseTarget(
                id, os, architecture, rid, triple,
                value.RequiredString("githubRunner", id),
                value.RequiredString("assetSuffix", id),
                archiveKind, executable, standardLibrary,
                value.RequiredString("privateBackendSelection", id),
                value.RequiredString("supportTier", id),
                value.RequiredBool("releaseEnabled", id), value));
            _ = value.RequiredString("minimumOs", id);
            _ = value.RequiredString("hostPrerequisite", id);
        }

        Validation.Require(targets.Any(target => target.ReleaseEnabled), "At least one release target must be enabled.");
        return targets;
    }

    private sealed record ManagedFacts(string DotNetVersion, string DotNetRuntimeVersion, string NuGetConfig, Dictionary<string, string> LockFiles);

    private static ManagedFacts ValidateDependencies(JsonObject document, JsonObject buildTools, IReadOnlyList<ReleaseTarget> targets, List<string> warnings, string root)
    {
        Validation.Require(document.RequiredInt("schemaVersion", "dependencies.json") == 1, "dependencies.json schemaVersion must be 1.");
        var dependencies = document.RequiredArray("dependencies", "dependencies.json").OfType<JsonObject>().ToArray();
        Validation.Require(dependencies.Length != 0, "dependencies.json dependencies must be non-empty.");
        var ids = dependencies.Select(item => item.RequiredString("id", "dependency")).ToArray();
        Validation.Unique(ids, "dependency IDs");
        Validation.Require(new[] { "dotnet-stage0-runtime", "antlr4-runtime-standard", "llvm-22.1.8" }.All(ids.Contains), "dependencies.json omits a required Stage0 dependency.");
        var targetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        var byTarget = targets.ToDictionary(target => target.Id, StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            var id = dependency.RequiredString("id", "dependency");
            Validation.Require(Validation.IsDependencyIdentifier(id), $"Invalid dependency ID '{id}'.");
            foreach (var name in new[] { "kind", "version", "sourceUrl", "license", "licenseUrl", "archiveLayout" })
            {
                var text = dependency.RequiredString(name, id);
                if (name == "archiveLayout") Validation.SafeRelativePath(text, $"{id} archiveLayout");
            }

            var selections = dependency.RequiredArray("selections", id).OfType<JsonObject>().ToArray();
            var selectedIds = selections.Select(item => item.RequiredString("target", $"{id} selection")).ToArray();
            Validation.Unique(selectedIds, $"{id} selection targets");
            Validation.Require(targetIds.SetEquals(selectedIds), $"{id} must explicitly select all six targets.");
            foreach (var selection in selections)
            {
                var targetId = selection.RequiredString("target", id);
                _ = selection.RequiredString("acquisition", $"{id}/{targetId}");
                if (selection["runtimeIdentifier"] is not null)
                {
                    Validation.Require(selection.RequiredString("runtimeIdentifier", $"{id}/{targetId}") == byTarget[targetId].RuntimeIdentifier, $"{id}/{targetId} RID mismatch.");
                }

                var qualification = selection["qualificationStatus"]?.GetValue<string>();
                if (qualification == "qualified-input")
                {
                    _ = selection.RequiredString("archiveUrl", $"{id}/{targetId}");
                    Validation.Require(Validation.IsSha256(selection.RequiredString("archiveSha256", $"{id}/{targetId}")), $"{id}/{targetId} archive SHA-256 is invalid.");
                }
                else if (qualification == "qualified-build")
                {
                    Validation.Require(selection.RequiredString("acquisition", $"{id}/{targetId}") == "pinned-source-build", $"{id}/{targetId} qualified-build must identify a pinned source build.");
                    var commit = selection.RequiredString("qualificationCommit", $"{id}/{targetId}");
                    Validation.Require(commit.Length == 40 && commit.All(char.IsAsciiHexDigit), $"{id}/{targetId} qualification commit is invalid.");
                    var workflow = selection.RequiredString("qualificationWorkflow", $"{id}/{targetId}");
                    Validation.Require(Uri.TryCreate(workflow, UriKind.Absolute, out var workflowUri) && workflowUri.Scheme == Uri.UriSchemeHttps && workflowUri.Host == "github.com", $"{id}/{targetId} qualification workflow URL is invalid.");
                    Validation.Require(long.TryParse(selection.RequiredString("qualificationArtifactId", $"{id}/{targetId}"), out var artifactId) && artifactId > 0, $"{id}/{targetId} qualification artifact ID is invalid.");
                }
                else if (qualification?.StartsWith("unqualified-", StringComparison.Ordinal) == true)
                {
                    _ = selection.RequiredString("qualificationReason", $"{id}/{targetId}");
                    warnings.Add($"{id}/{targetId} is explicitly {qualification}");
                }
            }

            if (dependency.RequiredString("pinStatus", id) != "exact")
            {
                warnings.Add($"{id} pinStatus is not exact");
            }

            if (dependency["acquisitionManifest"] is JsonValue manifestValue && manifestValue.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                var manifest = manifestValue.GetValue<string>();
                Validation.SafeRelativePath(manifest, $"{id} acquisitionManifest");
                Validation.Require(File.Exists(Path.Combine(root, manifest)), $"{id} acquisition manifest does not exist.");
            }
        }

        var dotnet = dependencies.Single(item => item.RequiredString("id", "dependency") == "dotnet-stage0-runtime");
        var antlr = dependencies.Single(item => item.RequiredString("id", "dependency") == "antlr4-runtime-standard");
        Validation.Require(dotnet.RequiredString("version", "dotnet-stage0-runtime") == "10.0.302", "Stage0 release SDK must be pinned to .NET SDK 10.0.302.");
        Validation.Require(dotnet.RequiredString("runtimeVersion", "dotnet-stage0-runtime") == "10.0.10", "Stage0 release runtime must be pinned to .NET 10.0.10.");
        Validation.Require(antlr.RequiredString("version", "antlr4-runtime-standard") == "4.13.1" && antlr.RequiredString("versionRange", "antlr4-runtime-standard") == "[4.13.1]", "Stage0 ANTLR runtime must be pinned exactly to 4.13.1.");
        ValidateGlobalJson(root, dotnet);
        ValidateCompilerProject(root);
        var nugetConfig = antlr.RequiredString("nugetConfig", "antlr4-runtime-standard");
        ValidateNuGetConfiguration(root, nugetConfig);

        var lockFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var selection in antlr.RequiredArray("selections", "antlr4-runtime-standard").OfType<JsonObject>())
        {
            var target = byTarget[selection.RequiredString("target", "ANTLR selection")];
            var lockFile = selection.RequiredString("lockFile", $"ANTLR/{target.RuntimeIdentifier}");
            Validation.Require(lockFile == $"src/packages.{target.RuntimeIdentifier}.lock.json", $"ANTLR/{target.RuntimeIdentifier} lock file is inconsistent.");
            Validation.Require(File.Exists(Path.Combine(root, lockFile)), $"ANTLR lock file is missing: {lockFile}");
            lockFiles.Add(target.RuntimeIdentifier, lockFile);
        }

        var backend = dependencies.Single(item => item.RequiredString("kind", "dependency") == "compiler-private-backend");
        ValidatePrivateBackendAcquisitionManifest(root, backend, buildTools, targets);
        var backendSelections = backend.RequiredArray("selections", "private backend").OfType<JsonObject>().ToDictionary(item => item.RequiredString("target", "private backend selection"), StringComparer.Ordinal);
        foreach (var target in targets.Where(target => target.ReleaseEnabled))
        {
            var status = backendSelections[target.Id].RequiredString("qualificationStatus", $"private backend/{target.Id}");
            Validation.Require(status is "qualified-input" or "qualified-build", $"Release-enabled target {target.Id} has an unqualified private backend.");
        }

        return new ManagedFacts(dotnet.RequiredString("version", "dotnet-stage0-runtime"), dotnet.RequiredString("runtimeVersion", "dotnet-stage0-runtime"), nugetConfig, lockFiles);
    }

    private static void ValidatePrivateBackendAcquisitionManifest(string root, JsonObject backend, JsonObject buildTools, IReadOnlyList<ReleaseTarget> targets)
    {
        var relative = backend.RequiredString("acquisitionManifest", "private backend");
        Validation.SafeRelativePath(relative, "private backend acquisitionManifest");
        var manifest = JsonIO.LoadObject(Path.Combine(root, relative), "LLVM acquisition manifest");
        Validation.NoPlaceholders("LLVM acquisition manifest", manifest);

        var version = backend.RequiredString("version", "private backend");
        var releaseTag = $"llvmorg-{version}";
        var releaseBaseUrl = $"https://github.com/llvm/llvm-project/releases/download/{releaseTag}/";
        Validation.Require(manifest.RequiredString("llvmVersion", "LLVM acquisition manifest") == version, "LLVM acquisition manifest version differs from dependencies.json.");
        Validation.Require(manifest.RequiredString("releaseTag", "LLVM acquisition manifest") == releaseTag, "LLVM acquisition manifest release tag is inconsistent.");
        Validation.Require(manifest.RequiredString("releaseUrl", "LLVM acquisition manifest") == $"https://github.com/llvm/llvm-project/releases/tag/{releaseTag}", "LLVM acquisition manifest release URL is inconsistent.");

        var sourceArchive = manifest.RequiredObject("sourceArchive", "LLVM acquisition manifest");
        ValidateLlvmAsset(sourceArchive, "LLVM source archive", releaseBaseUrl, requireEvidence: true);
        Validation.Require(sourceArchive.RequiredString("url", "LLVM source archive") == backend.RequiredString("sourceUrl", "private backend"), "LLVM source archive URL differs from dependencies.json.");
        Validation.Require(sourceArchive.RequiredString("sha256", "LLVM source archive") == backend.RequiredString("sourceSha256", "private backend"), "LLVM source archive SHA-256 differs from dependencies.json.");

        foreach (var rootName in Validation.Strings(manifest["copiedRoots"], "LLVM copiedRoots", nonEmpty: true))
        {
            Validation.SafeRelativePath(rootName, "LLVM copiedRoot");
        }

        _ = Validation.Strings(manifest["licenseFilePatterns"], "LLVM licenseFilePatterns", nonEmpty: true);
        var targetById = targets.ToDictionary(target => target.Id, StringComparer.Ordinal);
        var selections = backend.RequiredArray("selections", "private backend").OfType<JsonObject>()
            .ToDictionary(selection => selection.RequiredString("target", "private backend selection"), StringComparer.Ordinal);
        var configuredTargets = selections
            .Where(item =>
            {
                var acquisition = item.Value.RequiredString("acquisition", $"private backend/{item.Key}");
                var qualification = item.Value.RequiredString("qualificationStatus", $"private backend/{item.Key}");
                return (acquisition == "upstream-archive" && qualification == "qualified-input") || acquisition == "pinned-source-build";
            })
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var platforms = manifest.RequiredObject("platforms", "LLVM acquisition manifest");
        Validation.Require(platforms.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(configuredTargets), "LLVM acquisition manifest platforms must exactly match qualified upstream inputs and configured pinned source builds.");

        foreach (var item in platforms)
        {
            var targetId = item.Key;
            Validation.Require(targetById.TryGetValue(targetId, out var target), $"LLVM acquisition manifest contains unknown target '{targetId}'.");
            Validation.Require(item.Value is JsonObject, $"LLVM acquisition manifest platform '{targetId}' must be an object.");
            var platform = (JsonObject)item.Value!;
            Validation.Require(platform.RequiredString("runtimeIdentifier", $"LLVM/{targetId}") == target!.RuntimeIdentifier, $"LLVM/{targetId} RID mismatch.");

            var selection = selections[targetId];
            var acquisition = selection.RequiredString("acquisition", $"private backend/{targetId}");
            if (acquisition == "upstream-archive")
            {
                Validation.Require(platform["sourceBuild"] is null, $"LLVM/{targetId} upstream archive must not declare a source build.");
                var archive = platform.RequiredObject("archive", $"LLVM/{targetId}");
                ValidateLlvmAsset(archive, $"LLVM/{targetId} archive", releaseBaseUrl, requireEvidence: true);
                Validation.Require(archive.RequiredString("url", $"LLVM/{targetId} archive") == selection.RequiredString("archiveUrl", $"private backend/{targetId}"), $"LLVM/{targetId} archive URL differs from dependencies.json.");
                Validation.Require(archive.RequiredString("sha256", $"LLVM/{targetId} archive") == selection.RequiredString("archiveSha256", $"private backend/{targetId}"), $"LLVM/{targetId} archive SHA-256 differs from dependencies.json.");
            }
            else
            {
                Validation.Require(acquisition == "pinned-source-build" && platform["archive"] is null, $"LLVM/{targetId} has an invalid or ambiguous acquisition kind.");
                ValidateLlvmSourceBuild(platform.RequiredObject("sourceBuild", $"LLVM/{targetId}"), targetId, selection, buildTools);
            }

            foreach (var tool in Validation.Strings(platform["requiredTools"], $"LLVM/{targetId} requiredTools", nonEmpty: true))
            {
                Validation.SafeRelativePath(tool, $"LLVM/{targetId} required tool");
                Validation.Require(tool.StartsWith("bin/", StringComparison.Ordinal), $"LLVM/{targetId} required tool must be under bin/: {tool}");
            }

            foreach (var pattern in Validation.Strings(platform["requiredPatterns"], $"LLVM/{targetId} requiredPatterns"))
            {
                Validation.SafeRelativePath(pattern, $"LLVM/{targetId} required pattern");
            }

            ValidateOptionalPaths(platform, "compilerResourceRoots", targetId);
            ValidateOptionalPaths(platform, "copiedRoots", targetId);
            if (platform["hardlinkAliases"] is JsonArray aliases)
            {
                var aliasObjects = aliases.OfType<JsonObject>().ToArray();
                Validation.Require(aliasObjects.Length == aliases.Count, $"LLVM/{targetId} hardlinkAliases must contain only objects.");
                foreach (var alias in aliasObjects)
                {
                    Validation.SafeRelativePath(alias.RequiredString("path", $"LLVM/{targetId} hardlink alias"), $"LLVM/{targetId} hardlink alias path");
                    Validation.SafeRelativePath(alias.RequiredString("target", $"LLVM/{targetId} hardlink alias"), $"LLVM/{targetId} hardlink alias target");
                }
            }
        }
    }

    private static void ValidateLlvmSourceBuild(JsonObject build, string targetId, JsonObject selection, JsonObject buildTools)
    {
        Validation.Require(targetId == "macos-x64", "Only the reviewed macos-x64 LLVM source build is currently supported.");
        Validation.Require(selection.RequiredString("qualificationStatus", $"private backend/{targetId}") is "unqualified-build" or "qualified-build", $"LLVM/{targetId} source-build qualification status is invalid.");
        Validation.Require(build.RequiredString("hostOperatingSystem", $"LLVM/{targetId} source build") == "macos", "LLVM/macos-x64 source build must run on macOS.");
        Validation.Require(build.RequiredString("hostArchitecture", $"LLVM/{targetId} source build") == "x64", "LLVM/macos-x64 source build must run natively on x64.");
        Validation.Require(build.RequiredString("minimumDeploymentTarget", $"LLVM/{targetId} source build") == "11.0", "LLVM/macos-x64 deployment target must be macOS 11.0.");
        Validation.Require(build.RequiredString("generator", $"LLVM/{targetId} source build") == "Ninja", "LLVM/macos-x64 source build must use Ninja.");
        Validation.Require(build.RequiredString("configuration", $"LLVM/{targetId} source build") == "Release", "LLVM/macos-x64 source build must use Release configuration.");
        Validation.Require(build.RequiredString("optimization", $"LLVM/{targetId} source build") == "O3", "LLVM/macos-x64 source build must retain O3 optimization.");
        Validation.Require(build.RequiredString("lto", $"LLVM/{targetId} source build") == "Thin", "LLVM/macos-x64 source build must retain ThinLTO.");
        Validation.Require(build.RequiredString("sourceSubdirectory", $"LLVM/{targetId} source build") == "llvm", "LLVM/macos-x64 source subdirectory is invalid.");
        Validation.Require(build.RequiredString("buildTarget", $"LLVM/{targetId} source build") == "install-distribution-stripped", "LLVM/macos-x64 source build must install only the reviewed stripped distribution components.");
        Validation.Require(Validation.Strings(build["projects"], $"LLVM/{targetId} projects", nonEmpty: true).SequenceEqual(["clang", "lld"]), "LLVM/macos-x64 source build must include exactly Clang and LLD.");
        Validation.Require(Validation.Strings(build["targetsToBuild"], $"LLVM/{targetId} targets", nonEmpty: true).SequenceEqual(["AArch64", "X86"]), "LLVM/macos-x64 source build must preserve AArch64 and X86 code-generation support.");
        Validation.Require(Validation.Strings(build["cmakeOptions"], $"LLVM/{targetId} CMake options", nonEmpty: true).SequenceEqual(ExpectedMacOsX64LlvmCMakeOptions), "LLVM/macos-x64 source-build optimization or closure options drifted.");
        Validation.Require(build["sourceDateEpoch"] is JsonValue epoch && epoch.TryGetValue<long>(out var sourceDateEpoch) && sourceDateEpoch == 0, "LLVM/macos-x64 SOURCE_DATE_EPOCH must be fixed at zero.");
        Validation.Require(build["maxParallelCompileJobs"] is JsonValue jobs && jobs.TryGetValue<int>(out var compileJobs) && compileJobs is > 0 and <= 8, "LLVM/macos-x64 compile parallelism must be bounded.");
        Validation.Require(build["parallelLinkJobs"] is JsonValue links && links.TryGetValue<int>(out var linkJobs) && linkJobs == 1, "LLVM/macos-x64 ThinLTO linking must be serialized for bounded CI memory use.");

        var apple = build.RequiredObject("qualifiedAppleToolchain", $"LLVM/{targetId} source build");
        Validation.Require(
            apple.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals([
                "xcodeVersion", "sdkVersion", "clangVersionLine", "clangSha256", "clangxxSha256",
            ]),
            "LLVM/macos-x64 qualified Apple toolchain must contain exactly the reviewed identity fields.");
        Validation.Require(apple.RequiredString("xcodeVersion", "LLVM/macos-x64 qualified Apple toolchain") == "Xcode 16.4\nBuild version 16F6", "LLVM/macos-x64 qualified Xcode identity drifted.");
        Validation.Require(apple.RequiredString("sdkVersion", "LLVM/macos-x64 qualified Apple toolchain") == "15.5", "LLVM/macos-x64 qualified SDK identity drifted.");
        Validation.Require(apple.RequiredString("clangVersionLine", "LLVM/macos-x64 qualified Apple toolchain") == "Apple clang version 17.0.0 (clang-1700.0.13.5)", "LLVM/macos-x64 qualified Apple Clang identity drifted.");
        foreach (var field in new[] { "clangSha256", "clangxxSha256" })
        {
            var sha256 = apple.RequiredString(field, "LLVM/macos-x64 qualified Apple toolchain");
            Validation.Require(sha256 == sha256.ToLowerInvariant() && Validation.IsSha256(sha256), $"LLVM/macos-x64 qualified Apple toolchain {field} is invalid.");
        }
        Validation.Require(apple.RequiredString("clangSha256", "LLVM/macos-x64 qualified Apple toolchain") == "4c458256bcdf913774de1bb4a37768244d3b41f32ad55afb2dfec3432f46f4fe", "LLVM/macos-x64 qualified Apple Clang hash drifted.");
        Validation.Require(apple.RequiredString("clangxxSha256", "LLVM/macos-x64 qualified Apple toolchain") == "4c458256bcdf913774de1bb4a37768244d3b41f32ad55afb2dfec3432f46f4fe", "LLVM/macos-x64 qualified Apple Clang++ hash drifted.");

        var tools = buildTools.RequiredObject("tools", "build-tools.json");
        Validation.Require(build.RequiredString("cmakeVersion", $"LLVM/{targetId} source build") == tools.RequiredObject("cmake", "build-tools.json").RequiredString("version", "CMake"), "LLVM/macos-x64 CMake version differs from the pinned build-tool manifest.");
        Validation.Require(build.RequiredString("ninjaVersion", $"LLVM/{targetId} source build") == tools.RequiredObject("ninja", "build-tools.json").RequiredString("version", "Ninja"), "LLVM/macos-x64 Ninja version differs from the pinned build-tool manifest.");
    }

    private static void ValidateOptionalPaths(JsonObject value, string property, string targetId)
    {
        if (value[property] is null)
        {
            return;
        }

        foreach (var path in Validation.Strings(value[property], $"LLVM/{targetId} {property}", nonEmpty: true))
        {
            Validation.SafeRelativePath(path, $"LLVM/{targetId} {property}");
        }
    }

    private static void ValidateLlvmAsset(JsonObject asset, string context, string releaseBaseUrl, bool requireEvidence)
    {
        var name = asset.RequiredString("name", context);
        Validation.Require(name == Path.GetFileName(name) && !name.Contains('\\'), $"{context} name must be a portable filename.");
        Validation.Require(asset.RequiredString("url", context) == releaseBaseUrl + Uri.EscapeDataString(name), $"{context} URL is inconsistent with its filename and release.");
        var sha256 = asset.RequiredString("sha256", context);
        Validation.Require(sha256 == sha256.ToLowerInvariant() && Validation.IsSha256(sha256), $"{context} SHA-256 is invalid.");
        Validation.Require(asset["size"] is JsonValue size && size.TryGetValue<long>(out var bytes) && bytes > 0, $"{context} size is invalid.");

        if (!requireEvidence)
        {
            return;
        }

        var signature = asset.RequiredObject("signature", context);
        var attestation = asset.RequiredObject("attestation", context);
        ValidateLlvmAsset(signature, $"{context} signature", releaseBaseUrl, requireEvidence: false);
        ValidateLlvmAsset(attestation, $"{context} attestation", releaseBaseUrl, requireEvidence: false);
        Validation.Require(signature.RequiredString("name", $"{context} signature") == name + ".sig", $"{context} signature filename is inconsistent.");
        Validation.Require(attestation.RequiredString("name", $"{context} attestation") == name + ".jsonl", $"{context} attestation filename is inconsistent.");
    }

    internal static void ValidateBuildTools(JsonObject document, IReadOnlyList<ReleaseTarget> targets)
    {
        Validation.NoPlaceholders("build-tools.json", document);
        Validation.Require(document.RequiredInt("schemaVersion", "build-tools.json") == 1, "build-tools.json schemaVersion must be 1.");
        Validation.Require(document.RequiredString("purpose", "build-tools.json") == "build-time-only", "build-tools.json must declare build-time-only purpose.");
        var tools = document.RequiredObject("tools", "build-tools.json");
        Validation.Require(tools.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["cmake", "ninja"]), "build-tools.json must contain exactly CMake and Ninja.");

        var cmake = tools.RequiredObject("cmake", "build-tools.json tools");
        var ninja = tools.RequiredObject("ninja", "build-tools.json tools");
        var cmakeVersion = ValidateBuildToolVersion(cmake, "CMake", "https://github.com/Kitware/CMake/releases/tag/v");
        var ninjaVersion = ValidateBuildToolVersion(ninja, "Ninja", "https://github.com/ninja-build/ninja/releases/tag/v");
        var cmakeAssets = ValidateBuildToolAssets(cmake, "CMake", $"https://github.com/Kitware/CMake/releases/download/v{cmakeVersion}/");
        var ninjaAssets = ValidateBuildToolAssets(ninja, "Ninja", $"https://github.com/ninja-build/ninja/releases/download/v{ninjaVersion}/");

        var checksum = cmake.RequiredObject("checksumManifest", "CMake");
        var checksumName = checksum.RequiredString("name", "CMake checksum manifest");
        Validation.Require(checksumName == $"cmake-{cmakeVersion}-SHA-256.txt", "CMake checksum manifest filename is inconsistent.");
        ValidatePinnedDownload(checksum, "CMake checksum manifest", $"https://github.com/Kitware/CMake/releases/download/v{cmakeVersion}/{checksumName}");

        var targetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        var mappingNodes = document.RequiredArray("targets", "build-tools.json");
        var mappings = mappingNodes.OfType<JsonObject>().ToArray();
        Validation.Require(mappings.Length == mappingNodes.Count && mappings.Length == targets.Count, "build-tools.json must contain exactly one object mapping per release target.");
        var mappedIds = mappings.Select(mapping => mapping.RequiredString("id", "build-tool target mapping")).ToArray();
        Validation.Unique(mappedIds, "build-tool target mappings");
        Validation.Require(targetIds.SetEquals(mappedIds), "build-tools.json target mappings must cover all six release targets.");
        var selectedCMake = new HashSet<string>(StringComparer.Ordinal);
        var selectedNinja = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            var id = mapping.RequiredString("id", "build-tool target mapping");
            var expectedAsset = id.StartsWith("macos-", StringComparison.Ordinal) ? "macos-universal" : id;
            var cmakeAsset = mapping.RequiredString("cmakeAsset", $"build-tools/{id}");
            var ninjaAsset = mapping.RequiredString("ninjaAsset", $"build-tools/{id}");
            Validation.Require(cmakeAsset == expectedAsset && ninjaAsset == expectedAsset, $"build-tools/{id} must select the matching native asset family.");
            Validation.Require(cmakeAssets.ContainsKey(cmakeAsset), $"build-tools/{id} selects unknown CMake asset '{cmakeAsset}'.");
            Validation.Require(ninjaAssets.ContainsKey(ninjaAsset), $"build-tools/{id} selects unknown Ninja asset '{ninjaAsset}'.");
            selectedCMake.Add(cmakeAsset);
            selectedNinja.Add(ninjaAsset);
        }

        Validation.Require(selectedCMake.SetEquals(cmakeAssets.Keys), "build-tools.json contains an unused or unmapped CMake asset.");
        Validation.Require(selectedNinja.SetEquals(ninjaAssets.Keys), "build-tools.json contains an unused or unmapped Ninja asset.");
    }

    private static string ValidateBuildToolVersion(JsonObject tool, string name, string releaseUrlPrefix)
    {
        var version = tool.RequiredString("version", name);
        var parts = version.Split('.');
        Validation.Require(parts.Length == 3 && parts.All(part => int.TryParse(part, out var number) && number >= 0 && number.ToString() == part), $"{name} version must be an exact canonical three-part numeric version.");
        Validation.Require(tool.RequiredString("releaseUrl", name) == releaseUrlPrefix + version, $"{name} release URL is inconsistent with its version.");
        return version;
    }

    private static Dictionary<string, JsonObject> ValidateBuildToolAssets(JsonObject tool, string name, string downloadUrlPrefix)
    {
        var assetNodes = tool.RequiredArray("assets", name);
        var assets = assetNodes.OfType<JsonObject>().ToArray();
        Validation.Require(assets.Length == assetNodes.Count && assets.Length != 0, $"{name} assets must be a non-empty array of objects.");
        var ids = assets.Select(asset => asset.RequiredString("id", $"{name} asset")).ToArray();
        Validation.Unique(ids, $"{name} asset IDs");
        foreach (var asset in assets)
        {
            var id = asset.RequiredString("id", $"{name} asset");
            Validation.Require(Validation.IsIdentifier(id), $"{name} asset ID '{id}' is invalid.");
            var archiveKind = asset.RequiredString("archiveKind", $"{name}/{id}");
            Validation.Require(archiveKind is "zip" or "targz", $"{name}/{id} archive kind is invalid.");
            var archiveName = asset.RequiredString("name", $"{name}/{id}");
            Validation.Require(
                (archiveKind == "zip" && archiveName.EndsWith(".zip", StringComparison.Ordinal)) ||
                (archiveKind == "targz" && archiveName.EndsWith(".tar.gz", StringComparison.Ordinal)),
                $"{name}/{id} archive filename does not match its kind.");
            ValidatePinnedDownload(asset, $"{name}/{id}", downloadUrlPrefix + archiveName);
            var executable = asset.RequiredString("executable", $"{name}/{id}");
            Validation.SafeRelativePath(executable, $"{name}/{id} executable");
            Validation.Require(executable.IndexOfAny(['*', '?', '[', ']']) < 0, $"{name}/{id} executable must not contain wildcard characters.");
        }

        return assets.ToDictionary(asset => asset.RequiredString("id", $"{name} asset"), StringComparer.Ordinal);
    }

    private static void ValidatePinnedDownload(JsonObject asset, string context, string expectedUrl)
    {
        var name = asset.RequiredString("name", context);
        Validation.Require(IsPortableFileName(name), $"{context} name must be a portable filename.");
        Validation.Require(asset.RequiredString("url", context) == expectedUrl, $"{context} URL is inconsistent with its immutable release identity.");
        var sha256 = asset.RequiredString("sha256", context);
        Validation.Require(sha256 == sha256.ToLowerInvariant() && Validation.IsSha256(sha256), $"{context} SHA-256 is invalid.");
        Validation.Require(asset["bytes"] is JsonValue size && size.TryGetValue<long>(out var bytes) && bytes > 0, $"{context} byte count is invalid.");
    }

    private static bool IsPortableFileName(string name)
        => name.Length is > 0 and <= 128 &&
           char.IsAsciiLetterOrDigit(name[0]) &&
           name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '-');

    private static void ValidateGlobalJson(string root, JsonObject dotnet)
    {
        var relative = dotnet.RequiredString("globalJson", "dotnet-stage0-runtime");
        Validation.SafeRelativePath(relative, "dotnet-stage0-runtime globalJson");
        var document = JsonIO.LoadObject(Path.Combine(root, relative), "global.json");
        var sdk = document.RequiredObject("sdk", "global.json");
        Validation.Require(document.Count == 1 && sdk.Count == 3, "global.json must contain only the exact SDK contract.");
        Validation.Require(sdk.RequiredString("version", "global.json.sdk") == "10.0.302" && sdk.RequiredString("rollForward", "global.json.sdk") == "disable" && !sdk.RequiredBool("allowPrerelease", "global.json.sdk"), "global.json must select exact stable .NET SDK 10.0.302 with roll-forward disabled.");
    }

    private static void ValidateCompilerProject(string root)
    {
        var projectPath = Path.Combine(root, "src", "compiler.csproj");
        var project = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace).Root ?? throw new ReleaseToolException("Stage0 compiler project is empty.");
        var properties = project.Elements("PropertyGroup").Elements().GroupBy(element => element.Name.LocalName).ToDictionary(group => group.Key, group => group.Select(element => element.Value.Trim()).ToArray(), StringComparer.Ordinal);
        Validation.Require(properties.TryGetValue("TargetFramework", out var framework) && framework.SequenceEqual(["net10.0"]), "Stage0 compiler must target exactly net10.0.");
        Validation.Require(properties.TryGetValue("RuntimeFrameworkVersion", out var runtime) && runtime.SequenceEqual(["10.0.10"]), "Stage0 compiler RuntimeFrameworkVersion must be exactly 10.0.10.");
        var packages = project.Descendants("PackageReference").ToArray();
        Validation.Require(packages.Length == 1 && packages[0].Attribute("Include")?.Value == "Antlr4.Runtime.Standard" && packages[0].Attribute("Version")?.Value == "[4.13.1]", "Stage0 compiler managed package closure is not exact.");
    }

    private static void ValidateNuGetConfiguration(string root, string relative)
    {
        Validation.SafeRelativePath(relative, "ANTLR nugetConfig");
        var document = XDocument.Load(Path.Combine(root, relative)).Root ?? throw new ReleaseToolException("Release NuGet configuration is empty.");
        Validation.Require(document.Name.LocalName == "configuration", "Release NuGet configuration root must be <configuration>.");
        var sources = document.Element("packageSources")?.Elements().ToArray() ?? [];
        Validation.Require(sources.Length == 2 && sources[0].Name.LocalName == "clear" && sources[1].Name.LocalName == "add" && sources[1].Attribute("key")?.Value == "nuget.org" && sources[1].Attribute("value")?.Value == "https://api.nuget.org/v3/index.json", "Release NuGet source must be the exact nuget.org v3 endpoint.");
        var signature = document.Element("config")?.Elements("add").SingleOrDefault();
        Validation.Require(signature?.Attribute("key")?.Value == "signatureValidationMode" && signature.Attribute("value")?.Value == "require", "Release NuGet restore must require signed packages.");
    }

    private static void ValidateVendor(JsonObject document, IReadOnlyList<ReleaseTarget> targets, string root)
    {
        Validation.Require(document.RequiredInt("schemaVersion", "vendor-packages.json") == 1, "vendor-packages.json schemaVersion must be 1.");
        _ = document.RequiredString("catalogId", "vendor-packages.json");
        var packages = document.RequiredArray("packages", "vendor-packages.json").OfType<JsonObject>().ToArray();
        var ids = packages.Select(package => package.RequiredString("id", "Vendor package")).ToArray();
        Validation.Unique(ids, "Vendor package IDs");
        Validation.Require(ExpectedVendorPackages.SetEquals(ids), "Official Vendor catalog is incomplete or contains an unknown package.");
        var targetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        var allModules = new List<string>();
        var dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var id = package.RequiredString("id", "Vendor package");
            Validation.Require(id.StartsWith("Vendor.", StringComparison.Ordinal), $"Invalid Vendor package ID '{id}'.");
            foreach (var field in new[] { "version", "upstreamUrl", "sourceIdentity", "license", "buildRecipe", "nativePayloadOwner" })
            {
                _ = package.RequiredString(field, id);
            }

            var recipe = package.RequiredString("buildRecipe", id);
            Validation.SafeRelativePath(recipe, $"{id} buildRecipe");
            Validation.Require(File.Exists(Path.Combine(root, recipe)), $"{id} build recipe does not exist: {recipe}");
            var modules = Validation.Strings(package["modules"], $"{id} modules", nonEmpty: true);
            Validation.Require(modules.All(module => module.StartsWith("Vendor.", StringComparison.Ordinal)), $"{id} has an invalid module.");
            allModules.AddRange(modules);
            var packageDependencies = package["dependencies"] is null ? [] : Validation.Strings(package["dependencies"], $"{id} dependencies");
            Validation.Require(packageDependencies.All(dependency => ExpectedVendorPackages.Contains(dependency) && dependency != id), $"{id} has an invalid dependency.");
            dependencies[id] = packageDependencies;
            owners[id] = package.RequiredString("nativePayloadOwner", id);
            Validation.Require(ExpectedVendorPackages.Contains(owners[id]), $"{id} nativePayloadOwner is unknown.");

            var support = package.RequiredObject("targetSupport", id);
            Validation.Require(support.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(targetIds), $"{id} targetSupport must cover all six targets.");
            foreach (var item in support)
            {
                var state = Validation.String(item.Value, $"{id} targetSupport.{item.Key}");
                Validation.Require(state is "required-binary" or "required-source-build", $"{id} has invalid target support state.");
            }

            var evidence = Validation.Strings(package["licenseEvidencePaths"], $"{id} licenseEvidencePaths", nonEmpty: true);
            foreach (var path in evidence.Where(path => !path.StartsWith("archive:", StringComparison.Ordinal)))
            {
                Validation.SafeRelativePath(path, $"{id} license evidence");
                Validation.Require(File.Exists(Path.Combine(root, path)), $"{id} license evidence does not exist: {path}");
            }

            if (package["sourceFiles"] is JsonArray sourceFiles)
            {
                foreach (var source in sourceFiles.OfType<JsonObject>())
                {
                    var path = source.RequiredString("path", $"{id} source file");
                    Validation.SafeRelativePath(path, $"{id} source file");
                    var expected = source.RequiredString("sha256", $"{id} source file").ToLowerInvariant();
                    Validation.Require(Validation.IsSha256(expected) && File.Exists(Path.Combine(root, path)) && JsonIO.Sha256File(Path.Combine(root, path)) == expected, $"{id} source checksum mismatch: {path}");
                }
            }

            var binaryInputs = package["binaryInputs"] is JsonArray inputs ? inputs.OfType<JsonObject>().ToArray() : [];
            var binaryTargets = binaryInputs.Select(item => item.RequiredString("target", $"{id} binary input")).ToArray();
            Validation.Unique(binaryTargets, $"{id} binary targets");
            foreach (var input in binaryInputs)
            {
                var target = input.RequiredString("target", $"{id} binary input");
                Validation.Require(targetIds.Contains(target), $"{id} refers to unknown binary target {target}.");
                _ = input.RequiredString("url", $"{id}/{target}");
                Validation.Require(Validation.IsSha256(input.RequiredString("sha256", $"{id}/{target}")), $"{id}/{target} binary SHA-256 is invalid.");
            }

            var requiredBinary = support.Where(item => Validation.String(item.Value, $"{id} support") == "required-binary").Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            Validation.Require(requiredBinary.SetEquals(binaryTargets), $"{id} binary inputs do not match targetSupport.");
            var linkFacts = package.RequiredObject("systemLinkFacts", id);
            Validation.Require(linkFacts.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["linux", "windows", "macos"]), $"{id} systemLinkFacts must cover each OS.");
            foreach (var item in linkFacts) _ = Validation.Strings(item.Value, $"{id} link facts {item.Key}");
            if (package["acquisitionManifest"] is JsonValue manifest && manifest.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                var path = manifest.GetValue<string>();
                Validation.SafeRelativePath(path, $"{id} acquisitionManifest");
                Validation.Require(File.Exists(Path.Combine(root, path)), $"{id} acquisition manifest does not exist.");
            }
        }

        Validation.Unique(allModules, "Vendor module ownership");
        Validation.Require(dependencies["Vendor.Raymath"].SequenceEqual(["Vendor.Raylib"]) && dependencies["Vendor.Rlgl"].SequenceEqual(["Vendor.Raylib"]), "Raymath and Rlgl must depend exactly on Vendor.Raylib.");
        foreach (var owner in owners)
        {
            Validation.Require(owner.Key == owner.Value || dependencies[owner.Key].Contains(owner.Value), $"{owner.Key} nativePayloadOwner must be itself or a direct dependency.");
            Validation.Require(owners[owner.Value] == owner.Value, $"{owner.Key} nativePayloadOwner {owner.Value} must own its payload directly.");
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) Visit(id);
        void Visit(string id)
        {
            Validation.Require(visiting.Add(id), $"Vendor package dependency cycle includes {id}.");
            if (visited.Contains(id))
            {
                visiting.Remove(id);
                return;
            }

            foreach (var dependency in dependencies[id]) Visit(dependency);
            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static void ValidateArchiveContent(JsonObject document, IReadOnlyList<ReleaseTarget> targets, string root)
    {
        Validation.Require(document.RequiredInt("schemaVersion", "archive-content.json") == 1, "archive-content.json schemaVersion must be 1.");
        var schema = document.RequiredString("$schema", "archive-content.json");
        Validation.SafeRelativePath(schema, "archive-content.json $schema");
        Validation.Require(File.Exists(Path.Combine(root, "eng", "release", schema)), "Archive-content schema is missing.");
        Validation.Require(document.RequiredString("topLevelDirectory", "archive-content.json") == "asset-base-name", "Archive top-level directory policy is invalid.");
        var entries = document.RequiredArray("entries", "archive-content.json").OfType<JsonObject>().ToArray();
        Validation.Require(entries.Length != 0, "Archive content entries must be non-empty.");
        var ids = entries.Select(entry => entry.RequiredString("id", "archive entry")).ToArray();
        Validation.Unique(ids, "archive entry IDs");
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var id = entry.RequiredString("id", "archive entry");
            Validation.Require(Validation.IsIdentifier(id), $"Invalid archive entry ID '{id}'.");
            var path = entry.RequiredString("path", id);
            Validation.SafeRelativePath(path, $"{id} path");
            Validation.Require(entry.RequiredString("pathType", id) is "exact" or "tree", $"{id} pathType is invalid.");
            Validation.Require(entry.RequiredString("kind", id) is "file" or "directory", $"{id} kind is invalid.");
            var owner = entry.RequiredString("owner", id);
            Validation.Require(new[] { "compiler", "sdk", "system", "vendor-catalog", "private-backend", "documentation", "release" }.Contains(owner), $"{id} owner is invalid.");
            owners.Add(owner);
            Validation.Require(entry.RequiredString("mode", id) is "0644" or "0755", $"{id} mode is invalid.");
            Validation.Require(new[] { "release-sha256", "sdk-sha256", "metadata", "excluded" }.Contains(entry.RequiredString("checksumClass", id)), $"{id} checksum class is invalid.");
            _ = entry.RequiredString("description", id);
            if (entry["targets"] is JsonArray selected)
            {
                var selectedTargets = Validation.Strings(selected, $"{id} targets", nonEmpty: true);
                Validation.Require(selectedTargets.All(targetIds.Contains), $"{id} refers to an unknown target.");
            }
            else
            {
                Validation.Require(Validation.String(entry["targets"], $"{id} targets") == "all", $"{id} targets are invalid.");
            }
        }

        Validation.Require(owners.SetEquals(["compiler", "sdk", "system", "vendor-catalog", "private-backend", "documentation", "release"]), "Archive content omits a required ownership class.");
        foreach (var target in targets)
        {
            Validation.Require(entries.Any(entry => entry.RequiredString("path", "archive entry") == $"bin/{target.CompilerExecutable}"), $"Archive content has no compiler command for {target.Id}.");
        }
    }

    private static void ValidateRepositoryContent(JsonObject document, string root)
    {
        var content = document.RequiredObject("repositoryContent", "archive-content.json");
        var forbidden = Validation.Strings(content["forbiddenExtensions"], "repository content forbiddenExtensions", nonEmpty: true).ToHashSet(StringComparer.Ordinal);
        var trees = content.RequiredArray("trees", "repositoryContent").OfType<JsonObject>().ToArray();
        Validation.Require(trees.Length != 0, "Repository content trees must be non-empty.");
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var portable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var id = tree.RequiredString("id", "repository content tree");
            var source = tree.RequiredString("source", id);
            var destination = tree.RequiredString("destination", id);
            Validation.SafeRelativePath(source, $"{id} source");
            Validation.SafeRelativePath(destination, $"{id} destination");
            selectedRoots.Add(destination.Split('/')[0]);
            var sourceRoot = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar));
            Validation.Require(Directory.Exists(sourceRoot), $"Repository content source is missing: {source}");
            var extensions = Validation.Strings(tree["includeExtensions"], $"{id} includeExtensions").ToHashSet(StringComparer.Ordinal);
            var explicitFiles = Validation.Strings(tree["includeFiles"], $"{id} includeFiles").ToHashSet(StringComparer.Ordinal);
            Validation.Require(extensions.Count != 0 || explicitFiles.Count != 0, $"{id} has no inclusion allowlist.");
            foreach (var file in explicitFiles)
            {
                Validation.SafeRelativePath(file, $"{id} explicit file");
                Validation.Require(File.Exists(Path.Combine(sourceRoot, file.Replace('/', Path.DirectorySeparatorChar))), $"{id} explicit file is missing: {file}");
            }

            var selected = 0;
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                Validation.Require(info.LinkTarget is null, $"Repository content source must not contain a symlink: {file}");
                var relative = Path.GetRelativePath(sourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (!explicitFiles.Contains(relative) && !extensions.Contains(extension)) continue;
                Validation.Require(!forbidden.Contains(extension) && !HasNativeMagic(file), $"Repository content selected a forbidden native artifact: {relative}");
                var staged = $"{destination}/{relative}";
                Validation.Require(destinations.Add(staged) && portable.Add(staged), $"Repository content destination is duplicate or case-colliding: {staged}");
                selected++;
            }

            Validation.Require(selected > 0 && explicitFiles.All(file => destinations.Contains($"{destination}/{file}")), $"Repository content tree {id} did not select its complete allowlist.");
        }

        Validation.Require(selectedRoots.IsSupersetOf(["docs", "examples"]), "Repository content must curate both docs and examples.");
        var approvedRoots = Validation.Strings(document.RequiredObject("nativeBinaryPolicy", "archive-content.json")["approvedRoots"], "native binary approvedRoots", nonEmpty: true);
        Validation.Require(approvedRoots.ToHashSet(StringComparer.Ordinal).SetEquals(["bin", "stdlib", "toolchain", "vendor"]), "Native binary approvedRoots must be exactly bin, stdlib, toolchain, and vendor.");
    }

    private static void ValidateMetadataTemplate(JsonObject document)
    {
        Validation.Require(document.RequiredInt("schemaVersion", "release metadata template") == 1, "Release metadata template schemaVersion must be 1.");
        Validation.Require(document.RequiredString("outputPath", "release metadata template") == "release.json", "Release metadata template must generate release.json.");
        var staticValues = document.RequiredObject("staticValues", "release metadata template");
        Validation.Require(staticValues.RequiredInt("releaseSchemaVersion", "release metadata staticValues") == 2 && staticValues.RequiredString("architecturePolicy", "release metadata staticValues") == "64-bit-only" && staticValues.RequiredString("distributionModel", "release metadata staticValues") == "odin-compatible-compiler-sdk", "Release metadata static values are inconsistent.");
        var bindings = document.RequiredArray("bindings", "release metadata template").OfType<JsonObject>().ToArray();
        var outputs = bindings.Select(binding => binding.RequiredString("output", "release metadata binding")).ToArray();
        Validation.Unique(outputs, "release metadata outputs");
        Validation.Require(RequiredMetadataOutputs.IsSubsetOf(outputs), "Release metadata template is missing required outputs.");
        foreach (var binding in bindings)
        {
            _ = binding.RequiredString("source", $"release metadata source for {binding.RequiredString("output", "binding")}");
            Validation.Require(binding.RequiredBool("required", "release metadata binding"), "Every release metadata binding must be required.");
        }

        var defaults = document.RequiredObject("installerDefaults", "release metadata template");
        Validation.Require(!defaults.RequiredBool("networkRequiredForSdkPayload", "installerDefaults") && defaults.RequiredBool("preserveRelocatableTree", "installerDefaults"), "Installer defaults must preserve an offline relocatable SDK.");
    }

    private static bool HasNativeMagic(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        var length = stream.Read(header);
        var bytes = header[..length];
        if (bytes.StartsWith(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }) || bytes.StartsWith(new byte[] { (byte)'M', (byte)'Z' }) || bytes.SequenceEqual("!<arch>\n"u8)) return true;
        if (length < 4) return false;
        var magic = Convert.ToHexString(bytes[..4]).ToLowerInvariant();
        return magic is "feedface" or "cefaedfe" or "feedfacf" or "cffaedfe" or "cafebabe" or "bebafeca" or "cafebabf" or "bfbafeca";
    }
}
