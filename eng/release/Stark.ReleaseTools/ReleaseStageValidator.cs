using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace Stark.ReleaseTools;

internal static partial class ReleaseStageValidator
{
    public static JsonObject Run(CommandLine command)
    {
        command.RejectUnknown("--sdk-root", "--target-id", "--config-root", "--output");
        var root = Path.GetFullPath(command.Required("--sdk-root"));
        var target = command.Required("--target-id");
        var configRoot = Path.GetFullPath(command.Optional("--config-root", Path.Combine("eng", "release")));
        var report = Validate(root, target, configRoot);
        var output = command.OptionalNullable("--output");
        if (output is not null) JsonIO.Write(output, report);
        return report;
    }

    public static JsonObject Validate(string root, string targetId, string configRoot)
    {
        Validation.Require(Directory.Exists(root) && new DirectoryInfo(root).LinkTarget is null, $"SDK root does not exist or is a link: {root}");
        var release = JsonIO.LoadObject(Path.Combine(root, "release.json"), "staged release.json");
        var sdk = JsonIO.LoadObject(Path.Combine(root, "sdk.json"), "staged sdk.json");
        var content = JsonIO.LoadObject(Path.Combine(configRoot, "archive-content.json"), "archive-content.json");
        var catalog = JsonIO.LoadObject(Path.Combine(configRoot, "vendor-packages.json"), "vendor-packages.json");
        var licenseCounts = ManagedLicenseEvidence.ValidateStagedInventory(Path.Combine(root, "licenses", "managed"), targetId, Path.Combine(configRoot, "managed-license-evidence.json"));
        ValidateReleaseIdentity(root, targetId, release);
        ValidateManagedLicenseMetadata(root, release, licenseCounts);
        var requiredEntries = ValidateRequiredContent(root, targetId, content);
        var verifiedFiles = ValidateChecksumManifest(root);
        ValidateSymlinks(root);
        var documentationLinks = ValidateMarkdownLinks(root);
        var (packageCount, moduleCount) = ValidateSdkCatalog(root, targetId, sdk, catalog);
        var validated = CandidateIdentity.Inspect(root, targetId);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = "ok",
            ["validationScope"] = "release-candidate",
            ["targetId"] = targetId,
            ["sdkRoot"] = new DirectoryInfo(root).Name,
            ["requiredEntries"] = requiredEntries,
            ["verifiedFiles"] = verifiedFiles,
            ["verifiedDocumentationLinks"] = documentationLinks,
            ["sdkPackages"] = packageCount,
            ["sdkModules"] = moduleCount,
            ["officialVendorPackages"] = catalog.RequiredArray("packages", "vendor catalog").Count,
            ["managedLicensePackages"] = licenseCounts["packages"]!.GetValue<int>(),
            ["managedLicenseFiles"] = licenseCounts["licenseFiles"]!.GetValue<int>(),
            ["validatedCandidate"] = validated,
        };
    }

    private static void ValidateReleaseIdentity(string root, string targetId, JsonObject release)
    {
        Validation.Require(release.RequiredInt("schemaVersion", "release.json") == 2 && release.RequiredString("assetSuffix", "release.json") == targetId, "release.json schema or target identity is invalid.");
        var version = release.RequiredString("starkVersion", "release.json");
        Validation.Require(new DirectoryInfo(root).Name == $"stark-{version}-{targetId}" && release.RequiredString("releaseVersion", "release.json") == version, "Stage root does not match release identity.");
        var source = release.RequiredObject("source", "release.json");
        Validation.Require(source.Count == 3, "release.json source identity contains missing or environment-dependent facts.");
        var commit = source.RequiredString("commit", "release source");
        Validation.Require(SourceCommit().IsMatch(commit) && source.RequiredString("commitHashAlgorithm", "release source") == (commit.Length == 40 ? "sha1" : "sha256") && !source.RequiredBool("trackedWorkingTreeDirty", "release source") && release.RequiredString("gitCommit", "release.json") == commit, "release.json source identity is inconsistent.");
        var build = release.RequiredObject("buildIdentity", "release.json");
        Validation.Require(build.RequiredString("kind", "release build identity") == "content-addressed-release-build" && ContentIdentity().IsMatch(build.RequiredString("identity", "release build identity")) && JsonNode.DeepEquals(release["workflowIdentity"], build), "release.json build identity is invalid.");
        var options = release.RequiredObject("buildOptions", "release.json");
        Validation.Require(options.RequiredString("configuration", "release build options") == "Release" && options.RequiredString("packageProfile", "release build options") == "release" && options.RequiredString("architecturePolicy", "release build options") == "64-bit-only", "release.json build options are invalid.");
        var tool = options.RequiredObject("archiveContainerTool", "release build options");
        ValidateTool(tool);

        var configuration = release.RequiredObject("configuration", "release.json");
        Validation.Require(configuration.RequiredString("identityKind", "release configuration") == "stark-release-configuration" && configuration.RequiredString("algorithm", "release configuration") == "sha256-ordinal-path-size-content-v1", "release.json configuration identity is invalid.");
        var configurationFiles = configuration.RequiredArray("files", "release configuration").OfType<JsonObject>().ToArray();
        Validation.Require(configurationFiles.Length != 0, "release.json configuration file hashes are missing.");
        var paths = new List<string>();
        var digest = new StringBuilder();
        foreach (var entry in configurationFiles)
        {
            var path = entry.RequiredString("path", "release configuration entry");
            Validation.SafeRelativePath(path, "release configuration path");
            var bytes = entry["bytes"]!.GetValue<long>();
            var sha = entry.RequiredString("sha256", "release configuration entry");
            Validation.Require(bytes >= 0 && Validation.IsSha256(sha), "release configuration entry is invalid.");
            paths.Add(path);
            digest.Append(path).Append('\0').Append(bytes).Append('\0').Append(sha).Append('\n');
        }

        Validation.Require(paths.SequenceEqual(paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), "release configuration paths are not unique and ordinally sorted.");
        var configurationSha = JsonIO.Sha256(Encoding.UTF8.GetBytes(digest.ToString()));
        Validation.Require(configuration.RequiredString("packagingInputsSha256", "release configuration") == configurationSha && configuration.RequiredString("sha256", "release configuration") == configurationSha && build.RequiredString("configurationSha256", "release build identity") == configurationSha, "release configuration hashes disagree.");
        var planNode = build["releasePlanSha256"];
        var planSha = planNode is null ? string.Empty : Validation.String(planNode, "release plan SHA-256");
        Validation.Require(planSha.Length == 0 || Validation.IsSha256(planSha), "release build plan hash is invalid.");
        var manifest = tool.RequiredObject("manifest", "release tool");
        var configurationTool = configurationFiles.SingleOrDefault(entry => entry.RequiredString("path", "release configuration entry") == "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj");
        Validation.Require(configurationTool is not null && manifest.RequiredString("sha256", "release tool manifest") == configurationTool.RequiredString("sha256", "release tool configuration entry"), "Release tool is not bound to the release configuration identity.");
        var expectedFacts = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["commit"] = commit,
            ["releaseVersion"] = version,
            ["targetId"] = targetId,
            ["configurationSha256"] = configurationSha,
            ["releasePlanSha256"] = planSha.Length == 0 ? null : planSha,
            ["archiveTool"] = tool.DeepClone(),
        };
        Validation.Require(JsonNode.DeepEquals(build["identityFacts"], expectedFacts), "release.json content-addressed build identity facts disagree.");
        Validation.Require(build.RequiredString("identity", "release build identity") == CandidateEvidenceBinder.ComputeBuildIdentity(commit, version, targetId, configurationSha, planSha, tool), "release.json content-addressed build identity digest mismatch.");

        var schemas = release.RequiredObject("schemas", "release.json");
        Validation.Require(schemas.RequiredInt("releaseMetadata", "release schemas") == 2 && schemas.RequiredInt("sdkManifest", "release schemas") > 0 && schemas.RequiredInt("packageImageFormat", "release schemas") > 0 && schemas.RequiredInt("vendorReleaseInput", "release schemas") == 2 && schemas.RequiredInt("releaseTools", "release schemas") == 1, "release.json schema facts are invalid.");
        var targetFacts = release.RequiredObject("targetFacts", "release.json");
        var releaseTarget = targetFacts.RequiredObject("release", "release target facts");
        var sdkTarget = targetFacts.RequiredObject("sdk", "release target facts");
        Validation.Require(releaseTarget.RequiredString("id", "release target facts") == targetId && sdkTarget.RequiredString("id", "SDK target facts") == targetId && releaseTarget.RequiredString("targetTriple", "release target facts") == release.RequiredString("defaultTargetTriple", "release.json") && sdkTarget.RequiredString("llvmTriple", "SDK target facts") == release.RequiredString("defaultTargetTriple", "release.json"), "release.json target facts disagree.");
        var sdkIdentity = release.RequiredObject("sdk", "release.json");
        Validation.Require(sdkIdentity.RequiredString("path", "release SDK identity") == "sdk.json" && sdkIdentity.RequiredInt("schemaVersion", "release SDK identity") == schemas.RequiredInt("sdkManifest", "release schemas") && sdkIdentity.RequiredInt("packageFormatVersion", "release SDK identity") == schemas.RequiredInt("packageImageFormat", "release schemas") && JsonIO.Sha256File(Path.Combine(root, "sdk.json")) == sdkIdentity.RequiredString("sha256", "release SDK identity"), "release.json SDK identity differs.");
        ValidateCompilerPrivateBackend(root, release, targetId);
        ValidateReleasePackages(root, release, schemas);
        ValidateReleaseDependencies(root, release, targetId);
        ValidateReleaseVendor(root, release);
        var identities = release.RequiredObject("contentIdentities", "release.json");
        foreach (var name in new[] { "compilerRuntime", "standardLibrary", "vendor", "compilerPrivateBackend" }) ValidateContentIdentity(root, identities[name], $"release.json {name}");
        Validation.Require(release.RequiredString("contentChecksumManifest", "release.json") == "release-files.sha256", "release.json does not select release-files.sha256.");
        foreach (var entry in release.RequiredArray("files", "release.json").OfType<JsonObject>())
        {
            var relative = entry.RequiredString("path", "release file inventory");
            var path = SdkPath(root, relative, "release file inventory");
            Validation.Require(File.Exists(path) && new FileInfo(path).Length == entry["bytes"]!.GetValue<long>() && JsonIO.Sha256File(path) == entry.RequiredString("sha256", "release file inventory"), $"release.json inventory mismatch: {relative}");
        }
    }

    private static void ValidateTool(JsonObject tool)
    {
        Validation.Require(tool.RequiredString("implementation", "release tool") == ReleaseToolIdentity.Implementation && tool.RequiredString("targetFramework", "release tool") == ReleaseToolIdentity.TargetFramework && tool.RequiredString("dotnetSdkVersion", "release tool") == ReleaseToolIdentity.DotNetSdkVersion && tool.RequiredString("dotnetRuntimeVersion", "release tool") == ReleaseToolIdentity.DotNetRuntimeVersion, "release archive-tool identity is unsupported.");
        var manifest = tool.RequiredObject("manifest", "release tool");
        Validation.Require(manifest.RequiredString("path", "release tool manifest") == "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj" && manifest.RequiredInt("schemaVersion", "release tool manifest") == 1 && Validation.IsSha256(manifest.RequiredString("sha256", "release tool manifest")), "release tool manifest identity is invalid.");
        var assembly = tool.RequiredObject("assembly", "release tool");
        Validation.Require(assembly["bytes"]!.GetValue<long>() > 0 && Validation.IsSha256(assembly.RequiredString("sha256", "release tool assembly")), "release tool assembly identity is invalid.");
    }

    private static void ValidateCompilerPrivateBackend(string root, JsonObject release, string targetId)
    {
        var metadata = release.RequiredObject("compilerPrivateBackend", "release.json");
        Validation.Require(metadata.RequiredString("kind", "compiler private backend") == "stark-compiler-private-backend" && metadata.RequiredInt("schemaVersion", "compiler private backend") == 2, "release compiler-private backend identity is invalid.");
        Validation.Require(metadata.RequiredString("llvmVersion", "compiler private backend") == release.RequiredString("llvmVersion", "release.json"), "release compiler-private LLVM version differs.");
        var path = metadata.RequiredString("path", "compiler private backend");
        Validation.SafeRelativePath(path, "compiler private backend path");
        Validation.Require(release.RequiredObject("paths", "release.json").RequiredString("compilerPrivateBackend", "release paths") == path, "release compiler-private backend path differs.");
        var manifestRelative = metadata.RequiredString("manifest", "compiler private backend");
        Validation.Require(manifestRelative == path + "/manifest.json", "release compiler-private backend manifest path is inconsistent.");
        var manifestPath = SdkPath(root, manifestRelative, "compiler private backend manifest");
        Validation.Require(File.Exists(manifestPath) && JsonIO.Sha256File(manifestPath) == metadata.RequiredString("manifestSha256", "compiler private backend"), "release compiler-private backend manifest hash differs.");
        var manifest = JsonIO.LoadObject(manifestPath, "compiler private backend manifest");
        Validation.Require(manifest.RequiredString("assetSuffix", "compiler private backend manifest") == targetId && manifest.RequiredString("llvmVersion", "compiler private backend manifest") == metadata.RequiredString("llvmVersion", "compiler private backend"), "compiler-private backend manifest target or version differs.");
        var sourceSha = manifest.RequiredObject("sourceArchive", "compiler private backend manifest").RequiredString("sha256", "compiler private backend source");
        Validation.Require(Validation.IsSha256(sourceSha) && metadata.RequiredString("sourceArchiveSha256", "compiler private backend") == sourceSha, "compiler-private backend source identity differs.");

        var acquisition = metadata.RequiredString("acquisitionKind", "compiler private backend");
        Validation.Require(manifest.RequiredString("acquisitionKind", "compiler private backend manifest") == acquisition, "compiler-private backend acquisition kind differs.");
        if (acquisition == "upstream-archive")
        {
            var binarySha = manifest.RequiredObject("binaryArchive", "compiler private backend manifest").RequiredString("sha256", "compiler private backend binary archive");
            Validation.Require(Validation.IsSha256(binarySha) && metadata.RequiredString("binaryArchiveSha256", "compiler private backend") == binarySha && metadata["sourceBuild"] is null && manifest["sourceBuild"] is null, "compiler-private upstream archive provenance differs.");
        }
        else
        {
            Validation.Require(acquisition == "pinned-source-build" && targetId == "macos-x64" && metadata["binaryArchiveSha256"] is null && manifest["binaryArchive"] is null, "compiler-private source-build acquisition is invalid.");
            var metadataBuild = metadata.RequiredObject("sourceBuild", "compiler private backend");
            var manifestBuild = manifest.RequiredObject("sourceBuild", "compiler private backend manifest");
            Validation.Require(JsonNode.DeepEquals(metadataBuild, manifestBuild), "compiler-private source-build provenance differs between release and backend manifests.");
            Validation.Require(metadataBuild.RequiredString("recipeKind", "compiler private source build") == "pinned-source-build" && metadataBuild.RequiredString("configuration", "compiler private source build") == "Release" && metadataBuild.RequiredString("optimization", "compiler private source build") == "O3" && metadataBuild.RequiredString("lto", "compiler private source build") == "Thin", "compiler-private source build lost its optimization contract.");
            var buildTools = metadataBuild.RequiredObject("buildTools", "compiler private source build");
            foreach (var name in new[] { "cmake", "ninja" })
            {
                var tool = buildTools.RequiredObject(name, "compiler private source-build tools");
                _ = tool.RequiredString("version", $"compiler private source-build {name}");
                Validation.Require(Validation.IsSha256(tool.RequiredString("sha256", $"compiler private source-build {name}")), $"compiler-private source-build {name} hash is invalid.");
            }

            var appleToolchain = metadataBuild.RequiredObject("appleToolchain", "compiler private source build");
            foreach (var name in new[] { "clangSha256", "clangxxSha256" })
            {
                Validation.Require(Validation.IsSha256(appleToolchain.RequiredString(name, "compiler private Apple toolchain")), $"compiler-private Apple toolchain {name} is invalid.");
            }

            foreach (var name in new[] { "xcodeVersion", "sdkVersion", "clangVersion" })
            {
                Validation.Require(!string.IsNullOrWhiteSpace(appleToolchain.RequiredString(name, "compiler private Apple toolchain")), $"compiler-private Apple toolchain {name} is empty.");
            }
        }

        Validation.Require(metadata["fileCount"] is JsonValue count && count.TryGetValue<long>(out var files) && files > 0, "compiler-private backend file count is invalid.");
        Validation.Require(metadata["logicalBytes"] is JsonValue bytes && bytes.TryGetValue<long>(out var logicalBytes) && logicalBytes > 0, "compiler-private backend byte count is invalid.");
        Validation.Require(Validation.IsSha256(metadata.RequiredString("runtimeClosureManifestSha256", "compiler private backend")), "compiler-private backend runtime closure identity is invalid.");
    }

    private static void ValidateReleasePackages(string root, JsonObject release, JsonObject schemas)
    {
        var packages = release.RequiredArray("packages", "release.json").OfType<JsonObject>().ToArray();
        var facts = release.RequiredArray("packageSchemaFacts", "release.json").OfType<JsonObject>().ToArray();
        var packagesById = packages.ToDictionary(item => item.RequiredString("id", "release package"), StringComparer.Ordinal);
        var factsById = facts.ToDictionary(item => item.RequiredString("id", "release package fact"), StringComparer.Ordinal);
        Validation.Require(packagesById.Count == packages.Length && factsById.Count == facts.Length && packagesById.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(factsById.Keys), "release package inventories disagree.");
        foreach (var fact in facts)
        {
            var id = fact.RequiredString("id", "release package fact");
            var package = packagesById[id];
            var image = fact.RequiredString("image", id);
            Validation.Require(fact.RequiredInt("formatVersion", id) == schemas.RequiredInt("packageImageFormat", "release schemas") && image == package.RequiredString("image", id) && fact.RequiredString("imageSha256", id) == package.RequiredString("imageSha256", id) && fact.RequiredString("apiHash", id) == package.RequiredString("apiHash", id) && fact.RequiredString("contentHash", id) == package.RequiredString("contentHash", id), $"release package schema facts disagree: {id}");
            var path = SdkPath(root, image, $"release package {id}");
            Validation.Require(File.Exists(path) && JsonIO.Sha256File(path) == fact.RequiredString("imageSha256", id), $"release package image hash mismatch: {id}");
            var header = File.ReadAllBytes(path).Take(12).ToArray();
            Validation.Require(header.Length == 12 && header.AsSpan(0, 8).SequenceEqual("STARKPKG"u8) && BitConverter.ToInt32(header, 8) == fact.RequiredInt("formatVersion", id), $"release package header mismatch: {id}");
        }
    }

    private static void ValidateReleaseDependencies(string root, JsonObject release, string targetId)
    {
        var dependencies = release.RequiredObject("dependencies", "release.json");
        Validation.Require(Validation.IsSha256(dependencies.RequiredObject("manifest", "release dependencies").RequiredString("sha256", "dependency manifest")), "release dependency manifest hash is invalid.");
        foreach (var dependency in dependencies.RequiredArray("selected", "release dependencies").OfType<JsonObject>())
        {
            var id = dependency.RequiredString("id", "release dependency");
            Validation.Require(Validation.IsSha256(dependency.RequiredString("declarationSha256", id)) && Validation.IsSha256(dependency.RequiredString("selectionSha256", id)), $"release dependency hashes are invalid: {id}");
            if (dependency["sourceSha256"] is JsonValue source) Validation.Require(Validation.IsSha256(source.GetValue<string>()), $"release dependency source hash is invalid: {id}");
            Validation.Require(dependency.RequiredObject("selection", id).RequiredString("target", id) == targetId, $"release dependency target mismatch: {id}");
            if (dependency["acquisitionManifest"] is JsonObject manifest)
            {
                Validation.SafeRelativePath(manifest.RequiredString("path", id), $"dependency acquisition manifest {id}");
                Validation.Require(Validation.IsSha256(manifest.RequiredString("sha256", id)), $"dependency acquisition manifest hash is invalid: {id}");
            }

            ValidateContentIdentity(root, dependency["contentIdentity"], $"release dependency {id}");
        }
    }

    private static void ValidateReleaseVendor(string root, JsonObject release)
    {
        var vendor = release.RequiredObject("vendorCatalog", "release.json");
        var stagedPath = vendor.RequiredString("stagedPath", "release Vendor catalog");
        var staged = SdkPath(root, stagedPath, "release Vendor catalog");
        Validation.Require(File.Exists(staged) && JsonIO.Sha256File(staged) == vendor.RequiredString("stagedSha256", "release Vendor catalog"), "release staged Vendor catalog hash differs.");
        var stagedIds = JsonIO.LoadObject(staged, "staged Vendor catalog").RequiredArray("packages", "staged Vendor catalog").OfType<JsonObject>().Select(item => item.RequiredString("id", "staged Vendor package")).ToHashSet(StringComparer.Ordinal);
        var input = vendor.RequiredObject("releaseInput", "release Vendor catalog");
        Validation.Require(input.RequiredInt("schemaVersion", "Vendor release input") == 2, "Vendor release-input schema is invalid.");
        var inputPath = Path.Combine(root, "vendor", "release-input.json");
        Validation.Require(JsonIO.Sha256File(inputPath) == input.RequiredString("sha256", "Vendor release input"), "Vendor release-input hash differs.");
        var inputIds = JsonIO.LoadObject(inputPath, "Vendor release-input").RequiredArray("packages", "Vendor release-input").OfType<JsonObject>().Select(item => item.RequiredString("id", "Vendor release-input package")).ToHashSet(StringComparer.Ordinal);
        var selected = vendor.RequiredArray("selectedPackages", "release Vendor catalog").OfType<JsonObject>().ToArray();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in selected)
        {
            var id = package.RequiredString("id", "selected Vendor package");
            Validation.Require(id.StartsWith("Vendor.", StringComparison.Ordinal) && selectedIds.Add(id) && Validation.IsSha256(package.RequiredString("declarationSha256", id)) && Validation.IsSha256(package.RequiredString("releaseContributionSha256", id)), $"selected Vendor package identity is invalid: {id}");
            _ = package.RequiredString("targetSupport", id);
            var recipe = package.RequiredObject("buildRecipe", id);
            Validation.SafeRelativePath(recipe.RequiredString("path", id), $"Vendor build recipe {id}");
            Validation.Require(Validation.IsSha256(recipe.RequiredString("sha256", id)), $"Vendor build recipe hash is invalid: {id}");
        }

        Validation.Require(stagedIds.SetEquals(inputIds) && stagedIds.SetEquals(selectedIds) && input.RequiredInt("packageCount", "Vendor release input") == selected.Length, "Vendor catalog/release-input/selection package IDs disagree.");
    }

    private static void ValidateContentIdentity(string root, JsonNode? node, string context)
    {
        Validation.Require(node is JsonObject, $"{context} content identity is missing.");
        var identity = (JsonObject)node!;
        var relativeRoot = identity.RequiredString("root", context);
        var contentRoot = SdkPath(root, relativeRoot, context);
        Validation.Require(Directory.Exists(contentRoot), $"{context} content root is missing: {relativeRoot}");
        var files = Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories).OrderBy(path => Path.GetRelativePath(contentRoot, path).Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal).ToArray();
        var digest = new StringBuilder();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(contentRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            digest.Append(relative).Append('\0').Append(new FileInfo(file).Length).Append('\0').Append(JsonIO.Sha256File(file)).Append('\n');
        }

        Validation.Require(identity["fileCount"]!.GetValue<int>() == files.Length && identity["logicalBytes"]!.GetValue<long>() == files.Sum(file => new FileInfo(file).Length) && identity.RequiredString("manifestSha256", context) == JsonIO.Sha256(Encoding.UTF8.GetBytes(digest.ToString())), $"{context} content identity mismatch.");
    }

    private static int ValidateRequiredContent(string root, string targetId, JsonObject content)
    {
        Validation.Require(content.RequiredInt("schemaVersion", "archive-content.json") == 1, "archive-content.json schemaVersion must be 1.");
        var checkedCount = 0;
        foreach (var entry in content.RequiredArray("entries", "archive-content.json").OfType<JsonObject>())
        {
            var targets = entry["targets"];
            var applies = targets is JsonValue ? Validation.String(targets, "archive entry targets") == "all" : Validation.Strings(targets, "archive entry targets").Contains(targetId);
            if (!applies) continue;
            var relative = entry.RequiredString("path", "archive entry");
            var path = SdkPath(root, relative, "archive entry");
            var kind = entry.RequiredString("kind", "archive entry");
            Validation.Require(kind == "file" ? File.Exists(path) : kind == "directory" && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(), $"Required release {kind} is missing or empty: {relative}");
            if (!OperatingSystem.IsWindows() && entry.RequiredString("pathType", "archive entry") == "exact")
            {
                var expected = Convert.ToInt32(entry.RequiredString("mode", "archive entry"), 8);
                var actual = kind == "file" ? (int)File.GetUnixFileMode(path) & 0x1ff : (int)File.GetUnixFileMode(path) & 0x1ff;
                Validation.Require(actual == expected, $"{relative} mode is {FormatUnixMode(actual)}, expected {FormatUnixMode(expected)}.");
            }

            checkedCount++;
        }

        return checkedCount;
    }

    internal static string FormatUnixMode(int mode)
        => "0" + Convert.ToString(mode & 0x1ff, 8).PadLeft(3, '0');

    private static int ValidateChecksumManifest(string root)
    {
        var manifestPath = Path.Combine(root, "release-files.sha256");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        var number = 0;
        foreach (var line in File.ReadAllLines(manifestPath, Encoding.ASCII))
        {
            number++;
            var match = ChecksumLine().Match(line);
            Validation.Require(match.Success, $"release-files.sha256 line {number} is malformed.");
            var relative = match.Groups[2].Value;
            Validation.SafeRelativePath(relative, $"release-files.sha256 line {number}");
            Validation.Require(relative != "release-files.sha256" && expected.TryAdd(relative, match.Groups[1].Value), "release-files.sha256 contains a duplicate or self entry.");
        }

        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => Path.GetFullPath(path) != Path.GetFullPath(manifestPath)).ToDictionary(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal);
        Validation.Require(expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys), "Release checksum inventory differs from staged files.");
        foreach (var item in actual) Validation.Require(JsonIO.Sha256File(item.Value) == expected[item.Key], $"Release file failed SHA-256 verification: {item.Key}");
        return actual.Count;
    }

    private static void ValidateManagedLicenseMetadata(string root, JsonObject release, JsonObject counts)
    {
        var dependencies = release.RequiredObject("dependencies", "release.json");
        var inventory = dependencies.RequiredObject("managedLicenseInventory", "release dependencies");
        var path = Path.Combine(root, "licenses", "managed", "manifest.json");
        Validation.Require(inventory.RequiredString("path", "managed license inventory") == "licenses/managed/manifest.json" && inventory.RequiredInt("schemaVersion", "managed license inventory") == 1 && inventory.RequiredString("manifestKind", "managed license inventory") == "stark-managed-license-inventory" && inventory.RequiredString("sha256", "managed license inventory") == JsonIO.Sha256File(path) && inventory.RequiredInt("packageCount", "managed license inventory") == counts["packages"]!.GetValue<int>() && inventory.RequiredInt("licenseFileCount", "managed license inventory") == counts["licenseFiles"]!.GetValue<int>(), "release.json managed-license inventory metadata differs.");
    }

    private static void ValidateSymlinks(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is null) continue;
            var resolved = info.ResolveLinkTarget(true) ?? throw new ReleaseToolException($"Release symlink is dangling: {Path.GetRelativePath(root, path)}");
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(resolved.FullName);
            Validation.Require(full == fullRoot || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison), $"Release symlink escapes SDK root: {Path.GetRelativePath(root, path)}");
        }
    }

    private static int ValidateMarkdownLinks(string root)
    {
        var violations = new List<string>();
        var checkedCount = 0;
        foreach (var markdown in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var inFence = false;
            string fence = string.Empty;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(markdown, Encoding.UTF8))
            {
                lineNumber++;
                var stripped = line.TrimStart();
                if (stripped.StartsWith("```", StringComparison.Ordinal) || stripped.StartsWith("~~~", StringComparison.Ordinal))
                {
                    var marker = stripped[..3];
                    if (!inFence) { inFence = true; fence = marker; } else if (marker == fence) inFence = false;
                    continue;
                }

                if (inFence) continue;
                var withoutCode = InlineCode().Replace(line, string.Empty);
                var destinations = InlineLink().Matches(withoutCode).Select(match => match.Groups[1].Value.Trim()).ToList();
                var reference = ReferenceLink().Match(withoutCode);
                if (reference.Success) destinations.Add(reference.Groups[1].Value.Trim());
                foreach (var raw in destinations)
                {
                    var destination = raw.StartsWith('<') && raw.Contains('>') ? raw[1..raw.IndexOf('>')] : raw.Split((char[]?)null, 2)[0];
                    if (destination.Length == 0 || destination.StartsWith('#') || destination.StartsWith("//", StringComparison.Ordinal) || UriScheme().IsMatch(destination)) continue;
                    var pathPart = Uri.UnescapeDataString(destination.Split(['?', '#'], 2)[0]);
                    if (pathPart.Length == 0) continue;
                    if (Path.IsPathFullyQualified(pathPart) || pathPart.StartsWith('~') || pathPart.Contains('\0')) { violations.Add($"Link is not archive-relative in {Path.GetRelativePath(root, markdown)}:{lineNumber}: {destination}"); continue; }
                    var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(markdown)!, pathPart.Replace('/', Path.DirectorySeparatorChar)));
                    var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                    if (candidate != fullRoot && !candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison)) { violations.Add($"Link escapes SDK in {Path.GetRelativePath(root, markdown)}:{lineNumber}: {destination}"); continue; }
                    if (!File.Exists(candidate) && !Directory.Exists(candidate)) { violations.Add($"Link target is missing in {Path.GetRelativePath(root, markdown)}:{lineNumber}: {destination}"); continue; }
                    checkedCount++;
                }
            }
        }

        Validation.Require(violations.Count == 0, $"Staged Markdown contains {violations.Count} unresolved local link(s):\n{string.Join('\n', violations)}");
        return checkedCount;
    }

    private static (int Packages, int Modules) ValidateSdkCatalog(string root, string targetId, JsonObject sdk, JsonObject catalog)
    {
        Validation.Require(sdk.RequiredString("kind", "sdk.json") == "release" && sdk.RequiredObject("target", "sdk.json").RequiredString("id", "sdk target") == targetId, "sdk.json release target is invalid.");
        var packages = sdk.RequiredArray("packages", "sdk.json").OfType<JsonObject>().ToArray();
        var modules = sdk.RequiredArray("modules", "sdk.json").OfType<JsonObject>().ToArray();
        var packagesById = packages.ToDictionary(item => item.RequiredString("id", "SDK package"), StringComparer.Ordinal);
        var modulesByName = modules.ToDictionary(item => item.RequiredString("name", "SDK module"), item => item.RequiredString("package", "SDK module"), StringComparer.Ordinal);
        Validation.Require(packagesById.Count == packages.Length && modulesByName.Count == modules.Length, "sdk.json package or module ownership is duplicated.");
        var catalogPackages = catalog.RequiredArray("packages", "Vendor catalog").OfType<JsonObject>().ToArray();
        var catalogById = catalogPackages.ToDictionary(item => item.RequiredString("id", "Vendor package"), StringComparer.Ordinal);
        foreach (var package in catalogPackages)
        {
            var id = package.RequiredString("id", "Vendor package");
            Validation.Require(packagesById.TryGetValue(id, out var selected), $"Official Vendor package is absent from sdk.json: {id}");
            var selectedPackage = selected!;
            ValidatePackageArtifacts(root, id, selectedPackage);
            var expectedDependencies = package["dependencies"] is null ? [] : Validation.Strings(package["dependencies"], $"{id} dependencies");
            var selectedDependencies = selectedPackage.RequiredArray("dependencies", id).OfType<JsonObject>().Select(item => item.RequiredString("id", $"{id} dependency")).Where(catalogById.ContainsKey).ToHashSet(StringComparer.Ordinal);
            Validation.Require(selectedDependencies.SetEquals(expectedDependencies), $"SDK package {id} dependencies differ from catalog.");
            var native = selectedPackage.RequiredObject("native", id);
            var artifacts = Validation.Strings(native["artifacts"], $"{id} native artifacts");
            var licenses = Validation.Strings(native["licenseFiles"], $"{id} license files", nonEmpty: true);
            foreach (var relative in artifacts.Concat(licenses))
            {
                var path = SdkPath(root, relative, $"Vendor package {id}");
                Validation.Require(File.Exists(path) && new FileInfo(path).Length > 0, $"Vendor package {id} native payload is missing: {relative}");
            }

            foreach (var module in Validation.Strings(package["modules"], $"{id} modules", nonEmpty: true)) Validation.Require(modulesByName.TryGetValue(module, out var owner) && owner == id, $"Module {module} is not owned by {id}.");
        }

        Validation.Require(packagesById.TryGetValue("System", out var system), "Official System package is absent from sdk.json.");
        ValidatePackageArtifacts(root, "System", system!);
        Validation.Require(modulesByName.TryGetValue("System", out var systemOwner) && systemOwner == "System", "Module System is not owned by System.");
        return (packages.Length, modules.Length);
    }

    private static void ValidatePackageArtifacts(string root, string id, JsonObject package)
    {
        foreach (var field in new[] { "image", "library" })
        {
            var relative = package.RequiredString(field, $"SDK package {id}");
            var path = SdkPath(root, relative, $"SDK package {id} {field}");
            Validation.Require(File.Exists(path) && new FileInfo(path).Length > 0 && JsonIO.Sha256File(path) == package.RequiredString($"{field}Sha256", $"SDK package {id}"), $"SDK package {id} {field} is missing or has wrong checksum.");
        }
    }

    private static string SdkPath(string root, string relative, string context)
    {
        Validation.SafeRelativePath(relative, context);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        Validation.Require(candidate == fullRoot || candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison), $"{context} escapes the SDK: {relative}");
        return candidate;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex("^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex SourceCommit();
    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex ContentIdentity();
    [GeneratedRegex("^([0-9a-f]{64})  (.+)$")]
    private static partial Regex ChecksumLine();
    [GeneratedRegex("`[^`\\n]*`")]
    private static partial Regex InlineCode();
    [GeneratedRegex("!?\\[[^\\]\\n]*\\]\\(([^)\\n]+)\\)")]
    private static partial Regex InlineLink();
    [GeneratedRegex("^\\s*\\[[^\\]\\n]+\\]:\\s*(\\S+)")]
    private static partial Regex ReferenceLink();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex UriScheme();
}
