using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace compiler.IntegrationTests;

public sealed class ReleaseBackendPackagingScriptTests
{
    private static readonly HashSet<string> AllowedStage0ToolNames = new(StringComparer.Ordinal)
    {
        "clang",
        "clang++",
        "ld.lld",
        "ld64.lld",
        "lld",
        "llvm-ar",
        "llvm-ranlib",
        "clang.exe",
        "clang++.exe",
        "lld-link.exe",
        "lld.exe",
        "llvm-ar.exe",
        "llvm-lib.exe",
        "llvm-ranlib.exe"
    };

    [Fact]
    public void LlvmAcquisitionProducesAnExplicitCompilerPrivateRuntimeClosure()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "acquire-llvm-toolchain.ps1"));

        Assert.Contains("payloadKind = \"stark-compiler-private-backend\"", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 2", script, StringComparison.Ordinal);
        Assert.Contains("runtimeClosure = [ordered]@{", script, StringComparison.Ordinal);
        Assert.Contains("compilerResourceRoots = $compilerResourceRoots", script, StringComparison.Ordinal);
        Assert.Contains("acquisitionKind = $acquisitionKind", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-LlvmPinnedSourceBuild", script, StringComparison.Ordinal);
        Assert.Contains("Assert-CompilerPrivateBackendClosure", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsDevelopmentOnlyPattern", script, StringComparison.Ordinal);
        Assert.Contains("Full LLVM development trees must not enter a Stark release", script, StringComparison.Ordinal);
        Assert.Contains("Write-DeterministicJson", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Get-ArrayValues -Value $manifest.copiedRoots", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$platformCopiedRoots", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlvmAcquisitionAcceptsEmptyHardLinkAliasSets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var script = Path.Combine(repositoryRoot, "scripts", "acquire-llvm-toolchain.ps1");
        var scriptText = File.ReadAllText(script);
        Assert.Contains("$hardlinkAliases = @(\n        Convert-ToVerifiedHardLinkAliases", scriptText, StringComparison.Ordinal);

        const string command = """
            & {
                param([string] $ScriptPath)
                Set-StrictMode -Version Latest
                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $ScriptPath,
                    [ref] $tokens,
                    [ref] $errors)
                if ($errors.Count -ne 0) {
                    throw "acquire-llvm-toolchain.ps1 did not parse: $($errors[0].Message)"
                }

                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] `
                        -and $node.Name -eq 'Convert-ToVerifiedHardLinkAliases'
                }, $true)
                if ($null -eq $function) {
                    throw 'Convert-ToVerifiedHardLinkAliases was not found.'
                }

                Invoke-Expression $function.Extent.Text
                $omitted = @(Convert-ToVerifiedHardLinkAliases -DestinationRoot $PWD)
                $explicitNull = @(Convert-ToVerifiedHardLinkAliases -DestinationRoot $PWD -Aliases $null)
                $explicitEmpty = @(Convert-ToVerifiedHardLinkAliases -DestinationRoot $PWD -Aliases @())
                if ($omitted.Count -ne 0 -or $explicitNull.Count -ne 0 -or $explicitEmpty.Count -ne 0) {
                    throw 'Empty hard-link alias inputs must produce an empty array.'
                }
            }
            """;

        var result = await RunProcessAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-Command", command, script],
            repositoryRoot);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    [Fact]
    public void ReleasePackagingCopiesOnlyHashVerifiedClosureMembers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));

        Assert.Contains("Copy-CompilerPrivateBackendFromManifest", script, StringComparison.Ordinal);
        Assert.Contains("Assert-CompilerPrivateBackendManifest", script, StringComparison.Ordinal);
        Assert.Contains("failed SHA-256 validation", script, StringComparison.Ordinal);
        Assert.Contains("contains untracked file", script, StringComparison.Ordinal);
        Assert.Contains("duplicate or case-colliding path", script, StringComparison.Ordinal);
        Assert.Contains("Compiler-private backend: $toolchainText", script, StringComparison.Ordinal);
        Assert.Contains("compilerPrivateBackend = $toolchainPath", script, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Copy-TreeFiltered -Source $toolchainSourcePath -Destination $stagedToolchainRoot",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredBackendProgramsStayInsideTheStage0ToolAllowlist()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "llvm-22.1.8-assets.json")));

        foreach (var platform in document.RootElement.GetProperty("platforms").EnumerateObject())
        {
            var tools = platform.Value.GetProperty("requiredTools")
                .EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray();

            Assert.Contains(tools, static path => path is "bin/clang" or "bin/clang.exe");
            foreach (var tool in tools)
            {
                Assert.StartsWith("bin/", tool, StringComparison.Ordinal);
                Assert.Contains(Path.GetFileName(tool), AllowedStage0ToolNames);
            }
        }
    }

    [Fact]
    public void LlvmAcquisitionManifestPinsEveryAvailableOfficialBinaryInput()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var acquisitionDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "llvm-22.1.8-assets.json")));
        using var dependencyDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "dependencies.json")));

        var platforms = acquisitionDocument.RootElement.GetProperty("platforms");
        var binaryPlatforms = platforms.EnumerateObject()
            .Where(static platform => platform.Value.TryGetProperty("archive", out _))
            .ToArray();
        Assert.Equal(
            new[] { "linux-arm64", "linux-x64", "macos-arm64", "windows-arm64", "windows-x64" },
            binaryPlatforms.Select(static item => item.Name).Order(StringComparer.Ordinal));

        var privateBackend = dependencyDocument.RootElement.GetProperty("dependencies")
            .EnumerateArray()
            .Single(static dependency => dependency.GetProperty("kind").GetString() == "compiler-private-backend");
        var selections = privateBackend.GetProperty("selections").EnumerateArray()
            .ToDictionary(static selection => selection.GetProperty("target").GetString()!, StringComparer.Ordinal);
        foreach (var platform in binaryPlatforms)
        {
            var archive = platform.Value.GetProperty("archive");
            var selection = selections[platform.Name];
            Assert.Equal("qualified-input", selection.GetProperty("qualificationStatus").GetString());
            Assert.Equal(archive.GetProperty("url").GetString(), selection.GetProperty("archiveUrl").GetString());
            Assert.Equal(archive.GetProperty("sha256").GetString(), selection.GetProperty("archiveSha256").GetString());
            Assert.True(archive.GetProperty("size").GetInt64() > 0);
            Assert.True(archive.GetProperty("signature").GetProperty("size").GetInt64() > 0);
            Assert.True(archive.GetProperty("attestation").GetProperty("size").GetInt64() > 0);
        }

        Assert.Equal(
            "805efad2bb91cb4967fa569e0881d10c0f69c04461cf671cccbae19f547acc34",
            platforms.GetProperty("linux-arm64").GetProperty("archive").GetProperty("sha256").GetString());
        Assert.Equal(
            "de718c58ebbc5f61d58c17b90457fcf42983bc2c4a4aba3e010d108713bfd7f1",
            platforms.GetProperty("windows-arm64").GetProperty("archive").GetProperty("sha256").GetString());
    }

    [Fact]
    public void MacOsX64BackendHasAQualifiedPinnedOptimizedSourceBuild()
    {
        var repositoryRoot = FindRepositoryRoot();
        using var acquisitionDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "llvm-22.1.8-assets.json")));
        using var dependencyDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "eng", "release", "dependencies.json")));

        var platform = acquisitionDocument.RootElement.GetProperty("platforms").GetProperty("macos-x64");
        Assert.False(platform.TryGetProperty("archive", out _));
        Assert.Equal("osx-x64", platform.GetProperty("runtimeIdentifier").GetString());
        var build = platform.GetProperty("sourceBuild");
        Assert.Equal("macos", build.GetProperty("hostOperatingSystem").GetString());
        Assert.Equal("x64", build.GetProperty("hostArchitecture").GetString());
        Assert.Equal("11.0", build.GetProperty("minimumDeploymentTarget").GetString());
        Assert.Equal("Release", build.GetProperty("configuration").GetString());
        Assert.Equal("O3", build.GetProperty("optimization").GetString());
        Assert.Equal("Thin", build.GetProperty("lto").GetString());
        Assert.Equal("install-distribution-stripped", build.GetProperty("buildTarget").GetString());
        Assert.Equal(["clang", "lld"], build.GetProperty("projects").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(["AArch64", "X86"], build.GetProperty("targetsToBuild").EnumerateArray().Select(static item => item.GetString()));
        var cmakeOptions = build.GetProperty("cmakeOptions").EnumerateArray().Select(static item => item.GetString()).ToArray();
        var distributionOption = Assert.Single(
            cmakeOptions,
            static option => option!.StartsWith("-DLLVM_DISTRIBUTION_COMPONENTS=", StringComparison.Ordinal));
        Assert.Equal(
            ["clang", "clang-resource-headers", "lld", "llvm-ar", "llvm-ranlib"],
            distributionOption!["-DLLVM_DISTRIBUTION_COMPONENTS=".Length..].Split(';'));
        Assert.Contains("-DCMAKE_OSX_ARCHITECTURES=x86_64", cmakeOptions);
        Assert.Contains("-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0", cmakeOptions);
        Assert.Contains("-DLLVM_ENABLE_LTO=Thin", cmakeOptions);
        Assert.Contains("-DLLVM_BUILD_LLVM_DYLIB=OFF", cmakeOptions);
        Assert.Contains("-DLLVM_LINK_LLVM_DYLIB=OFF", cmakeOptions);
        var apple = build.GetProperty("qualifiedAppleToolchain");
        Assert.Equal("Xcode 16.4\nBuild version 16F6", apple.GetProperty("xcodeVersion").GetString());
        Assert.Equal("15.5", apple.GetProperty("sdkVersion").GetString());
        Assert.Equal("Apple clang version 17.0.0 (clang-1700.0.13.5)", apple.GetProperty("clangVersionLine").GetString());

        var privateBackend = dependencyDocument.RootElement.GetProperty("dependencies")
            .EnumerateArray()
            .Single(static dependency => dependency.GetProperty("kind").GetString() == "compiler-private-backend");
        var selection = privateBackend.GetProperty("selections").EnumerateArray()
            .Single(static item => item.GetProperty("target").GetString() == "macos-x64");
        Assert.Equal("pinned-source-build", selection.GetProperty("acquisition").GetString());
        Assert.Equal("qualified-build", selection.GetProperty("qualificationStatus").GetString());
        Assert.Equal("ddf24627821fb02322ddfcd253715cc8343472ff", selection.GetProperty("qualificationCommit").GetString());
        Assert.Equal("https://github.com/AlexanderBaggett/Stark/actions/runs/30944641351", selection.GetProperty("qualificationWorkflow").GetString());
        Assert.Equal("8909655368", selection.GetProperty("qualificationArtifactId").GetString());
    }

    [Fact]
    public void PinnedSourceBuildUsesExplicitToolsAndRecordsBuildProvenance()
    {
        var repositoryRoot = FindRepositoryRoot();
        var acquisition = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "acquire-llvm-toolchain.ps1"));
        var sourceBuild = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "llvm-source-build.ps1"));
        var packaging = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var localDriver = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-release.ps1"));

        Assert.Contains("[string] $CMakePath", acquisition, StringComparison.Ordinal);
        Assert.Contains("[string] $NinjaPath", acquisition, StringComparison.Ordinal);
        Assert.Contains(". (Join-Path $PSScriptRoot \"llvm-source-build.ps1\")", acquisition, StringComparison.Ordinal);
        Assert.Contains("-CMakePath $CMakePath", acquisition, StringComparison.Ordinal);
        Assert.Contains("-NinjaPath $NinjaPath", acquisition, StringComparison.Ordinal);
        Assert.Contains("require an explicit -$($Name)Path", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("ProcessArchitecture", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("-ffile-prefix-map=", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("-Wl,-no_uuid", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("SOURCE_DATE_EPOCH", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("ZERO_AR_DATE", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("Resolve-LlvmSourceBuildApplePath", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("ResolveLinkTarget($true)", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("[switch] $PreserveInvocationPath", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("return $invocationPath", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("-Label \"LLVM source-build Apple Clang++\" `", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("-PreserveInvocationPath", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("Assert-LlvmSourceBuildCxxCompilerConfiguration", sourceBuild, StringComparison.Ordinal);
        Assert.Contains(
            "GetFileName($configuredCompiler) -cne \"clang++\"",
            sourceBuild,
            StringComparison.Ordinal);
        Assert.Contains("-ExpectedCompilerPath $clangxxPath", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("com.apple.pkg.CLTools_Executables", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("appleToolchain = [ordered]@{", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("qualifiedAppleToolchain", sourceBuild, StringComparison.Ordinal);
        Assert.True(sourceBuild.IndexOf("qualifiedAppleToolchain", StringComparison.Ordinal) < sourceBuild.IndexOf("Invoke-LlvmSourceBuildProcess", sourceBuild.IndexOf("function Invoke-LlvmPinnedSourceBuild", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Contains("clangSha256", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("& $FileName @Arguments 2>&1 | ForEach-Object", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("Starting $Label", sourceBuild, StringComparison.Ordinal);
        Assert.Contains("Completed $Label", sourceBuild, StringComparison.Ordinal);
        Assert.DoesNotContain("$output = @(& $FileName @Arguments 2>&1)", sourceBuild, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Command cmake", sourceBuild, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Command ninja", sourceBuild, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("binaryArchiveSha256 = if ($null -eq $backendBinaryArchive)", packaging, StringComparison.Ordinal);
        Assert.Contains("sourceBuild = $backendSourceBuild", packaging, StringComparison.Ordinal);
        Assert.Contains("-CMakePath \"${{ steps.build_tools.outputs.cmake_path }}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("-NinjaPath \"${{ steps.build_tools.outputs.ninja_path }}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("$llvmArguments += @(\"-CMakePath\", $resolvedCMakePath, \"-NinjaPath\", $resolvedNinjaPath)", localDriver, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsX64QualificationIsNativeEvidenceOnlyAndCannotPublish()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "qualify-private-backend.yml"));
        var releaseWorkflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var identityScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "get-release-configuration-identity.ps1"));

        Assert.Contains("runs-on: macos-15-intel", workflow, StringComparison.Ordinal);
        Assert.Contains("contents: read", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", workflow, StringComparison.Ordinal);
        Assert.Contains("TARGET_ID: macos-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("ProcessArchitecture", workflow, StringComparison.Ordinal);
        Assert.Contains("acquire-release-build-tools.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("acquire-llvm-toolchain.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("qualify-private-backend", workflow, StringComparison.Ordinal);
        Assert.Contains("private-backend-report.json", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish src/compiler.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/stage0/macos-x64/compiler", workflow, StringComparison.Ordinal);
        Assert.Contains("STAGE0_COMPILER=$stage0Compiler", workflow, StringComparison.Ordinal);
        Assert.Contains("& $env:STAGE0_COMPILER", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts/stage0/macos-x64/stark", workflow, StringComparison.Ordinal);
        Assert.Contains("stdlib/src/System.stark", workflow, StringComparison.Ordinal);
        Assert.Contains("--package-profile release", workflow, StringComparison.Ordinal);
        Assert.Contains("examples/hello.stark", workflow, StringComparison.Ordinal);
        Assert.Contains("Hello, World!", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/toolchain/macos-x64/provenance", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("package-release.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("reconcile-github-release", workflow, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/qualify-private-backend.yml", identityScript, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/release-contract.yml", identityScript, StringComparison.Ordinal);

        Assert.Contains("GenerateMatrix(result, command.HasFlag(\"--include-planned\"))", File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "Stark.ReleaseTools", "ReleaseConfiguration.cs")), StringComparison.Ordinal);
        Assert.Contains("include_planned:", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("Planned targets are diagnostic-only", File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "Stark.ReleaseTools", "ReleasePlanPreparer.cs")), StringComparison.Ordinal);
        Assert.Contains("matrix.private_backend_acquisition == 'pinned-source-build'", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("qualify-private-backend", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("& $env:STAGE0_COMPILER", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet run --no-restore --project src", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("-AllowPlannedTarget", releaseWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseQualityGateUsesThePinnedBackendInsteadOfRunnerClang()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var qualityStart = workflow.IndexOf("  quality:", StringComparison.Ordinal);
        var buildStart = workflow.IndexOf("  build:", qualityStart, StringComparison.Ordinal);

        Assert.True(qualityStart >= 0 && buildStart > qualityStart, "Release workflow quality/build boundaries are missing.");
        var qualityJob = workflow[qualityStart..buildStart];
        var acquireIndex = qualityJob.IndexOf("Acquire and verify pinned LLVM for repository quality", StringComparison.Ordinal);
        var gateIndex = qualityJob.IndexOf("Run mandatory repository quality gate", StringComparison.Ordinal);

        Assert.True(acquireIndex >= 0 && gateIndex > acquireIndex, "Pinned LLVM must be verified before the quality suite starts.");
        Assert.Contains("actions/cache/restore@55cc8345863c7cc4c66a329aec7e433d2d1c52a9", qualityJob, StringComparison.Ordinal);
        Assert.Contains("actions/cache/save@55cc8345863c7cc4c66a329aec7e433d2d1c52a9", qualityJob, StringComparison.Ordinal);
        Assert.Contains("-AssetSuffix \"linux-x64\"", qualityJob, StringComparison.Ordinal);
        Assert.Contains("scripts/llvm-22.1.8-assets.json", qualityJob, StringComparison.Ordinal);
        Assert.Contains("stark-compiler-private-backend", qualityJob, StringComparison.Ordinal);
        Assert.Contains("clang version $expectedVersion", qualityJob, StringComparison.Ordinal);
        Assert.DoesNotContain("STARK_TOOLCHAIN_DIR=$toolchainRoot", qualityJob, StringComparison.Ordinal);
        Assert.DoesNotContain("STARK_CLANG=$clangPath", qualityJob, StringComparison.Ordinal);
        Assert.DoesNotContain("STARK_LINKER=$clangPath", qualityJob, StringComparison.Ordinal);
        Assert.DoesNotContain("STARK_ARCHIVER=$llvmArPath", qualityJob, StringComparison.Ordinal);
        Assert.Contains("$env:GITHUB_PATH", qualityJob, StringComparison.Ordinal);
        Assert.DoesNotContain("apt-get", qualityJob, StringComparison.OrdinalIgnoreCase);

        var qualityRunner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-release-quality-gate.ps1"));
        var buildIndex = qualityRunner.IndexOf("Build the complete solution in Release", StringComparison.Ordinal);
        var manifestIndex = qualityRunner.IndexOf("Write development SDK manifest for repository tests", StringComparison.Ordinal);
        var testIndex = qualityRunner.IndexOf("Run the complete solution test suite in Release", StringComparison.Ordinal);
        Assert.True(buildIndex >= 0 && manifestIndex > buildIndex && testIndex > manifestIndex,
            "The development SDK manifest must be generated from the built compiler before repository tests start.");
        Assert.Contains("--write-development-sdk-manifest", qualityRunner, StringComparison.Ordinal);
        Assert.Contains("Test-Path -LiteralPath $developmentSdkManifest -PathType Leaf", qualityRunner, StringComparison.Ordinal);

        var buildJob = workflow[buildStart..];
        Assert.Contains("matrix.target_id == 'linux-x64' || matrix.private_backend_acquisition == 'pinned-source-build'", buildJob, StringComparison.Ordinal);
        Assert.Contains("key: llvm-input-${{ matrix.target_id }}-", buildJob, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseMatrixQualifiesAssemblyBridgeArchivesOnEverySelectedTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "qualify-assembly-bridge.ps1"));

        Assert.Contains("Qualify assembly bridge archive and ThinLTO lowering", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/qualify-assembly-bridge.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-TargetId \"${{ matrix.target_id }}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("-TargetTriple \"${{ matrix.target_triple }}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/backend-qualification/${{ matrix.asset_suffix }}/assembly-bridge", workflow, StringComparison.Ordinal);
        Assert.Contains("producerObjectIsLlvmBitcode = $true", script, StringComparison.Ordinal);
        Assert.Contains("$manifestPath = Join-Path $packageDirectory \"libBridge.starkpkg\"", script, StringComparison.Ordinal);
        Assert.Contains("\"--package-image-output\", $manifestPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangeExtension($libraryPath", script, StringComparison.Ordinal);
        Assert.Contains("sourceRemovedBeforeConsumerBuild = $true", script, StringComparison.Ordinal);
        Assert.Contains("@llvm.used = appending global [1 x ptr] [ptr @OpaqueTarget]", script, StringComparison.Ordinal);
        Assert.Contains("-ExpectedExitCode 73", script, StringComparison.Ordinal);
        Assert.EndsWith("exit 0\n", script.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("if (-not $?) { throw \"Assembly bridge archive/ThinLTO qualification failed", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("OperatingSystem.IsWindows", script, StringComparison.Ordinal);

        var archiveSmoke = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-release-archive.ps1"));
        Assert.Contains("[string] $ReportPath", archiveSmoke, StringComparison.Ordinal);
        Assert.Contains("packagedSystemAssembly = [ordered]@{", archiveSmoke, StringComparison.Ordinal);
        Assert.Contains("memory(argmem: readwrite)", archiveSmoke, StringComparison.Ordinal);
        Assert.Contains("archive-smoke-${{ matrix.asset_suffix }}.json", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannedTargetPackagingRequiresAnExplicitDiagnosticOverride()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scripts = new[]
        {
            "prepare-vendor-release-input.ps1",
            "prepare-core-vendor-release-input.ps1",
            "prepare-raylib-release-input.ps1",
            "prepare-glfw-vendor-release-input.ps1",
            "prepare-sdl3-vendor-release-input.ps1",
            "prepare-sqlite-vendor-release-input.ps1",
        }.Select(name => File.ReadAllText(Path.Combine(repositoryRoot, "scripts", name))).ToArray();

        foreach (var script in scripts)
        {
            foreach (var target in new[] { "linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64" })
            {
                Assert.Contains(target, script, StringComparison.Ordinal);
            }
        }

        var vendor = scripts[0];
        var glfw = scripts[3];
        var sdl3 = scripts[4];
        var packaging = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));
        foreach (var guardedScript in new[] { vendor, glfw, sdl3, packaging })
        {
            Assert.Contains("[switch] $AllowPlannedTarget", guardedScript, StringComparison.Ordinal);
            Assert.Contains("supportTier", guardedScript, StringComparison.Ordinal);
        }
        Assert.Contains("diagnostic-only", vendor, StringComparison.Ordinal);
        Assert.Contains("diagnostic-only", packaging, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowUsesHermeticRestoreAndNamedScriptArguments()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));
        var qualification = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "qualify-private-backend.yml"));

        foreach (var definition in new[] { workflow, qualification })
        {
            Assert.Contains("DOTNET_INSTALL_DIR: ${{ runner.temp }}/stark-dotnet", definition, StringComparison.Ordinal);
            Assert.Contains("-p:DisableImplicitLibraryPacksFolder=true", definition, StringComparison.Ordinal);
            Assert.Contains("-p:DisableTransitiveFrameworkReferenceDownloads=true", definition, StringComparison.Ordinal);
            Assert.DoesNotMatch("(?m)^    env:\\r?\\n      DOTNET_INSTALL_DIR:", definition);
            Assert.Equal(
                Regex.Matches(definition, "(?m)^        uses: actions/setup-dotnet@").Count,
                Regex.Matches(definition, "(?m)^      - name: .+\\r?\\n        env:\\r?\\n          DOTNET_INSTALL_DIR: \\$\\{\\{ runner\\.temp \\}\\}/stark-dotnet\\r?\\n        uses: actions/setup-dotnet@").Count);
        }

        Assert.StartsWith("name: Release", workflow, StringComparison.Ordinal);
        Assert.StartsWith("name: Qualify compiler-private backend", qualification, StringComparison.Ordinal);
        Assert.Contains("src/obj/project.assets.json", workflow, StringComparison.Ordinal);
        Assert.Contains("-AllowPlannedTarget:(-not [bool]::Parse(\"${{ matrix.release_enabled }}\"))", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("& ./scripts/prepare-vendor-release-input.ps1 @arguments", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("& ./scripts/package-release.ps1 @arguments", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowsPinNode24NativeActions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflows = string.Join(
            Environment.NewLine,
            File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "qualify-private-backend.yml")),
            File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release-contract.yml")),
            File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml")));

        foreach (var retiredReference in new[]
        {
            "actions/checkout@34e114876b0b11c390a56381ad16ebd13914f8d5",
            "actions/cache@0057852bfaa89a56745cba8c7296529d2fc39830",
            "actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9",
            "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02",
            "actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093",
            "softprops/action-gh-release@3bb12739c298aeb8a4eeaf626c5b8d85266b0e65",
        })
        {
            Assert.DoesNotContain(retiredReference, workflows, StringComparison.Ordinal);
        }

        foreach (var node24Reference in new[]
        {
            "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
            "actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9",
            "actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68",
            "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
            "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c",
            "softprops/action-gh-release@3d0d9888cb7fd7b750713d6e236d1fcb99157228",
        })
        {
            Assert.Contains(node24Reference, workflows, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EmptyMacRuntimeLibrarySetBindsForACompilerResourceClosureEntry()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var packageScript = Path.Combine(repositoryRoot, "scripts", "package-release.ps1");
        var escapedPackageScript = packageScript.Replace("'", "''", StringComparison.Ordinal);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "stark-release-backend-packaging-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var probeScript = Path.Combine(temporaryRoot, "empty-runtime-library-set.ps1");

        try
        {
            File.WriteAllText(
                probeScript,
                $$"""
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    '{{escapedPackageScript}}',
                    [ref] $tokens,
                    [ref] $parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw "package-release.ps1 did not parse: $($parseErrors[0].Message)"
                }

                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] `
                        -and $node.Name -eq 'Assert-CompilerPrivateBackendEntryClass'
                }, $true)
                if ($null -eq $function) {
                    throw 'Assert-CompilerPrivateBackendEntryClass was not found.'
                }

                Invoke-Expression $function.Extent.Text
                $requiredTools = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                [void] $requiredTools.Add('bin/clang')
                $runtimeLibraries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                Assert-CompilerPrivateBackendEntryClass `
                    -RelativePath 'lib/clang/22/include/stddef.h' `
                    -RequiredTools $requiredTools `
                    -RuntimeLibraries $runtimeLibraries `
                    -CompilerResourceRoots @('lib/clang')
                Write-Output 'empty-runtime-set-accepted'
                """);

            var result = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-File", probeScript],
                repositoryRoot);

            Assert.True(
                result.ExitCode == 0,
                $"Empty runtime-library binding probe failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("empty-runtime-set-accepted", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AcquiredRuntimeClosureIncludesThePortableDotfileOwnerMarker()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var acquisitionScript = Path.Combine(repositoryRoot, "scripts", "acquire-llvm-toolchain.ps1");
        var escapedAcquisitionScript = acquisitionScript.Replace("'", "''", StringComparison.Ordinal);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "stark-release-backend-packaging-tests",
            Guid.NewGuid().ToString("N"));
        var payloadRoot = Path.Combine(temporaryRoot, "backend");
        Directory.CreateDirectory(Path.Combine(payloadRoot, "bin"));
        File.WriteAllText(Path.Combine(payloadRoot, ".stark-llvm-toolchain-owner.json"), "{}\n");
        File.WriteAllText(Path.Combine(payloadRoot, "bin", "clang"), "compiler\n");
        var probeScript = Path.Combine(temporaryRoot, "owner-marker-closure.ps1");

        try
        {
            File.WriteAllText(
                probeScript,
                $$"""
                param([Parameter(Mandatory = $true)][string] $PayloadRoot)
                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    '{{escapedAcquisitionScript}}',
                    [ref] $tokens,
                    [ref] $parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw "acquire-llvm-toolchain.ps1 did not parse: $($parseErrors[0].Message)"
                }

                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] `
                        -and $node.Name -eq 'Get-OutputFileManifest'
                }, $true)
                if ($null -eq $function) {
                    throw 'Get-OutputFileManifest was not found.'
                }

                Invoke-Expression $function.Extent.Text
                $closure = @(Get-OutputFileManifest -Root $PayloadRoot)
                $ownerEntries = @($closure | Where-Object {
                    $_.path -eq '.stark-llvm-toolchain-owner.json'
                })
                if ($ownerEntries.Count -ne 1) {
                    throw "Runtime closure contained $($ownerEntries.Count) portable owner markers instead of one."
                }
                if ($closure.Count -ne 2) {
                    throw "Runtime closure contained $($closure.Count) files instead of two."
                }
                Write-Output 'owner-marker-included'
                """);

            var result = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-File", probeScript, payloadRoot],
                repositoryRoot);

            Assert.True(
                result.ExitCode == 0,
                $"Portable owner-marker closure probe failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("owner-marker-included", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static async Task<string?> FindPowerShellAsync(string workingDirectory)
    {
        var explicitPowerShell = Environment.GetEnvironmentVariable("POWERSHELL_EXE");
        var candidates = OperatingSystem.IsWindows()
            ? new[] { explicitPowerShell, "pwsh.exe", "powershell.exe" }
            : new[] { explicitPowerShell, "pwsh" };
        foreach (var candidateValue in candidates.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var candidate = candidateValue!;
            try
            {
                var result = await RunProcessAsync(
                    candidate,
                    ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"],
                    workingDirectory);
                if (result.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Win32Exception)
            {
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
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
