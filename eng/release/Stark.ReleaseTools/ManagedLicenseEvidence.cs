using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class ManagedLicenseEvidence
{
    private sealed record Package(
        string DependencyId,
        string PackageId,
        string Version,
        string License,
        string PackageArchiveSha512,
        JsonArray Files);

    private sealed record Declaration(JsonObject Document, Dictionary<string, JsonObject> Targets, Dictionary<string, JsonObject> Dependencies);

    public static JsonObject Run(CommandLine command)
    {
        command.RejectUnknown("--root", "--target-id", "--assets", "--output-root");
        var root = Path.GetFullPath(command.Optional("--root", Directory.GetCurrentDirectory()));
        var inventory = PrepareInventory(root, command.Required("--target-id"), Path.GetFullPath(command.Required("--assets")), Path.GetFullPath(command.Required("--output-root")));
        var packages = inventory.RequiredArray("packages", "managed license inventory").OfType<JsonObject>().ToArray();
        return new JsonObject
        {
            ["status"] = "ready",
            ["targetId"] = inventory["targetId"]!.DeepClone(),
            ["runtimeIdentifier"] = inventory["runtimeIdentifier"]!.DeepClone(),
            ["packages"] = packages.Length,
            ["licenseFiles"] = packages.Sum(package => package.RequiredArray("licenseFiles", "managed package").Count),
        };
    }

    public static void ValidateDeclaration(string root)
    {
        _ = LoadDeclaration(root);
    }

    public static JsonObject PrepareInventory(string root, string targetId, string assetsPath, string outputRoot)
    {
        var (declaration, target, packages) = SelectedPackages(root, targetId);
        var assets = JsonIO.LoadObject(assetsPath, "project.assets.json");
        var project = assets.RequiredObject("project", "project.assets.json");
        var restore = project.RequiredObject("restore", "project.assets.json.project");
        var packagesRoot = restore.RequiredString("packagesPath", "project.assets.json restore");
        Validation.Require(Path.IsPathFullyQualified(packagesRoot) && Directory.Exists(packagesRoot), "project.assets.json NuGet packagesPath is not an existing absolute directory.");
        var runtimes = project.RequiredObject("runtimes", "project.assets.json.project");
        Validation.Require(runtimes.Count == 1 && runtimes.ContainsKey(target.RequiredString("runtimeIdentifier", targetId)), "Managed-license target does not match the exact restored RID.");

        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar);
        Validation.Require(new DirectoryInfo(destination).LinkTarget is null, "Managed-license output root must not be a symbolic link.");
        var artifacts = Path.Combine(rootPath, "artifacts") + Path.DirectorySeparatorChar;
        Validation.Require(destination != rootPath && (!destination.StartsWith(rootPath + Path.DirectorySeparatorChar, PathComparison) || destination.StartsWith(artifacts, PathComparison)), $"Managed-license output inside the repository must be under {artifacts}.");
        var manifestPath = Path.Combine(destination, "manifest.json");
        if (Directory.Exists(destination))
        {
            var existing = JsonIO.LoadObject(manifestPath, "existing managed-license inventory");
            Validation.Require(existing.RequiredString("manifestKind", "existing managed-license inventory") == "stark-managed-license-inventory" && existing.RequiredString("targetId", "existing managed-license inventory") == targetId, $"Refusing to replace unowned managed-license output: {destination}");
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        var packageInventory = new JsonArray();
        var seenOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.OrderBy(package => package.PackageId, StringComparer.Ordinal))
        {
            var archive = PackageArchive(packagesRoot, package);
            var files = new JsonArray();
            foreach (var evidence in package.Files.OfType<JsonObject>())
            {
                byte[] content;
                JsonObject source;
                var sourceKind = evidence.RequiredString("sourceKind", $"{package.PackageId} evidence");
                if (sourceKind == "archive-entry")
                {
                    var archivePath = evidence.RequiredString("archivePath", $"{package.PackageId} evidence");
                    content = ReadArchiveEvidence(archive, archivePath, $"{package.PackageId}/{package.Version}");
                    source = new JsonObject { ["kind"] = "archive-entry", ["path"] = archivePath };
                }
                else
                {
                    var sourcePath = evidence.RequiredString("sourcePath", $"{package.PackageId} evidence");
                    Validation.SafeRelativePath(sourcePath, "repository license evidence path");
                    content = File.ReadAllBytes(Path.Combine(root, sourcePath.Replace('/', Path.DirectorySeparatorChar)));
                    source = new JsonObject
                    {
                        ["kind"] = "repository-file",
                        ["path"] = sourcePath,
                        ["upstreamUrl"] = evidence["upstreamUrl"]!.DeepClone(),
                        ["upstreamTag"] = evidence["upstreamTag"]!.DeepClone(),
                        ["upstreamCommit"] = evidence["upstreamCommit"]!.DeepClone(),
                    };
                }

                ValidateEvidence(content, evidence, $"{package.PackageId}/{package.Version}/{evidence.RequiredString("outputName", "license evidence")}");
                var relative = $"{package.PackageId}/{package.Version}/{evidence.RequiredString("outputName", "license evidence")}";
                Validation.SafeRelativePath(relative, "managed-license output path");
                Validation.Require(seenOutputs.Add(relative), $"Managed-license output is duplicate or case-colliding: {relative}");
                var output = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllBytes(output, content);
                files.Add(new JsonObject
                {
                    ["path"] = relative,
                    ["bytes"] = content.Length,
                    ["sha256"] = JsonIO.Sha256(content),
                    ["source"] = source,
                });
            }

            packageInventory.Add(new JsonObject
            {
                ["dependencyId"] = package.DependencyId,
                ["packageId"] = package.PackageId,
                ["version"] = package.Version,
                ["license"] = package.License,
                ["packageArchiveSha512"] = package.PackageArchiveSha512,
                ["signatureArtifactsPresent"] = true,
                ["licenseFiles"] = files,
            });
        }

        var declarationPath = Path.Combine(root, "eng", "release", "managed-license-evidence.json");
        var declarationInfo = new FileInfo(declarationPath);
        var inventory = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["manifestKind"] = "stark-managed-license-inventory",
            ["targetId"] = targetId,
            ["runtimeIdentifier"] = target.RequiredString("runtimeIdentifier", targetId),
            ["declaration"] = new JsonObject
            {
                ["path"] = "eng/release/managed-license-evidence.json",
                ["bytes"] = declarationInfo.Length,
                ["sha256"] = JsonIO.Sha256File(declarationPath),
            },
            ["packages"] = packageInventory,
        };
        JsonIO.Write(manifestPath, inventory);
        _ = ValidateStagedInventory(destination, targetId, declarationPath);
        return inventory;
    }

    public static JsonObject ValidateStagedInventory(string managedRoot, string targetId, string declarationPath)
    {
        var destination = Path.GetFullPath(managedRoot);
        Validation.Require(new DirectoryInfo(destination).LinkTarget is null, "Staged managed-license root must not be a symbolic link.");
        var releaseDirectory = Path.GetDirectoryName(Path.GetFullPath(declarationPath))!;
        var root = Directory.GetParent(Directory.GetParent(releaseDirectory)!.FullName)!.FullName;
        var (_, target, packages) = SelectedPackages(root, targetId);
        var manifestPath = Path.Combine(destination, "manifest.json");
        Validation.Require(new FileInfo(manifestPath).LinkTarget is null, "Staged managed-license manifest must be a regular file.");
        var inventory = JsonIO.LoadObject(manifestPath, "staged managed-license inventory");
        Validation.Require(inventory.RequiredInt("schemaVersion", "managed-license inventory") == 1 && inventory.RequiredString("manifestKind", "managed-license inventory") == "stark-managed-license-inventory", "Staged managed-license manifest identity is invalid.");
        Validation.Require(inventory.RequiredString("targetId", "managed-license inventory") == targetId && inventory.RequiredString("runtimeIdentifier", "managed-license inventory") == target.RequiredString("runtimeIdentifier", targetId), "Staged managed-license target identity is invalid.");
        var declaration = inventory.RequiredObject("declaration", "managed-license inventory");
        var declarationInfo = new FileInfo(declarationPath);
        Validation.Require(declaration.RequiredString("path", "managed-license declaration") == "eng/release/managed-license-evidence.json" && declaration["bytes"]!.GetValue<long>() == declarationInfo.Length && declaration.RequiredString("sha256", "managed-license declaration") == JsonIO.Sha256File(declarationPath), "Staged managed-license declaration identity differs.");

        var actualPackages = inventory.RequiredArray("packages", "managed-license inventory").OfType<JsonObject>().ToArray();
        Validation.Require(actualPackages.Length == 3, "Staged managed-license inventory must contain exactly three packages.");
        var byId = actualPackages.ToDictionary(package => package.RequiredString("packageId", "managed package"), StringComparer.Ordinal);
        Validation.Require(byId.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(packages.Select(package => package.PackageId)), "Staged managed-license package set is not exact.");
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expected in packages)
        {
            var actual = byId[expected.PackageId];
            Validation.Require(actual.RequiredString("dependencyId", expected.PackageId) == expected.DependencyId && actual.RequiredString("version", expected.PackageId) == expected.Version && actual.RequiredString("license", expected.PackageId) == expected.License && actual.RequiredString("packageArchiveSha512", expected.PackageId) == expected.PackageArchiveSha512 && actual.RequiredBool("signatureArtifactsPresent", expected.PackageId), $"Staged managed-license package facts differ for {expected.PackageId}.");
            var expectedByPath = expected.Files.OfType<JsonObject>().ToDictionary(evidence => $"{expected.PackageId}/{expected.Version}/{evidence.RequiredString("outputName", "license evidence")}", StringComparer.Ordinal);
            var actualFiles = actual.RequiredArray("licenseFiles", expected.PackageId).OfType<JsonObject>().ToArray();
            Validation.Require(actualFiles.Length == expectedByPath.Count, $"Staged managed-license file count differs for {expected.PackageId}.");
            foreach (var descriptor in actualFiles)
            {
                var relative = descriptor.RequiredString("path", "staged managed-license file");
                Validation.SafeRelativePath(relative, "staged managed-license file path");
                Validation.Require(expectedByPath.TryGetValue(relative, out var expectedDescriptor), $"Staged managed-license file is undeclared: {relative}");
                var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(path);
                Validation.Require(info.Exists && info.LinkTarget is null && info.Length == descriptor["bytes"]!.GetValue<long>() && JsonIO.Sha256File(path) == descriptor.RequiredString("sha256", relative), $"Staged managed-license file differs: {relative}");
                Validation.Require(descriptor["bytes"]!.GetValue<long>() == expectedDescriptor!["bytes"]!.GetValue<long>() && descriptor.RequiredString("sha256", relative) == expectedDescriptor.RequiredString("sha256", relative), $"Staged managed-license file differs from its declaration: {relative}");
                expectedFiles.Add(relative);
            }
        }

        var actualTree = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(destination, path).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal);
        var expectedTree = expectedFiles.Append("manifest.json").ToHashSet(StringComparer.Ordinal);
        Validation.Require(actualTree.SetEquals(expectedTree), "Staged managed-license tree contains missing or undeclared files.");
        return new JsonObject { ["packages"] = actualPackages.Length, ["licenseFiles"] = expectedFiles.Count };
    }

    private static Declaration LoadDeclaration(string root)
    {
        var declaration = JsonIO.LoadObject(Path.Combine(root, "eng", "release", "managed-license-evidence.json"), "managed-license-evidence.json");
        var dependenciesDocument = JsonIO.LoadObject(Path.Combine(root, "eng", "release", "dependencies.json"), "dependencies.json");
        var targetsDocument = JsonIO.LoadObject(Path.Combine(root, "eng", "release", "targets.json"), "targets.json");
        Validation.Require(declaration.RequiredInt("schemaVersion", "managed-license-evidence.json") == 1 && declaration.RequiredString("manifestKind", "managed-license-evidence.json") == "stark-managed-license-evidence" && declaration.RequiredString("outputRoot", "managed-license-evidence.json") == "licenses/managed", "Managed-license declaration identity is invalid.");
        var targets = targetsDocument.RequiredArray("targets", "targets.json").OfType<JsonObject>().ToDictionary(target => target.RequiredString("id", "target"), StringComparer.Ordinal);
        Validation.Require(targets.Count == 6, "Managed-license validation requires the six-target matrix.");
        var dependencies = dependenciesDocument.RequiredArray("dependencies", "dependencies.json").OfType<JsonObject>().Where(dependency => dependency["id"]?.GetValue<string>() is "dotnet-stage0-runtime" or "antlr4-runtime-standard").ToDictionary(dependency => dependency.RequiredString("id", "dependency"), StringComparer.Ordinal);
        Validation.Require(dependencies.Count == 2, "Managed-license declaration requires exact .NET and ANTLR dependencies.");
        var dotnet = dependencies["dotnet-stage0-runtime"];
        var antlr = dependencies["antlr4-runtime-standard"];
        var families = declaration.RequiredArray("packageFamilies", "managed-license-evidence.json").OfType<JsonObject>().ToArray();
        Validation.Require(families.Length == 3, "Managed-license manifest must declare ANTLR and both .NET runtime package families.");
        var familyKeys = new HashSet<string>(StringComparer.Ordinal);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families)
        {
            var dependencyId = family.RequiredString("dependencyId", "managed-license family");
            Validation.Require(dependencies.ContainsKey(dependencyId), $"Managed-license family has unknown dependency {dependencyId}.");
            var expectedVersion = dependencyId == "antlr4-runtime-standard" ? antlr.RequiredString("version", dependencyId) : dotnet.RequiredString("runtimeVersion", dependencyId);
            Validation.Require(family.RequiredString("version", "managed-license family") == expectedVersion, $"Managed-license family {dependencyId} version differs.");
            var key = family["packageId"]?.GetValue<string>() ?? family.RequiredString("packageIdTemplate", "managed-license family");
            Validation.Require(new[] { "Antlr4.Runtime.Standard", "Microsoft.NETCore.App.Runtime.{rid}", "Microsoft.AspNetCore.App.Runtime.{rid}" }.Contains(key) && familyKeys.Add(key), $"Managed-license family identity is invalid or duplicate: {key}");
            if (key == "Antlr4.Runtime.Standard")
            {
                Validation.Require(family.RequiredString("targets", key) == "all" && family.RequiredString("packageArchiveSha512", key) == antlr.RequiredString("signedPackageSha512", dependencyId), "ANTLR license package identity differs from dependencies.json.");
                var files = family.RequiredArray("files", key).OfType<JsonObject>().ToArray();
                Validation.Require(files.Length == 1 && files[0].RequiredString("sourceKind", key) == "repository-file", "ANTLR must declare one repository-owned license file.");
                var evidence = files[0];
                var source = evidence.RequiredString("sourcePath", key);
                Validation.SafeRelativePath(source, "ANTLR license source");
                var content = File.ReadAllBytes(Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)));
                ValidateEvidence(content, evidence, "ANTLR repository license");
                outputs.Add($"{key}/{expectedVersion}/{evidence.RequiredString("outputName", key)}");
                continue;
            }

            var selections = family.RequiredArray("selections", key).OfType<JsonObject>().ToArray();
            Validation.Require(selections.Length == 6 && selections.Select(selection => selection.RequiredString("target", key)).ToHashSet(StringComparer.Ordinal).SetEquals(targets.Keys), $"{key} target selections are incomplete.");
            var dotnetSelections = dotnet.RequiredArray("selections", "dotnet-stage0-runtime").OfType<JsonObject>().ToDictionary(selection => selection.RequiredString("target", "dotnet selection"), StringComparer.Ordinal);
            var hashName = key.StartsWith("Microsoft.NETCore", StringComparison.Ordinal) ? "runtimePackContentHash" : "aspNetCoreRuntimePackContentHash";
            foreach (var selection in selections)
            {
                var targetId = selection.RequiredString("target", key);
                var rid = targets[targetId].RequiredString("runtimeIdentifier", targetId);
                Validation.Require(selection.RequiredString("runtimeIdentifier", key) == rid && selection.RequiredString("packageArchiveSha512", key) == dotnetSelections[targetId].RequiredString(hashName, $"dotnet/{targetId}"), $"{key}/{targetId} package identity differs.");
                var files = selection.RequiredArray("files", $"{key}/{targetId}").OfType<JsonObject>().ToArray();
                Validation.Require(files.Length == 2, $"{key}/{targetId} must declare a license and notice.");
                foreach (var evidence in files)
                {
                    ValidateEvidenceDescriptor(evidence, $"{key}/{targetId}");
                    var outputName = evidence.RequiredString("outputName", $"{key}/{targetId}");
                    Validation.SafeRelativePath(outputName, "managed-license output name");
                    Validation.Require(outputs.Add($"{key.Replace("{rid}", rid, StringComparison.Ordinal)}/{expectedVersion}/{outputName}"), "Managed-license output path is duplicate or case-colliding.");
                }
            }
        }

        Validation.Require(familyKeys.SetEquals(["Antlr4.Runtime.Standard", "Microsoft.NETCore.App.Runtime.{rid}", "Microsoft.AspNetCore.App.Runtime.{rid}"]), "Managed-license family set is not exact.");
        return new Declaration(declaration, targets, dependencies);
    }

    private static (Declaration Declaration, JsonObject Target, List<Package> Packages) SelectedPackages(string root, string targetId)
    {
        var declaration = LoadDeclaration(root);
        Validation.Require(declaration.Targets.TryGetValue(targetId, out var target), $"Unknown managed-license target '{targetId}'.");
        var packages = new List<Package>();
        foreach (var family in declaration.Document.RequiredArray("packageFamilies", "managed-license declaration").OfType<JsonObject>())
        {
            var dependencyId = family.RequiredString("dependencyId", "managed-license family");
            var dependency = declaration.Dependencies[dependencyId];
            if (family["targets"]?.GetValue<string>() == "all")
            {
                packages.Add(new Package(dependencyId, family.RequiredString("packageId", "managed-license family"), family.RequiredString("version", "managed-license family"), dependency.RequiredString("license", dependencyId), family.RequiredString("packageArchiveSha512", "managed-license family"), family.RequiredArray("files", "managed-license family")));
            }
            else
            {
                var selection = family.RequiredArray("selections", "managed-license family").OfType<JsonObject>().Single(item => item.RequiredString("target", "managed-license selection") == targetId);
                var packageId = family.RequiredString("packageIdTemplate", "managed-license family").Replace("{rid}", target!.RequiredString("runtimeIdentifier", targetId), StringComparison.Ordinal);
                packages.Add(new Package(dependencyId, packageId, family.RequiredString("version", "managed-license family"), dependency.RequiredString("license", dependencyId), selection.RequiredString("packageArchiveSha512", "managed-license selection"), selection.RequiredArray("files", "managed-license selection")));
            }
        }

        return (declaration, target!, packages);
    }

    private static string PackageArchive(string packagesRoot, Package package)
    {
        var name = package.PackageId.ToLowerInvariant();
        var packageRoot = Path.Combine(packagesRoot, name, package.Version);
        var archive = Path.Combine(packageRoot, $"{name}.{package.Version}.nupkg");
        var hashPath = archive + ".sha512";
        var signature = Path.Combine(packageRoot, ".signature.p7s");
        Validation.Require(File.Exists(archive) && File.Exists(hashPath) && new FileInfo(signature).Length > 0, $"Managed-license package cache is incomplete: {package.PackageId}/{package.Version}");
        Validation.Require(File.ReadAllText(hashPath, Encoding.ASCII).Trim() == package.PackageArchiveSha512 && Sha512Base64File(archive) == package.PackageArchiveSha512, $"Managed-license package archive differs: {package.PackageId}/{package.Version}");
        return archive;
    }

    private static byte[] ReadArchiveEvidence(string archivePath, string evidencePath, string context)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Validation.Require(names.Length == names.Distinct(StringComparer.Ordinal).Count() && names.Length == names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), $"{context} package contains duplicate or case-colliding ZIP paths.");
        Validation.Require(names.Contains(".signature.p7s", StringComparer.Ordinal), $"{context} package archive has no NuGet signature.");
        var entry = archive.Entries.SingleOrDefault(entry => entry.FullName == evidencePath);
        Validation.Require(entry is not null, $"{context} declared archive evidence is missing: {evidencePath}");
        using var input = entry!.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void ValidateEvidence(byte[] content, JsonObject evidence, string context)
    {
        ValidateEvidenceDescriptor(evidence, context);
        Validation.Require(content.LongLength == evidence["bytes"]!.GetValue<long>() && JsonIO.Sha256(content) == evidence.RequiredString("sha256", context), $"{context} content differs from its declaration.");
    }

    private static void ValidateEvidenceDescriptor(JsonObject evidence, string context)
    {
        Validation.Require(evidence["bytes"] is JsonValue bytes && bytes.TryGetValue<long>(out var count) && count > 0, $"{context} byte count is invalid.");
        var sha = evidence.RequiredString("sha256", context);
        Validation.Require(sha == sha.ToLowerInvariant() && Validation.IsSha256(sha), $"{context} SHA-256 is invalid.");
    }

    private static string Sha512Base64File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
