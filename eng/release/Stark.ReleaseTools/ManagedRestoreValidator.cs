using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class ManagedRestoreValidator
{
    public static JsonNode? Run(CommandLine command)
    {
        command.RejectUnknown("--root", "--rid", "--assets", "--dotnet-version", "--restore-only", "--candidate-sdk-root", "--output");
        var restoreOnly = command.HasFlag("--restore-only");
        var candidate = command.OptionalNullable("--candidate-sdk-root");
        Validation.Require(restoreOnly != (candidate is not null), "Exactly one of --restore-only or --candidate-sdk-root is required.");
        var report = Validate(
            Path.GetFullPath(command.Optional("--root", Directory.GetCurrentDirectory())),
            command.Required("--rid"),
            Path.GetFullPath(command.Required("--assets")),
            command.Required("--dotnet-version").Trim(),
            candidate is null ? null : Path.GetFullPath(candidate),
            restoreOnly);
        var output = command.OptionalNullable("--output");
        if (output is not null)
        {
            JsonIO.Write(output, report);
            return null;
        }

        return report;
    }

    public static JsonObject Validate(string root, string rid, string assetsPath, string dotnetVersion, string? candidateSdkRoot, bool restoreOnly)
    {
        var dependencies = JsonIO.LoadObject(Path.Combine(root, "eng", "release", "dependencies.json"), "dependencies.json").RequiredArray("dependencies", "dependencies.json").OfType<JsonObject>().ToArray();
        var targets = JsonIO.LoadObject(Path.Combine(root, "eng", "release", "targets.json"), "targets.json").RequiredArray("targets", "targets.json").OfType<JsonObject>().ToArray();
        var dotnet = dependencies.Single(item => item.RequiredString("id", "dependency") == "dotnet-stage0-runtime");
        var antlr = dependencies.Single(item => item.RequiredString("id", "dependency") == "antlr4-runtime-standard");
        var target = targets.SingleOrDefault(item => item.RequiredString("runtimeIdentifier", "target") == rid);
        Validation.Require(target is not null, $"Unknown release RID '{rid}'.");
        var targetId = target!.RequiredString("id", "target");
        var dotnetSelection = dotnet.RequiredArray("selections", "dotnet-stage0-runtime").OfType<JsonObject>().Single(item => item.RequiredString("target", "dotnet selection") == targetId);
        var antlrSelection = antlr.RequiredArray("selections", "antlr4-runtime-standard").OfType<JsonObject>().Single(item => item.RequiredString("target", "ANTLR selection") == targetId);
        Validation.Require(dotnetVersion == dotnet.RequiredString("version", "dotnet-stage0-runtime"), $"dotnet --version is {dotnetVersion}, expected {dotnet["version"]}.");
        Validation.Require(dotnetSelection.RequiredString("runtimeIdentifier", "dotnet selection") == rid, "Dependency manifest RID mapping is inconsistent.");
        var nugetConfig = Path.GetFullPath(Path.Combine(root, antlr.RequiredString("nugetConfig", "ANTLR dependency")));
        var lockFile = Path.GetFullPath(Path.Combine(root, antlrSelection.RequiredString("lockFile", "ANTLR selection")));
        Validation.Require(File.Exists(nugetConfig) && File.Exists(lockFile), "Release NuGet configuration or lock file is missing.");
        var assets = JsonIO.LoadObject(assetsPath, "project.assets.json");
        var report = ValidateAssets(
            assets, rid, root, nugetConfig, lockFile,
            dotnet.RequiredString("version", "dotnet dependency"),
            dotnet.RequiredString("runtimeVersion", "dotnet dependency"),
            antlr.RequiredString("version", "ANTLR dependency"),
            antlr.RequiredString("lockContentHash", "ANTLR dependency"),
            antlr.RequiredString("signedPackageSha512", "ANTLR dependency"),
            dotnetSelection.RequiredString("runtimePackContentHash", "dotnet selection"),
            dotnetSelection.RequiredString("aspNetCoreRuntimePackContentHash", "dotnet selection"));
        report["targetId"] = targetId;
        if (restoreOnly)
        {
            Validation.Require(candidateSdkRoot is null, "Restore-only validation must not claim a staged candidate.");
            report["validationScope"] = "managed-restore-only";
        }
        else
        {
            Validation.Require(candidateSdkRoot is not null, "Candidate-scoped validation requires a staged SDK root.");
            report["validationScope"] = "release-candidate";
            report["validatedCandidate"] = CandidateIdentity.Inspect(candidateSdkRoot!, targetId, rid);
        }

        return report;
    }

    private static JsonObject ValidateAssets(JsonObject assets, string rid, string root, string nugetConfig, string lockFile, string sdkVersion, string runtimeVersion, string antlrVersion, string antlrContentHash, string antlrArchiveHash, string runtimeHash, string aspnetHash)
    {
        Validation.Require(sdkVersion == "10.0.302" && runtimeVersion == "10.0.10", "Release SDK/runtime versions are not the reviewed .NET 10 versions.");
        Validation.Require(assets.RequiredInt("version", "project.assets.json") == 4, "project.assets.json schema version must be 4.");
        var targetKeys = assets.RequiredObject("targets", "project.assets.json").Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        Validation.Require(targetKeys.SetEquals(["net10.0", $"net10.0/{rid}"]), "Restored target graph is not the exact net10.0/RID pair.");
        var libraryKey = $"Antlr4.Runtime.Standard/{antlrVersion}";
        var libraries = assets.RequiredObject("libraries", "project.assets.json");
        Validation.Require(libraries.Count == 1 && libraries[libraryKey] is JsonObject, "Restored managed package closure is not exactly ANTLR.");
        var antlr = (JsonObject)libraries[libraryKey]!;
        Validation.Require(antlr.RequiredString("type", "ANTLR library") == "package" && antlr.RequiredString("sha512", "ANTLR library") == antlrContentHash, "ANTLR restore entry does not match the reviewed content hash.");
        var project = assets.RequiredObject("project", "project.assets.json");
        var restore = project.RequiredObject("restore", "project.assets.json project");
        RequireCanonical(restore.RequiredString("projectPath", "restore"), Path.Combine(root, "src", "compiler.csproj"), "restored project");
        var configs = Validation.Strings(restore["configFilePaths"], "restore configFilePaths");
        Validation.Require(configs.Length == 1, "Restore must use exactly one NuGet configuration.");
        RequireCanonical(configs[0], nugetConfig, "restore NuGet configuration");
        Validation.Require(restore.RequiredObject("sources", "restore").Count == 1 && restore.RequiredObject("sources", "restore").ContainsKey("https://api.nuget.org/v3/index.json"), "Restore used an ambient or unreviewed NuGet source.");
        Validation.Require(Validation.Strings(restore["originalTargetFrameworks"], "restore target frameworks").SequenceEqual(["net10.0"]), "Restore target framework is not exactly net10.0.");
        var lockProperties = restore.RequiredObject("restoreLockProperties", "restore");
        Validation.Require(lockProperties.RequiredString("restorePackagesWithLockFile", "restore lock") == "true", "Restore did not enable its lock file.");
        RequireCanonical(lockProperties.RequiredString("nuGetLockFilePath", "restore lock"), lockFile, "restore lock file");
        var framework = project.RequiredObject("frameworks", "project.assets.json project").RequiredObject("net10.0", "project framework");
        var requestedAntlr = framework.RequiredObject("dependencies", "project framework").RequiredObject("Antlr4.Runtime.Standard", "project framework dependencies");
        Validation.Require(requestedAntlr.RequiredString("target", "ANTLR request") == "Package" && requestedAntlr.RequiredString("version", "ANTLR request") == $"[{antlrVersion}, {antlrVersion}]", "Restore project does not request the exact ANTLR package range.");
        var downloads = framework.RequiredArray("downloadDependencies", "project framework").OfType<JsonObject>().ToDictionary(item => item.RequiredString("name", "download dependency"), item => item.RequiredString("version", "download dependency"), StringComparer.Ordinal);
        Validation.Require(downloads.Count == 2 && downloads[$"Microsoft.AspNetCore.App.Runtime.{rid}"] == $"[{runtimeVersion}, {runtimeVersion}]" && downloads[$"Microsoft.NETCore.App.Runtime.{rid}"] == $"[{runtimeVersion}, {runtimeVersion}]", "Self-contained runtime-pack graph is not the exact reviewed pair.");
        var runtimes = project.RequiredObject("runtimes", "project.assets.json project");
        Validation.Require(runtimes.Count == 1 && runtimes.ContainsKey(rid), "Restore runtime graph is not the selected RID.");
        var packagesPath = restore.RequiredString("packagesPath", "restore");
        Validation.Require(Path.IsPathFullyQualified(packagesPath) && Directory.Exists(packagesPath), "Restore package cache path is missing or not absolute.");
        var packages = new JsonArray
        {
            PackageCacheEntry(packagesPath, "Antlr4.Runtime.Standard", antlrVersion, antlrArchiveHash),
            PackageCacheEntry(packagesPath, $"Microsoft.NETCore.App.Runtime.{rid}", runtimeVersion, runtimeHash),
            PackageCacheEntry(packagesPath, $"Microsoft.AspNetCore.App.Runtime.{rid}", runtimeVersion, aspnetHash),
        };
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["status"] = "ready",
            ["sdkVersion"] = sdkVersion,
            ["runtimeVersion"] = runtimeVersion,
            ["runtimeIdentifier"] = rid,
            ["targetFramework"] = "net10.0",
            ["nugetConfig"] = Path.GetRelativePath(root, nugetConfig).Replace(Path.DirectorySeparatorChar, '/'),
            ["lockFile"] = Path.GetRelativePath(root, lockFile).Replace(Path.DirectorySeparatorChar, '/'),
            ["packages"] = packages,
        };
    }

    private static JsonObject PackageCacheEntry(string packagesPath, string packageId, string version, string expectedHash)
    {
        var name = packageId.ToLowerInvariant();
        var packageRoot = Path.Combine(packagesPath, name, version);
        var archive = Path.Combine(packageRoot, $"{name}.{version}.nupkg");
        var hashFile = archive + ".sha512";
        var signature = Path.Combine(packageRoot, ".signature.p7s");
        Validation.Require(File.Exists(archive) && File.Exists(hashFile) && new FileInfo(signature).Length > 0, $"Restored package cache is incomplete: {packageId}/{version}");
        using var stream = File.OpenRead(archive);
        var actual = Convert.ToBase64String(SHA512.HashData(stream));
        Validation.Require(File.ReadAllText(hashFile, System.Text.Encoding.ASCII).Trim() == expectedHash && actual == expectedHash, $"Restored {packageId}/{version} content does not match its SHA-512.");
        return new JsonObject { ["id"] = packageId, ["version"] = version, ["contentHash"] = actual, ["bytes"] = new FileInfo(archive).Length, ["signed"] = true };
    }

    private static void RequireCanonical(string actual, string expected, string label)
        => Validation.Require(Path.GetFullPath(actual) == Path.GetFullPath(expected), $"{label} is {Path.GetFullPath(actual)}, expected {Path.GetFullPath(expected)}.");
}
