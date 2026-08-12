namespace compiler.IntegrationTests;

public sealed class ReleaseSdkManifestAssemblyScriptTests
{
    [Fact]
    public void VendorAssemblyConsumesTheStrictUnifiedSchemaTwoContract()
    {
        var script = ReadAssemblyScript();

        Assert.Contains("Read-VendorReleaseInput", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion must be integer 2", script, StringComparison.Ordinal);
        Assert.Contains("stark-vendor-release-input", script, StringComparison.Ordinal);
        Assert.Contains("manifest must be in the fail-closed 'ready' state", script, StringComparison.Ordinal);
        Assert.Contains("nativePayload", script, StringComparison.Ordinal);
        Assert.Contains("sourceIdentity", script, StringComparison.Ordinal);
        Assert.Contains("provenance", script, StringComparison.Ordinal);
        Assert.Contains("catalog", script, StringComparison.Ordinal);
        Assert.Contains("return ,$property.Value", script, StringComparison.Ordinal);
        Assert.Contains("return ,([object[]]@($value))", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Get-VendorPackageVersions", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-JsonPropertyValue -InputObject $releaseInput -Name \"raylib\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorAssemblyIsClosedWorldAndNeverSilentlyOmitsAPackage()
    {
        var script = ReadAssemblyScript();

        Assert.Contains(
            "files inventory does not exactly match the staged vendor file set",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Staged Vendor package image set does not exactly match the schema-2 release-input package declarations",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Compiler-inspected Vendor package ID set does not exactly match the schema-2 release-input package IDs",
            script,
            StringComparison.Ordinal);
        Assert.Contains("duplicates module ownership", script, StringComparison.Ordinal);
        Assert.Contains("imports unavailable official modules", script, StringComparison.Ordinal);
        Assert.Contains("dependency identity set", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Write-Warning", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SDK omitted", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageImagesMustMatchIdentityTargetProfileAndAllDeclaredArtifacts()
    {
        var script = ReadAssemblyScript();

        Assert.Contains("Assert-VendorCandidateMatchesReleaseInput", script, StringComparison.Ordinal);
        Assert.Contains("image/library paths do not match release-input artifacts", script, StringComparison.Ordinal);
        Assert.Contains("image/library hashes do not match release-input artifacts", script, StringComparison.Ordinal);
        Assert.Contains("module set does not match release-input package metadata", script, StringComparison.Ordinal);
        Assert.Contains(
            "package-image native artifact set does not exactly match release-input nativePayload.artifacts",
            script,
            StringComparison.Ordinal);
        Assert.Contains("release SDK assembly requires release-built packages", script, StringComparison.Ordinal);
        Assert.Contains("does not contain a nonempty LLVM data layout", script, StringComparison.Ordinal);
        Assert.Contains("Test-PackageTargetCompatibility", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseInputLicensesAugmentNativeMetadataWithoutInventingLibraries()
    {
        var script = ReadAssemblyScript();

        Assert.Contains("Merge-ReleaseInputLicensesIntoNativeDescriptor", script, StringComparison.Ordinal);
        Assert.Contains("$Native.licenseFiles = [object[]]@($licenseFiles)", script, StringComparison.Ordinal);
        Assert.Contains("$Native.fileChecksums = [object[]]$fileChecksums", script, StringComparison.Ordinal);
        Assert.Contains("@($Native.artifacts) + @($Native.runtimeFiles) + @($licenseFiles)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("self-library", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssemblyKeepsCompilerInspectedPackageFactsAsTheSdkSourceOfTruth()
    {
        var script = ReadAssemblyScript();

        Assert.Contains("Inspection = $inspection", script, StringComparison.Ordinal);
        Assert.Contains("Target = $candidateTarget", script, StringComparison.Ordinal);
        Assert.Contains("Native = $native", script, StringComparison.Ordinal);
        Assert.Contains("$includeDirectories = [System.Collections.Generic.List[string]]::new()", script, StringComparison.Ordinal);
        Assert.Contains("$libraryDirectories = [System.Collections.Generic.List[string]]::new()", script, StringComparison.Ordinal);
        Assert.Contains("libraries = [object[]]$libraries", script, StringComparison.Ordinal);
        Assert.Contains("linkArguments = [object[]]$linkArguments", script, StringComparison.Ordinal);
        Assert.Contains("$pkgConfigPackages.Count -ne 0", script, StringComparison.Ordinal);
        Assert.Contains("baselineFeatures = [object[]]@(Get-ValidatedTargetFeatures", script, StringComparison.Ordinal);
        Assert.Contains("relocationModel = $targetRelocationModel", script, StringComparison.Ordinal);
        Assert.Contains("codeModel = if", script, StringComparison.Ordinal);
        Assert.Contains("cDataModel = if", script, StringComparison.Ordinal);
        Assert.Contains("API/content identity does not match the selected package image", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractualOrderingAndRuntimeIdentityAreCrossPlatformDeterministic()
    {
        var script = ReadAssemblyScript();

        Assert.Contains("Get-OrdinalSortedStrings", script, StringComparison.Ordinal);
        Assert.Contains("Get-OrdinalSortedUniqueStrings", script, StringComparison.Ordinal);
        Assert.Contains("$items.Sort([System.StringComparer]::Ordinal)", script, StringComparison.Ordinal);
        Assert.Contains("[System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)", script, StringComparison.Ordinal);
        Assert.Contains("\"windows-x64\" { \"win-x64\" }", script, StringComparison.Ordinal);
        Assert.Contains("\"macos-arm64\" { \"osx-arm64\" }", script, StringComparison.Ordinal);
        Assert.Contains("does not match release asset", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Sort-Object", script, StringComparison.Ordinal);
    }

    private static string ReadAssemblyScript()
    {
        var repositoryRoot = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "assemble-sdk-manifest.ps1"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
