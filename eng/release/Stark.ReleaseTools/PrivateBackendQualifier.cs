using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal sealed record PrivateBackendQualificationInput(
    string RepositoryRoot,
    string ToolchainRoot,
    ReleaseTarget Target,
    JsonObject Manifest,
    string[] RequiredTools,
    JsonObject SourceBuildRecipe,
    JsonObject SourceBuildEvidence);

internal static class PrivateBackendQualifier
{
    private static readonly HashSet<string> AllowedToolNames = new(
    [
        "clang", "clang++", "ld.lld", "ld64.lld", "lld", "llvm-ar", "llvm-ranlib",
        "clang.exe", "clang++.exe", "lld-link.exe", "lld.exe", "llvm-ar.exe",
        "llvm-lib.exe", "llvm-ranlib.exe",
    ], StringComparer.Ordinal);

    private static readonly string[] ManifestProperties =
    [
        "schemaVersion", "payloadKind", "llvmVersion", "releaseTag", "releaseUrl",
        "assetSuffix", "runtimeIdentifier", "acquisitionKind", "binaryArchive",
        "sourceArchive", "sourceBuild", "compilerResourceRoots", "requiredTools",
        "requiredPatternMatches", "excludedDevelopmentPatterns", "hardlinkAliases",
        "licenseFiles", "runtimeClosure",
    ];

    private static readonly string[] SourceBuildEvidenceProperties =
    [
        "schemaVersion", "recipeKind", "hostOperatingSystem", "hostArchitecture",
        "minimumDeploymentTarget", "configuration", "optimization", "lto", "generator",
        "sourceSubdirectory", "projects", "targetsToBuild", "buildTarget", "cmakeOptions",
        "sourceDateEpoch", "compileJobs", "parallelLinkJobs", "buildTools", "appleToolchain",
    ];

