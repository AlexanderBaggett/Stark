using Stark.Compiler;

namespace compiler.Tests;

public sealed class TargetCompatibilityValidatorTests
{
    [Fact]
    public void SdkPackageGenericCpuAndFeatureSubsetAcceptStrongerApplicationTarget()
    {
        var diagnostics = CompareTargets(
            packageCpu: "generic",
            packageFeatures: ["+sse2"],
            activeCpu: "znver4",
            activeFeatures: ["+avx2", "+sse2"],
            isSdkPackage: true);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void SdkPackageRejectsFeatureMissingFromApplicationTarget()
    {
        var diagnostics = CompareTargets(
            packageCpu: "generic",
            packageFeatures: ["+sse2", "+sse4.2"],
            activeCpu: "znver4",
            activeFeatures: ["+avx2", "+sse2"],
            isSdkPackage: true);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("STK7314", diagnostic.Code);
        Assert.Contains("sse4.2", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryPackagesKeepExactCpuAndOrderedFeatureSafety()
    {
        var diagnostics = CompareTargets(
            packageCpu: "generic",
            packageFeatures: ["+sse2", "+avx2"],
            activeCpu: "znver4",
            activeFeatures: ["+avx2", "+sse2"],
            isSdkPackage: false);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "STK7313");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "STK7314");
    }

    [Fact]
    public void SdkPackagesAcceptStructuredTripleAliasesWhileOrdinaryPackagesRemainExact()
    {
        var sdkDiagnostics = CompareTargets(
            packageCpu: "generic",
            packageFeatures: [],
            activeCpu: "generic",
            activeFeatures: [],
            isSdkPackage: true,
            packageTriple: "x86_64-pc-linux-gnu",
            activeTriple: "amd64-unknown-linux-gnu");
        var ordinaryDiagnostics = CompareTargets(
            packageCpu: "generic",
            packageFeatures: [],
            activeCpu: "generic",
            activeFeatures: [],
            isSdkPackage: false,
            packageTriple: "x86_64-pc-linux-gnu",
            activeTriple: "amd64-unknown-linux-gnu");

        Assert.Empty(sdkDiagnostics);
        Assert.Contains(ordinaryDiagnostics, diagnostic => diagnostic.Code == "STK7311");
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", "amd64-pc-linux-musl")]
    [InlineData("arm64-apple-macosx14.0.0", "aarch64-apple-macosx13.0.0")]
    [InlineData("arm64-apple-macosx14.0.0", "aarch64-unknown-linux-gnu")]
    public void SdkPackagesRejectStructuredAbiDeploymentAndOperatingSystemDifferences(
        string packageTriple,
        string activeTriple)
    {
        var diagnostics = CompareTargets(
            packageCpu: "generic",
            packageFeatures: [],
            activeCpu: "generic",
            activeFeatures: [],
            isSdkPackage: true,
            packageTriple: packageTriple,
            activeTriple: activeTriple);

        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Code == "STK7311");
        Assert.Contains("structured architecture/OS/ABI/deployment facts", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkPackageAliasStillRequiresExactBackendDataLayout()
    {
        var diagnostics = CompareTargets(
            packageCpu: "generic",
            packageFeatures: [],
            activeCpu: "generic",
            activeFeatures: [],
            isSdkPackage: true,
            packageTriple: "arm64-apple-macosx11.0.0",
            activeTriple: "aarch64-apple-macosx14.0.0",
            packageDataLayout: "e-m:o-i64:64-i128:128-n32:64-S128",
            activeDataLayout: "e-m:o-i64:64-n32:64-S128");

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "STK7311");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "STK7312");
    }

    [Theory]
    [InlineData("release", "dev", true)]
    [InlineData("release", "release", true)]
    [InlineData("dev", "dev", true)]
    [InlineData("dev", "release", false)]
    [InlineData("debug", "dev", false)]
    [InlineData("release", "debug", false)]
    [InlineData("Release", "dev", false)]
    [InlineData("release ", "dev", false)]
    [InlineData("", "dev", false)]
    public void PackageBuildProfileCompatibilityIsDirectionalAndStrict(
        string packageBuildProfile,
        string activeBuildProfile,
        bool expected)
    {
        Assert.Equal(
            expected,
            TargetCompatibilityValidator.IsPackageBuildProfileCompatible(
                packageBuildProfile,
                activeBuildProfile));
    }

    private static IReadOnlyList<CompilerDiagnostic> CompareTargets(
        string? packageCpu,
        IReadOnlyList<string> packageFeatures,
        string? activeCpu,
        IReadOnlyList<string> activeFeatures,
        bool isSdkPackage,
        string packageTriple = "x86_64-unknown-linux-gnu",
        string activeTriple = "x86_64-unknown-linux-gnu",
        string packageDataLayout = "e-p:64:64",
        string activeDataLayout = "e-p:64:64")
    {
        var packageTarget = new StarkPackageTargetManifest(
            packageTriple,
            DataLayout: packageDataLayout,
            Cpu: packageCpu,
            Features: packageFeatures,
            RelocationModel: "pic",
            CodeModel: "small");
        var activeTarget = new StarkPackageTargetManifest(
            activeTriple,
            DataLayout: activeDataLayout,
            Cpu: activeCpu,
            Features: activeFeatures,
            RelocationModel: "pic",
            CodeModel: "small");
        var diagnostics = new List<CompilerDiagnostic>();
        TargetCompatibilityValidator.ComparePackageTarget(
            packageTarget,
            activeTarget,
            "Vendor.Probe",
            new SourceLocation("Probe.starkpkg", 1, 1),
            diagnostics,
            isSdkPackage);
        return diagnostics;
    }
}