    public static JsonObject Run(CommandLine command)
    {
        command.RejectUnknown("--root", "--target-id", "--toolchain-root", "--output");
        var repositoryRoot = Path.GetFullPath(command.Optional("--root", Directory.GetCurrentDirectory()));
        var targetId = command.Required("--target-id");
        var toolchainRoot = Path.GetFullPath(command.Required("--toolchain-root"));
        var output = Path.GetFullPath(command.Required("--output"));
        Validation.Require(!IsSameOrDescendant(output, toolchainRoot), "Qualification report must be outside the private backend closure.");

        var input = ValidateManifest(repositoryRoot, targetId, toolchainRoot);
        var nativeEvidence = CollectNativeEvidence(input);
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["evidenceKind"] = "stark-compiler-private-backend-qualification",
            ["status"] = "qualified-native",
            ["targetId"] = input.Target.Id,
            ["runtimeIdentifier"] = input.Target.RuntimeIdentifier,
            ["targetTriple"] = input.Target.TargetTriple,
            ["llvmVersion"] = input.Manifest.RequiredString("llvmVersion", "private backend manifest"),
            ["manifestSha256"] = JsonIO.Sha256File(Path.Combine(input.ToolchainRoot, "manifest.json")),
            ["runtimeClosure"] = input.Manifest.RequiredObject("runtimeClosure", "private backend manifest").DeepClone(),
            ["sourceBuild"] = input.SourceBuildEvidence.DeepClone(),
            ["nativeEvidence"] = nativeEvidence,
        };
        JsonIO.Write(output, report);
        return report;
    }

    internal static PrivateBackendQualificationInput ValidateManifest(
        string repositoryRoot,
        string targetId,
        string toolchainRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        toolchainRoot = Path.GetFullPath(toolchainRoot);
        var configuration = ReleaseConfiguration.Validate(repositoryRoot);
        var target = configuration.Targets.SingleOrDefault(item => item.Id == targetId)
            ?? throw new ReleaseToolException($"Unknown release target '{targetId}'.");
        Validation.Require(target.Id == "macos-x64", "Native source-build qualification currently supports only the reviewed macos-x64 backend.");
        Validation.Require(target.OperatingSystem == "macos" && target.Architecture == "x64", "macos-x64 target facts are inconsistent.");
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
        Validation.Require(selection.RequiredString("acquisition", $"private backend/{targetId}") == "pinned-source-build", $"{targetId} is not a pinned source build.");
        Validation.Require(
            selection.RequiredString("qualificationStatus", $"private backend/{targetId}") is "unqualified-build" or "qualified-build",
            $"{targetId} has an invalid source-build qualification state.");

        var acquisition = JsonIO.LoadObject(
            Path.Combine(repositoryRoot, dependency.RequiredString("acquisitionManifest", "compiler-private-backend")),
            "LLVM acquisition manifest");
        var platform = acquisition.RequiredObject("platforms", "LLVM acquisition manifest")
            .RequiredObject(targetId, "LLVM acquisition platforms");
        var sourceBuildRecipe = platform.RequiredObject("sourceBuild", $"LLVM/{targetId}");
        var manifestPath = Path.Combine(toolchainRoot, "manifest.json");
        var manifest = JsonIO.LoadObject(manifestPath, "private backend manifest");
        RequireExactProperties(manifest, ManifestProperties, "private backend manifest");

        Validation.Require(manifest.RequiredInt("schemaVersion", "private backend manifest") == 2, "Private backend manifest schemaVersion must be 2.");
        Validation.Require(manifest.RequiredString("payloadKind", "private backend manifest") == "stark-compiler-private-backend", "Private backend manifest payloadKind is invalid.");
        RequireEqual(manifest["llvmVersion"], acquisition["llvmVersion"], "LLVM version");
        RequireEqual(manifest["releaseTag"], acquisition["releaseTag"], "LLVM release tag");
        RequireEqual(manifest["releaseUrl"], acquisition["releaseUrl"], "LLVM release URL");
        Validation.Require(manifest.RequiredString("assetSuffix", "private backend manifest") == target.AssetSuffix, "Private backend asset suffix differs from the target.");
        Validation.Require(manifest.RequiredString("runtimeIdentifier", "private backend manifest") == target.RuntimeIdentifier, "Private backend RID differs from the target.");
        Validation.Require(manifest.RequiredString("acquisitionKind", "private backend manifest") == "pinned-source-build", "Private backend acquisition kind is not pinned-source-build.");
        Validation.Require(manifest["binaryArchive"] is null, "Pinned source build must not declare a binary archive.");
        RequireEqual(manifest["sourceArchive"], acquisition["sourceArchive"], "LLVM source archive");

        var requiredTools = ValidateStringArray(manifest, "requiredTools", nonEmpty: true);
        RequireEqualStringSet(requiredTools, Validation.Strings(platform["requiredTools"], $"LLVM/{targetId} requiredTools", nonEmpty: true), "required tools");
        foreach (var tool in requiredTools)
        {
            PortablePaths.Validate(tool, "private backend required tool");
            Validation.Require(tool.StartsWith("bin/", StringComparison.Ordinal) && AllowedToolNames.Contains(Path.GetFileName(tool)), $"Private backend tool '{tool}' is outside the Stage0 allowlist.");
        }

        var expectedResourceRoots = platform["compilerResourceRoots"] is null
            ? new[] { "lib/clang" }
            : Validation.Strings(platform["compilerResourceRoots"], $"LLVM/{targetId} compilerResourceRoots", nonEmpty: true);
        var resourceRoots = ValidateStringArray(manifest, "compilerResourceRoots", nonEmpty: true);
        RequireEqualStringSet(resourceRoots, expectedResourceRoots, "compiler resource roots");
        var requiredPatternMatches = ValidateStringArray(manifest, "requiredPatternMatches");
        Validation.Require(
            platform.RequiredArray("requiredPatterns", $"LLVM/{targetId}").Count == 0 && requiredPatternMatches.Length == 0,
            "macos-x64 source build must not acquire broad runtime-library patterns.");
        Validation.Require(ValidateStringArray(manifest, "excludedDevelopmentPatterns").Length == 0, "macos-x64 source build unexpectedly excluded configured runtime patterns.");

        ValidateHardlinkDeclaration(manifest.RequiredArray("hardlinkAliases", "private backend manifest"), platform["hardlinkAliases"] as JsonArray);
        var licenseFiles = ValidateStringArray(manifest, "licenseFiles", nonEmpty: true);
        foreach (var license in licenseFiles)
        {
            PortablePaths.Validate(license, "private backend license");
        }

        var sourceBuildEvidence = manifest.RequiredObject("sourceBuild", "private backend manifest");
        ValidateSourceBuildEvidence(sourceBuildEvidence, sourceBuildRecipe);
        ValidateRuntimeClosure(
            toolchainRoot,
            manifest.RequiredObject("runtimeClosure", "private backend manifest"),
            requiredTools,
            requiredPatternMatches,
            resourceRoots,
            licenseFiles,
            acquisition.RequiredObject("sourceArchive", "LLVM acquisition manifest"));

        return new PrivateBackendQualificationInput(
            repositoryRoot,
            toolchainRoot,
            target,
            manifest,
            requiredTools,
            sourceBuildRecipe,
            sourceBuildEvidence);
    }

    private static void ValidateSourceBuildEvidence(JsonObject evidence, JsonObject recipe)
    {
        RequireExactProperties(evidence, SourceBuildEvidenceProperties, "source-build evidence");
        Validation.Require(evidence.RequiredInt("schemaVersion", "source-build evidence") == 1, "Source-build evidence schemaVersion must be 1.");
        Validation.Require(evidence.RequiredString("recipeKind", "source-build evidence") == "pinned-source-build", "Source-build evidence recipe kind is invalid.");
        foreach (var field in new[]
        {
            "hostOperatingSystem", "hostArchitecture", "minimumDeploymentTarget", "configuration",
            "optimization", "lto", "generator", "sourceSubdirectory", "buildTarget",
        })
        {
            RequireEqual(evidence[field], recipe[field], $"source-build {field}");
        }
        foreach (var field in new[] { "projects", "targetsToBuild", "cmakeOptions", "sourceDateEpoch", "parallelLinkJobs" })
        {
            RequireEqual(evidence[field], recipe[field], $"source-build {field}");
        }

        var compileJobs = evidence.RequiredInt("compileJobs", "source-build evidence");
        var maxCompileJobs = recipe.RequiredInt("maxParallelCompileJobs", "source-build recipe");
        Validation.Require(compileJobs is > 0 && compileJobs <= maxCompileJobs, $"Source-build compileJobs '{compileJobs}' exceeds the reviewed bound '{maxCompileJobs}'.");

        var buildTools = evidence.RequiredObject("buildTools", "source-build evidence");
        RequireExactProperties(buildTools, ["cmake", "ninja"], "source-build buildTools");
        foreach (var (name, recipeField) in new[] { ("cmake", "cmakeVersion"), ("ninja", "ninjaVersion") })
        {
            var tool = buildTools.RequiredObject(name, "source-build buildTools");
            RequireExactProperties(tool, ["version", "sha256"], $"source-build {name}");
            Validation.Require(tool.RequiredString("version", $"source-build {name}") == recipe.RequiredString(recipeField, "source-build recipe"), $"Source-build {name} version differs from the recipe.");
            RequireLowerSha256(tool.RequiredString("sha256", $"source-build {name}"), $"source-build {name}");
        }

        var apple = evidence.RequiredObject("appleToolchain", "source-build evidence");
        RequireExactProperties(apple, ["xcodeVersion", "sdkVersion", "clangVersion", "clangSha256", "clangxxSha256"], "source-build Apple toolchain");
        _ = apple.RequiredString("xcodeVersion", "source-build Apple toolchain");
        _ = apple.RequiredString("sdkVersion", "source-build Apple toolchain");
        _ = apple.RequiredString("clangVersion", "source-build Apple toolchain");
        RequireLowerSha256(apple.RequiredString("clangSha256", "source-build Apple toolchain"), "Apple Clang");
        RequireLowerSha256(apple.RequiredString("clangxxSha256", "source-build Apple toolchain"), "Apple Clang++");
    }

    private static void ValidateRuntimeClosure(
        string toolchainRoot,
        JsonObject closure,
        IReadOnlyCollection<string> requiredTools,
        IReadOnlyCollection<string> requiredPatternMatches,
        IReadOnlyCollection<string> resourceRoots,
        IReadOnlyCollection<string> licenseFiles,
        JsonObject sourceArchive)
    {
        RequireExactProperties(closure, ["fileCount", "logicalBytes", "files"], "private backend runtimeClosure");
        var entries = closure.RequiredArray("files", "private backend runtimeClosure").OfType<JsonObject>().ToArray();
        Validation.Require(entries.Length == closure.RequiredArray("files", "private backend runtimeClosure").Count, "Private backend runtime closure must contain only file objects.");
        Validation.Require(entries.Length == closure.RequiredInt("fileCount", "private backend runtimeClosure"), "Private backend runtime closure fileCount is inconsistent.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long logicalBytes = 0;
        foreach (var entry in entries)
        {
            RequireExactProperties(entry, ["path", "bytes", "sha256"], "private backend runtime closure entry");
            var relativePath = entry.RequiredString("path", "private backend runtime closure entry");
            PortablePaths.Validate(relativePath, "private backend runtime closure entry");
            Validation.Require(relativePath != "manifest.json", "Private backend runtime closure cannot recursively list manifest.json.");
            Validation.Require(paths.Add(relativePath) && portablePaths.Add(relativePath), $"Private backend runtime closure contains duplicate or case-colliding path '{relativePath}'.");
            ValidateClosureEntryClass(relativePath, requiredTools, requiredPatternMatches, resourceRoots);

            var expectedBytes = ReadNonNegativeInt64(entry["bytes"], $"runtime closure '{relativePath}' bytes");
            var expectedSha = entry.RequiredString("sha256", $"runtime closure '{relativePath}'");
            RequireLowerSha256(expectedSha, $"runtime closure '{relativePath}'");
            var path = PortablePaths.SafeDestination(toolchainRoot, relativePath);
            EnsureNoSymbolicLinkTraversal(toolchainRoot, path, relativePath);
            Validation.Require(File.Exists(path), $"Private backend runtime closure entry '{relativePath}' is missing.");
            var actualBytes = new FileInfo(path).Length;
            Validation.Require(actualBytes == expectedBytes, $"Private backend runtime closure entry '{relativePath}' has {actualBytes} bytes; expected {expectedBytes}.");
            Validation.Require(JsonIO.Sha256File(path) == expectedSha, $"Private backend runtime closure entry '{relativePath}' failed SHA-256 validation.");
            logicalBytes = checked(logicalBytes + actualBytes);
        }

        Validation.Require(logicalBytes == ReadNonNegativeInt64(closure["logicalBytes"], "runtime closure logicalBytes"), "Private backend runtime closure logicalBytes is inconsistent.");
        Validation.Require(paths.Contains(".stark-llvm-toolchain-owner.json"), "Private backend runtime closure omits its portable owner marker.");
        foreach (var path in requiredTools.Concat(requiredPatternMatches))
        {
            Validation.Require(paths.Contains(path), $"Private backend runtime closure omits declared runtime path '{path}'.");
        }
        foreach (var root in resourceRoots)
        {
            Validation.Require(paths.Any(path => path.StartsWith(root + "/", StringComparison.Ordinal)), $"Private backend compiler resource root '{root}' is empty.");
        }
        foreach (var license in licenseFiles)
        {
            Validation.Require(paths.Contains($"licenses/{license}"), $"Private backend runtime closure omits declared license '{license}'.");
        }

        var evidenceNames = new[]
        {
            sourceArchive.RequiredObject("signature", "LLVM source archive").RequiredString("name", "LLVM source signature"),
            sourceArchive.RequiredObject("attestation", "LLVM source archive").RequiredString("name", "LLVM source attestation"),
        };
        foreach (var name in evidenceNames)
        {
            PortablePaths.Validate(name, "LLVM provenance filename");
            Validation.Require(paths.Contains($"provenance/{name}"), $"Private backend runtime closure omits source provenance '{name}'.");
        }

        EnsureNoSymbolicLinksInTree(toolchainRoot);

        var actualFiles = Directory.EnumerateFiles(toolchainRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(toolchainRoot, path).Replace('\\', '/'))
            .Where(path => path != "manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        Validation.Require(actualFiles.SetEquals(paths), $"Private backend contains untracked files or omits tracked files: {DescribeSetDifference(paths, actualFiles)}.");
    }

    private static JsonObject CollectNativeEvidence(PrivateBackendQualificationInput input)
    {
        Validation.Require(OperatingSystem.IsMacOS(), "macos-x64 backend qualification must run on a native macOS host.");
        Validation.Require(RuntimeInformation.ProcessArchitecture == Architecture.X64, $"macos-x64 backend qualification requires a native x64 process; found '{RuntimeInformation.ProcessArchitecture}'.");

        var tools = new JsonArray();
        foreach (var relativePath in input.RequiredTools.Order(StringComparer.Ordinal))
        {
            var path = PortablePaths.SafeDestination(input.ToolchainRoot, relativePath);
            var versionArguments = GetVersionProbeArguments(relativePath);
            var version = RunProcess(path, versionArguments, input.RepositoryRoot, timeout: TimeSpan.FromMinutes(2));
            var architectures = RunProcess("/usr/bin/lipo", ["-archs", path], input.RepositoryRoot, timeout: TimeSpan.FromMinutes(2));
            Validation.Require(ParseArchitectures(architectures.StandardOutput).SetEquals(["x86_64"]), $"Private backend tool '{relativePath}' is not a thin x86_64 Mach-O binary: {architectures.StandardOutput.Trim()}");
            var dependencies = RunProcess("/usr/bin/otool", ["-L", path], input.RepositoryRoot, timeout: TimeSpan.FromMinutes(2));
            var dependencyPaths = ParseMachODependencies(dependencies.StandardOutput);
            Validation.Require(
                dependencyPaths.All(dependency => dependency.StartsWith("/usr/lib/", StringComparison.Ordinal) || dependency.StartsWith("/System/Library/", StringComparison.Ordinal)),
                $"Private backend tool '{relativePath}' depends outside the macOS system boundary: {string.Join(", ", dependencyPaths)}");
            tools.Add(new JsonObject
            {
                ["path"] = relativePath,
                ["sha256"] = JsonIO.Sha256File(path),
                ["architectures"] = new JsonArray("x86_64"),
                ["version"] = FirstNonEmptyLine(version.StandardOutput, version.StandardError),
                ["versionArguments"] = new JsonArray(versionArguments.Select(static value => (JsonNode)value).ToArray()),
                ["dependencies"] = new JsonArray(dependencyPaths.Select(static value => (JsonNode)value).ToArray()),
            });
        }

        ValidateHardlinkIdentity(input);
        var smoke = RunOptimizedDeterminismSmoke(input);
        return new JsonObject
        {
            ["host"] = new JsonObject
            {
                ["operatingSystem"] = "macos",
                ["processArchitecture"] = "x64",
            },
            ["tools"] = tools,
            ["optimizedDeterminismSmoke"] = smoke,
        };
    }

    internal static IReadOnlyList<string> GetVersionProbeArguments(string relativePath)
    {
        var fileName = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return fileName == "lld"
            ? ["-flavor", "darwin", "--version"]
            : ["--version"];
    }

    private static JsonObject RunOptimizedDeterminismSmoke(PrivateBackendQualificationInput input)
    {
        var clang = PortablePaths.SafeDestination(input.ToolchainRoot, "bin/clang");
        var llvmAr = PortablePaths.SafeDestination(input.ToolchainRoot, "bin/llvm-ar");
        var llvmRanlib = PortablePaths.SafeDestination(input.ToolchainRoot, "bin/llvm-ranlib");
        var workRoot = Directory.CreateTempSubdirectory("stark-private-backend-qualification-").FullName;
        const string llvmIr = """
            target triple = "x86_64-apple-macosx11.0.0"

            define i32 @stark_backend_qualification(i32 %value) {
            entry:
              %scaled = mul nsw i32 %value, 7
              %biased = add nsw i32 %scaled, 3
              ret i32 %biased
            }
            """;
        try
        {
            var outputs = new List<(string Object, string Archive)>();
            foreach (var name in new[] { "first", "second" })
            {
                var root = Directory.CreateDirectory(Path.Combine(workRoot, name)).FullName;
                var objectPath = Path.Combine(root, "smoke.o");
                var archivePath = Path.Combine(root, "libSmoke.a");
                RunProcess(
                    clang,
                    [
                        "-x", "ir", "-", "-c", "-o", objectPath,
                        "-target", input.Target.TargetTriple,
                        "-O3", "-flto=thin", "-ffunction-sections", "-fdata-sections",
                    ],
                    root,
                    standardInput: llvmIr,
                    timeout: TimeSpan.FromMinutes(5));
                RunProcess(llvmAr, ["rcD", archivePath, objectPath], root, timeout: TimeSpan.FromMinutes(2));
                RunProcess(llvmRanlib, ["-D", archivePath], root, timeout: TimeSpan.FromMinutes(2));
                var members = RunProcess(llvmAr, ["t", archivePath], root, timeout: TimeSpan.FromMinutes(2));
                Validation.Require(members.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SequenceEqual(["smoke.o"]), "Deterministic backend smoke archive has unexpected members.");
                outputs.Add((objectPath, archivePath));
            }

            var objectSha = JsonIO.Sha256File(outputs[0].Object);
            var archiveSha = JsonIO.Sha256File(outputs[0].Archive);
            Validation.Require(objectSha == JsonIO.Sha256File(outputs[1].Object), "Pinned backend produced different optimized ThinLTO object bytes across repeated builds.");
            Validation.Require(archiveSha == JsonIO.Sha256File(outputs[1].Archive), "Pinned backend produced different deterministic archive bytes across repeated builds.");
            return new JsonObject
            {
                ["targetTriple"] = input.Target.TargetTriple,
                ["optimization"] = "O3",
                ["lto"] = "Thin",
                ["functionSections"] = true,
                ["dataSections"] = true,
                ["archiveMode"] = "deterministic",
                ["repeatCount"] = 2,
                ["objectSha256"] = objectSha,
                ["archiveSha256"] = archiveSha,
            };
        }
        finally
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    private static void ValidateHardlinkIdentity(PrivateBackendQualificationInput input)
    {
        foreach (var alias in input.Manifest.RequiredArray("hardlinkAliases", "private backend manifest").OfType<JsonObject>())
        {
            var path = PortablePaths.SafeDestination(input.ToolchainRoot, alias.RequiredString("path", "hardlink alias"));
            var target = PortablePaths.SafeDestination(input.ToolchainRoot, alias.RequiredString("target", "hardlink alias"));
            var pathIdentity = RunProcess("/usr/bin/stat", ["-f", "%d:%i", path], input.RepositoryRoot, timeout: TimeSpan.FromMinutes(1)).StandardOutput.Trim();
            var targetIdentity = RunProcess("/usr/bin/stat", ["-f", "%d:%i", target], input.RepositoryRoot, timeout: TimeSpan.FromMinutes(1)).StandardOutput.Trim();
            Validation.Require(pathIdentity == targetIdentity, $"Private backend alias '{Path.GetRelativePath(input.ToolchainRoot, path)}' is byte-identical but not a hard link to its declared target.");
        }
    }

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput = null,
        TimeSpan? timeout = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new ReleaseToolException($"Could not start qualification command '{fileName}': {exception.Message}", exception);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        var wait = timeout ?? TimeSpan.FromMinutes(10);
        if (!process.WaitForExit((int)Math.Min(wait.TotalMilliseconds, int.MaxValue)))
        {
            process.Kill(entireProcessTree: true);
            throw new ReleaseToolException($"Qualification command '{fileName}' timed out after {wait}.");
        }

        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        var result = new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
        if (result.ExitCode != 0)
        {
            throw new ReleaseToolException($"Qualification command failed ({result.ExitCode}): {fileName} {string.Join(' ', arguments)}{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
        }
        return result;
    }

    private static void ValidateHardlinkDeclaration(JsonArray actual, JsonArray? expected)
    {
        expected ??= [];
        Validation.Require(actual.Count == actual.OfType<JsonObject>().Count(), "Private backend hardlinkAliases must contain only objects.");
        Validation.Require(expected.Count == expected.OfType<JsonObject>().Count(), "LLVM hardlinkAliases must contain only objects.");
        var actualPairs = actual.OfType<JsonObject>().Select(ReadAlias).Order(StringComparer.Ordinal).ToArray();
        var expectedPairs = expected.OfType<JsonObject>().Select(ReadAlias).Order(StringComparer.Ordinal).ToArray();
        Validation.Require(actualPairs.SequenceEqual(expectedPairs, StringComparer.Ordinal), "Private backend hard-link declarations differ from the pinned platform recipe.");
        static string ReadAlias(JsonObject alias)
        {
            RequireExactProperties(alias, ["path", "target"], "private backend hardlink alias");
            var path = alias.RequiredString("path", "hardlink alias");
            var target = alias.RequiredString("target", "hardlink alias");
            PortablePaths.Validate(path, "hardlink alias path");
            PortablePaths.Validate(target, "hardlink alias target");
            return $"{path}\0{target}";
        }
    }

    private static void ValidateClosureEntryClass(
        string path,
        IReadOnlyCollection<string> requiredTools,
        IReadOnlyCollection<string> requiredPatterns,
        IReadOnlyCollection<string> resourceRoots)
    {
        var allowed = path == ".stark-llvm-toolchain-owner.json"
            || path.StartsWith("licenses/", StringComparison.Ordinal)
            || path.StartsWith("provenance/", StringComparison.Ordinal)
            || requiredTools.Contains(path)
            || requiredPatterns.Contains(path)
            || resourceRoots.Any(root => path.StartsWith(root + "/", StringComparison.Ordinal));
        Validation.Require(allowed, $"Private backend runtime closure contains undeclared development file '{path}'.");
    }

    private static string[] ValidateStringArray(JsonObject owner, string name, bool nonEmpty = false)
    {
        var values = Validation.Strings(owner[name], $"private backend manifest.{name}", nonEmpty);
        foreach (var value in values)
        {
            PortablePaths.Validate(value, $"private backend manifest.{name}");
        }
        return values;
    }

    private static void RequireEqualStringSet(IEnumerable<string> actual, IEnumerable<string> expected, string label)
    {
        Validation.Require(actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected), $"Private backend {label} differ from the pinned platform recipe.");
    }

    private static void RequireEqual(JsonNode? actual, JsonNode? expected, string label)
    {
        Validation.Require(JsonNode.DeepEquals(actual, expected), $"Private backend {label} differs from the pinned acquisition recipe.");
    }

    private static void RequireExactProperties(JsonObject value, IEnumerable<string> expected, string label)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = value.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        Validation.Require(actualSet.SetEquals(expectedSet), $"{label} has unexpected or missing properties: {DescribeSetDifference(expectedSet, actualSet)}.");
    }

    private static void RequireLowerSha256(string value, string label)
    {
        Validation.Require(Validation.IsSha256(value) && value == value.ToLowerInvariant(), $"{label} SHA-256 must be 64 lowercase hexadecimal characters.");
    }

    private static long ReadNonNegativeInt64(JsonNode? node, string label)
    {
        if (node is not JsonValue value || !value.TryGetValue<long>(out var result) || result < 0)
        {
            throw new ReleaseToolException($"{label} must be a non-negative 64-bit integer.");
        }
        return result;
    }

    private static void EnsureNoSymbolicLinkTraversal(string root, string path, string relativePath)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            Validation.Require((info.Attributes & FileAttributes.ReparsePoint) == 0 && info.LinkTarget is null, $"Private backend runtime closure entry '{relativePath}' traverses a symbolic link.");
        }
    }

    private static void EnsureNoSymbolicLinksInTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entryPath);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(entryPath) : new FileInfo(entryPath);
                Validation.Require(
                    (attributes & FileAttributes.ReparsePoint) == 0 && info.LinkTarget is null,
                    $"Private backend contains symbolic link or reparse point '{Path.GetRelativePath(root, entryPath)}'.");
                if (isDirectory)
                {
                    pending.Push(entryPath);
                }
            }
        }
    }

    private static HashSet<string> ParseArchitectures(string output)
        => output.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static string[] ParseMachODependencies(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(line => line.Split(" (", 2, StringSplitOptions.None)[0].Trim())
            .Where(static line => line.Length != 0)
            .ToArray();

    private static string FirstNonEmptyLine(params string[] values)
        => values.SelectMany(value => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).FirstOrDefault()
            ?? throw new ReleaseToolException("Qualification tool did not report a version.");

    private static bool IsSameOrDescendant(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string DescribeSetDifference(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = actualSet.Except(expectedSet, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return $"missing [{string.Join(", ", missing)}], extra [{string.Join(", ", extra)}]";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
