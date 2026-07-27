using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace compiler.IntegrationTests;

public sealed class ReleaseInstallerContractTests
{
    [Fact]
    public async Task UnixInstallerLifecycleTestsPassOnUnixHosts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var testScript = Path.Combine(repositoryRoot, "tests", "release-installers", "test-installers.sh");
        var result = await RunProcessAsync("/bin/sh", [testScript], repositoryRoot);

        Assert.True(
            result.ExitCode == 0,
            $"Release installer tests failed.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
        Assert.Contains("Release installer tests passed.", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShellInstallersParseWhenPowerShellIsAvailable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scripts = new[]
        {
            Path.Combine(repositoryRoot, "scripts", "release-installers", "install.ps1"),
            Path.Combine(repositoryRoot, "scripts", "release-installers", "uninstall.ps1"),
            Path.Combine(repositoryRoot, "scripts", "stage-release-installers.ps1"),
            Path.Combine(repositoryRoot, "scripts", "generate-release-docs.ps1"),
            Path.Combine(repositoryRoot, "scripts", "release-documentation-contract.ps1"),
            Path.Combine(repositoryRoot, "scripts", "smoke-release-install.ps1"),
        };
        const string parserCommand = """
            & {
                param([string] $ScriptPath)
                $tokens = $null
                $errors = $null
                [void][System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) {
                    $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }
                    exit 1
                }
            }
            """;

        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        foreach (var script in scripts)
        {
            var result = await RunProcessAsync(
                powershell,
                ["-NoProfile", "-NonInteractive", "-Command", parserCommand, script],
                repositoryRoot);
            Assert.True(
                result.ExitCode == 0,
                $"PowerShell parser rejected '{script}'.{Environment.NewLine}{result.Stderr}");
        }
    }

    [Fact]
    public async Task ReleaseDocumentationIsGeneratedForEveryManifestTargetWhenPowerShellIsAvailable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        using var targetsDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "targets.json")));
        using var dependenciesDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "eng", "release", "dependencies.json")));
        var llvmVersion = dependenciesDocument.RootElement.GetProperty("dependencies")
            .EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "llvm-22.1.8")
            .GetProperty("version")
            .GetString()!;
        var dotnetDependency = dependenciesDocument.RootElement.GetProperty("dependencies")
            .EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "dotnet-stage0-runtime");
        var dotnetSdkVersion = dotnetDependency.GetProperty("version").GetString()!;
        var dotnetRuntimeVersion = dotnetDependency.GetProperty("runtimeVersion").GetString()!;
        var generator = Path.Combine(repositoryRoot, "scripts", "generate-release-docs.ps1");
        var contractHelper = Path.Combine(repositoryRoot, "scripts", "release-documentation-contract.ps1");
        const string contractValidationCommand = """
            & {
                param([string] $Helper, [string] $StageRoot, [string] $TargetTriple)
                . $Helper
                Assert-ReleaseDocumentationCommandContract `
                    -SdkRoot $StageRoot `
                    -ExpectedTargetTriple $TargetTriple | Out-Null
            }
            """;
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"stark-release-doc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            foreach (var target in targetsDocument.RootElement.GetProperty("targets").EnumerateArray())
            {
                var targetId = target.GetProperty("id").GetString()!;
                var stageRoot = Path.Combine(temporaryRoot, targetId);
                Directory.CreateDirectory(Path.Combine(stageRoot, "bin"));
                Directory.CreateDirectory(Path.Combine(stageRoot, "examples", "hello"));
                var compilerName = target.GetProperty("operatingSystem").GetString() == "windows"
                    ? "stark.exe"
                    : "stark";
                File.WriteAllText(Path.Combine(stageRoot, "bin", compilerName), string.Empty);
                File.WriteAllText(Path.Combine(stageRoot, "examples", "hello.stark"), "module Hello\n");
                File.WriteAllText(Path.Combine(stageRoot, "examples", "hello", "Stark.toml"), "[project]\nname = \"hello\"\n");
                var result = await RunProcessAsync(
                    powershell,
                    [
                        "-NoProfile", "-NonInteractive", "-File", generator,
                        "-StageRoot", stageRoot,
                        "-Version", "v9.8.7",
                        "-AssetSuffix", targetId,
                        "-PackagedRuntimeIdentifier", target.GetProperty("runtimeIdentifier").GetString()!,
                        "-PackagedTargetTriple", target.GetProperty("targetTriple").GetString()!,
                        "-PackagedArchiveKind", target.GetProperty("archiveKind").GetString()!,
                        "-PackagedLlvmVersion", llvmVersion,
                    ],
                    repositoryRoot);
                Assert.True(
                    result.ExitCode == 0,
                    $"Documentation generation failed for {targetId}.{Environment.NewLine}{result.Stderr}");

                var readme = File.ReadAllText(Path.Combine(stageRoot, "README.md"));
                var install = File.ReadAllText(Path.Combine(stageRoot, "INSTALL.md"));
                using var commandContract = JsonDocument.Parse(File.ReadAllText(
                    Path.Combine(stageRoot, "release-commands.json")));
                var commandRoot = commandContract.RootElement;
                Assert.Equal(1, commandRoot.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("stark-release-quick-start", commandRoot.GetProperty("kind").GetString());
                Assert.Equal(targetId, commandRoot.GetProperty("targetId").GetString());
                Assert.Equal(target.GetProperty("targetTriple").GetString(), commandRoot.GetProperty("targetTriple").GetString());
                Assert.Equal(
                    target.GetProperty("operatingSystem").GetString() == "windows" ? "bin/stark.exe" : "bin/stark",
                    commandRoot.GetProperty("paths").GetProperty("compiler").GetString());
                Assert.Equal("examples/hello.stark", commandRoot.GetProperty("paths").GetProperty("helloSource").GetString());
                Assert.Equal("examples/hello", commandRoot.GetProperty("paths").GetProperty("helloProject").GetString());
                var commandDocumentation = commandRoot.GetProperty("documentation");
                var quickStartMarker = commandDocumentation.GetProperty("quickStartMarker").GetString()!;
                var quickStartMarkdown = commandDocumentation.GetProperty("quickStartMarkdown").GetString()!;
                var targetTriple = target.GetProperty("targetTriple").GetString()!;
                var expectedQuickStartMarkdown = target.GetProperty("operatingSystem").GetString() == "windows"
                    ? string.Join('\n',
                        "```powershell",
                        "$env:Path = \"$PWD\\bin;$env:Path\"",
                        "stark doctor --strict",
                        "stark examples/hello.stark --check",
                        "Push-Location .\\examples\\hello",
                        "try {",
                        $"    stark build --target {targetTriple}",
                        $"    stark run --target {targetTriple}",
                        "} finally {",
                        "    Pop-Location",
                        "}",
                        "```")
                    : string.Join('\n',
                        "```sh",
                        "export PATH=\"$PWD/bin:$PATH\"",
                        "stark doctor --strict",
                        "stark examples/hello.stark --check",
                        "(",
                        "  cd examples/hello",
                        $"  stark build --target {targetTriple}",
                        $"  stark run --target {targetTriple}",
                        ")",
                        "```");
                Assert.Equal(expectedQuickStartMarkdown, quickStartMarkdown);
                var markedQuickStart = $"<!-- {quickStartMarker}:start -->\n{quickStartMarkdown}\n<!-- {quickStartMarker}:end -->";
                Assert.Contains(markedQuickStart, readme.Replace("\r\n", "\n"), StringComparison.Ordinal);
                Assert.Contains(markedQuickStart, install.Replace("\r\n", "\n"), StringComparison.Ordinal);

                var steps = commandRoot.GetProperty("steps").EnumerateArray().ToArray();
                Assert.Equal(["doctor", "check-hello", "build-hello", "run-hello"],
                    steps.Select(step => step.GetProperty("id").GetString()!).ToArray());
                Assert.Equal(["doctor", "--strict"],
                    steps[0].GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!).ToArray());
                Assert.Equal(["examples/hello.stark", "--check"],
                    steps[1].GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!).ToArray());
                Assert.Equal("examples/hello", steps[2].GetProperty("workingDirectory").GetString());
                Assert.Equal(["build", "--target", target.GetProperty("targetTriple").GetString()!],
                    steps[2].GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!).ToArray());
                Assert.Equal("Hello, World!", steps[3].GetProperty("expectedStdoutContains").GetString());
                var validationResult = await RunProcessAsync(
                    powershell,
                    [
                        "-NoProfile", "-NonInteractive", "-Command", contractValidationCommand,
                        contractHelper, stageRoot, target.GetProperty("targetTriple").GetString()!,
                    ],
                    repositoryRoot);
                Assert.True(
                    validationResult.ExitCode == 0,
                    $"Generated documentation contract validation failed for {targetId}.{Environment.NewLine}{validationResult.Stderr}");

                if (targetId == "macos-arm64")
                {
                    File.WriteAllText(
                        Path.Combine(stageRoot, "README.md"),
                        readme.Replace("stark doctor --strict", "stark doctor", StringComparison.Ordinal));
                    var driftResult = await RunProcessAsync(
                        powershell,
                        [
                            "-NoProfile", "-NonInteractive", "-Command", contractValidationCommand,
                            contractHelper, stageRoot, target.GetProperty("targetTriple").GetString()!,
                        ],
                        repositoryRoot);
                    Assert.NotEqual(0, driftResult.ExitCode);
                    Assert.Contains("drift", driftResult.Stderr, StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(Path.Combine(stageRoot, "README.md"), readme);

                    var semanticallyDifferentMarkdown = quickStartMarkdown.Replace(
                        "stark doctor --strict",
                        "stark doctor",
                        StringComparison.Ordinal);
                    Assert.NotEqual(quickStartMarkdown, semanticallyDifferentMarkdown);
                    var semanticallyDifferentBlock =
                        $"<!-- {quickStartMarker}:start -->\n{semanticallyDifferentMarkdown}\n<!-- {quickStartMarker}:end -->";
                    File.WriteAllText(
                        Path.Combine(stageRoot, "README.md"),
                        readme.Replace(markedQuickStart, semanticallyDifferentBlock, StringComparison.Ordinal));
                    File.WriteAllText(
                        Path.Combine(stageRoot, "INSTALL.md"),
                        install.Replace(markedQuickStart, semanticallyDifferentBlock, StringComparison.Ordinal));
                    var commandContractPath = Path.Combine(stageRoot, "release-commands.json");
                    var commandContractText = File.ReadAllText(commandContractPath);
                    File.WriteAllText(
                        commandContractPath,
                        commandContractText.Replace(
                            "stark doctor --strict",
                            "stark doctor",
                            StringComparison.Ordinal));
                    var semanticDriftResult = await RunProcessAsync(
                        powershell,
                        [
                            "-NoProfile", "-NonInteractive", "-Command", contractValidationCommand,
                            contractHelper, stageRoot, target.GetProperty("targetTriple").GetString()!,
                        ],
                        repositoryRoot);
                    Assert.NotEqual(0, semanticDriftResult.ExitCode);
                    Assert.Contains("canonical rendering", semanticDriftResult.Stderr, StringComparison.OrdinalIgnoreCase);
                    File.WriteAllText(Path.Combine(stageRoot, "README.md"), readme);
                    File.WriteAllText(Path.Combine(stageRoot, "INSTALL.md"), install);
                    File.WriteAllText(commandContractPath, commandContractText);
                }
                foreach (var expected in new[]
                {
                    targetId,
                    target.GetProperty("runtimeIdentifier").GetString()!,
                    target.GetProperty("targetTriple").GetString()!,
                    target.GetProperty("minimumOs").GetString()!,
                    target.GetProperty("hostPrerequisite").GetString()!,
                    "stark doctor --strict",
                    "examples/hello.stark",
                    "release-commands.json",
                    "--check",
                    "release-files.sha256",
                    "no `pkg-config`",
                    "Verify every official Vendor family",
                    "Vendor.Raylib",
                    "Vendor.Raymath",
                    "Vendor.Rlgl",
                    "Vendor.GLFW",
                    "Vendor.SDL3",
                    "Vendor.STB.Image",
                    "Vendor.Miniaudio",
                    "Vendor.Cgltf",
                    "Vendor.SQLite",
                    "pre-1.0 software",
                    "https://github.com/AlexanderBaggett/Stark/security/policy",
                })
                {
                    Assert.Contains(expected, readme, StringComparison.Ordinal);
                }
                Assert.Contains("Extraction-only use", install, StringComparison.Ordinal);
                Assert.Contains("Official Vendor packages", install, StringComparison.Ordinal);
                Assert.Contains($"self-contained .NET {dotnetRuntimeVersion} runtime", readme, StringComparison.Ordinal);
                if (!string.Equals(dotnetSdkVersion, dotnetRuntimeVersion, StringComparison.Ordinal))
                {
                    Assert.DoesNotContain($"self-contained .NET {dotnetSdkVersion} runtime", readme, StringComparison.Ordinal);
                }
                Assert.DoesNotMatch("__[A-Z0-9_]+__", readme);
                Assert.DoesNotMatch("__[A-Z0-9_]+__", install);

                if (target.GetProperty("operatingSystem").GetString() == "windows")
                {
                    Assert.Contains(@".\install.ps1 -DryRun -NoPath -NonInteractive", readme, StringComparison.Ordinal);
                    Assert.Contains("-Destination", install, StringComparison.Ordinal);
                    Assert.Contains("-ArchiveSha256", install, StringComparison.Ordinal);
                    Assert.Contains("-Repair", install, StringComparison.Ordinal);
                    Assert.Contains("Edit environment variables for your account", readme, StringComparison.Ordinal);
                    Assert.Contains("$starkBin", readme, StringComparison.Ordinal);
                    Assert.Contains("where.exe stark", readme, StringComparison.Ordinal);
                    Assert.Contains(@"stark .\VendorRaylibSafeApis.stark --emit-exe", readme, StringComparison.Ordinal);
                    Assert.Contains("foreach ($project in $projects)", readme, StringComparison.Ordinal);
                    Assert.Contains("stark run --target", readme, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Contains("./install.sh --dry-run --no-path --non-interactive", readme, StringComparison.Ordinal);
                    Assert.Contains("--prefix", install, StringComparison.Ordinal);
                    Assert.Contains("--archive-sha256", install, StringComparison.Ordinal);
                    Assert.Contains("--repair", install, StringComparison.Ordinal);
                    Assert.Contains("Bash or Zsh profile", readme, StringComparison.Ordinal);
                    Assert.Contains("fish_add_path --universal", readme, StringComparison.Ordinal);
                    Assert.Contains("command -v stark", readme, StringComparison.Ordinal);
                    Assert.Contains("stark VendorRaylibSafeApis.stark --emit-exe", readme, StringComparison.Ordinal);
                    Assert.Contains("for project in glfw sdl3 stb-image miniaudio cgltf sqlite", readme, StringComparison.Ordinal);
                    Assert.Contains("stark run --target", readme, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void ReleaseDocumentationGenerationConsumesManifestsBeforeContentChecksums()
    {
        var repositoryRoot = FindRepositoryRoot();
        var generator = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "generate-release-docs.ps1"));
        var packageScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));

        Assert.Contains("eng/release/targets.json", generator.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("eng/release/dependencies.json", generator.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("eng/release/archive-content.json", generator.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("runtimeIdentifier", generator, StringComparison.Ordinal);
        Assert.Contains("targetTriple", generator, StringComparison.Ordinal);
        Assert.Contains("minimumOs", generator, StringComparison.Ordinal);
        Assert.Contains("hostPrerequisite", generator, StringComparison.Ordinal);
        Assert.Contains("No installer or Stark environment variable is required", generator, StringComparison.Ordinal);
        Assert.Contains("no `pkg-config`", generator, StringComparison.Ordinal);
        Assert.Contains("release-commands.json", generator, StringComparison.Ordinal);
        Assert.Contains("release-command-contract", generator, StringComparison.Ordinal);
        Assert.Contains("New-ReleaseDocumentationQuickStartSteps", generator, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-ReleaseDocumentationQuickStartMarkdown", generator, StringComparison.Ordinal);
        Assert.DoesNotContain("$pathCommands", generator, StringComparison.Ordinal);

        var generateIndex = packageScript.IndexOf("generate-release-docs.ps1", StringComparison.Ordinal);
        var releaseJsonIndex = packageScript.IndexOf("Write-ReleaseJson `", StringComparison.Ordinal);
        var checksumsIndex = packageScript.IndexOf("Write-ReleaseFileChecksums `", StringComparison.Ordinal);
        Assert.True(generateIndex >= 0 && generateIndex < releaseJsonIndex && releaseJsonIndex < checksumsIndex);
        Assert.DoesNotContain("Write-InstallDocument", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Copy-OptionalFile -Source (Join-Path $repositoryRoot \"README.md\")",
            packageScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallSmokeWorkflowExercisesArchiveAndRealInstallerLifecycle()
    {
        var repositoryRoot = FindRepositoryRoot();
        var lifecycleScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-release-install.ps1"));
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "release.yml"));

        Assert.Contains("stark-install-smoke-", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--prefix", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--no-path", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--non-interactive", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("-Destination", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("-NoPath", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("-NonInteractive", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains(".stark-install-receipt", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("doctor", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("Invoke-ReleaseDocumentationCommandContract", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("documentationContractSucceeded", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--strict", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--format", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--check", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--emit-exe", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("Vendor.SQLite", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("--no-stark-path", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("executableSucceeded", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("vendorSucceeded", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("sourceEnvironmentOverridesCleared", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("uninstallSucceeded", lifecycleScript, StringComparison.Ordinal);
        Assert.Contains("Uninstaller left receipt-owned installation state", lifecycleScript, StringComparison.Ordinal);
        Assert.DoesNotContain("xcode-select --install", lifecycleScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apt install", lifecycleScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("winget install", lifecycleScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest", lifecycleScript, StringComparison.OrdinalIgnoreCase);

        var jobIndex = workflow.IndexOf("install-smoke:", StringComparison.Ordinal);
        var archiveSmokeIndex = workflow.IndexOf("Re-run archive smoke on downloaded candidate", jobIndex, StringComparison.Ordinal);
        var lifecycleIndex = workflow.IndexOf("Qualify archive-local installer lifecycle", jobIndex, StringComparison.Ordinal);
        var uploadIndex = workflow.IndexOf("Upload install-smoke diagnostics", jobIndex, StringComparison.Ordinal);
        Assert.True(jobIndex >= 0 && archiveSmokeIndex > jobIndex && lifecycleIndex > archiveSmokeIndex && uploadIndex > lifecycleIndex);
        Assert.Contains("./scripts/smoke-release-archive.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/smoke-release-install.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-ReportPath \"artifacts/install-smoke/installer-lifecycle-${{ matrix.asset_suffix }}.json\"", workflow, StringComparison.Ordinal);
        Assert.Contains("-DiagnosticsDir \"artifacts/install-smoke/installer-lifecycle-${{ matrix.asset_suffix }}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("if: ${{ always() }}", workflow[uploadIndex..], StringComparison.Ordinal);
        Assert.Contains("name: install-smoke-${{ matrix.asset_suffix }}", workflow[uploadIndex..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallSmokeScriptCompletesUnixArchiveLifecycleWhenPowerShellIsAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var powershell = await FindPowerShellAsync(repositoryRoot);
        if (powershell is null)
        {
            return;
        }

        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        var architectureName = architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architectureName is null)
        {
            return;
        }

        var osName = OperatingSystem.IsMacOS() ? "macos" : "linux";
        var targetTriple = (osName, architectureName) switch
        {
            ("macos", "x64") => "x86_64-apple-macosx11.0.0",
            ("macos", "arm64") => "arm64-apple-macosx11.0.0",
            ("linux", "x64") => "x86_64-unknown-linux-gnu",
            ("linux", "arm64") => "aarch64-unknown-linux-gnu",
            _ => throw new InvalidOperationException(),
        };
        var assetSuffix = $"{osName}-{architectureName}";
        var rootName = $"stark-v1.2.3-{assetSuffix}";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"stark-install-smoke-contract-{Guid.NewGuid():N}");
        var archiveRoot = Path.Combine(temporaryRoot, rootName);
        var archivePath = Path.Combine(temporaryRoot, $"{rootName}.tar.gz");
        var reportPath = Path.Combine(temporaryRoot, "report.json");
        var diagnosticsPath = Path.Combine(temporaryRoot, "diagnostics");
        Directory.CreateDirectory(Path.Combine(archiveRoot, "bin"));

        try
        {
            File.Copy(
                Path.Combine(repositoryRoot, "scripts", "release-installers", "install.sh"),
                Path.Combine(archiveRoot, "install.sh"));
            File.Copy(
                Path.Combine(repositoryRoot, "scripts", "release-installers", "uninstall.sh"),
                Path.Combine(archiveRoot, "uninstall.sh"));
            File.WriteAllText(
                Path.Combine(archiveRoot, "release.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    starkVersion = "v1.2.3",
                    assetSuffix,
                    defaultTargetTriple = targetTriple,
                },
                new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(archiveRoot, "sdk.json"), "{\"schemaVersion\":1}\n", new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(archiveRoot, "bin", "stark"),
                """
                #!/bin/sh
                sdk_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd -P)
                if [ "${1-}" = doctor ]; then
                    case " $* " in
                        *" --format json "*)
                            printf '{"schemaVersion":1,"status":"ok","compiler":{"path":"%s/bin/stark"},"sdk":{"status":"ok","root":"%s"}}\n' "$sdk_root" "$sdk_root"
                            ;;
                    esac
                    exit 0
                fi
                if [ "${1-}" = build ]; then
                    exit 0
                fi
                if [ "${1-}" = run ]; then
                    printf '%s\n' 'Hello, World!'
                    exit 0
                fi
                case " $* " in
                    *" --check "*) printf '%s\n' 'Check succeeded.'; exit 0 ;;
                    *" --emit-exe "*)
                        output=''
                        previous=''
                        for argument in "$@"; do
                            if [ "$previous" = '-o' ]; then output=$argument; fi
                            previous=$argument
                        done
                        if [ -z "$output" ]; then exit 65; fi
                        if grep -q 'Vendor.SQLite' "$1"; then
                            printf '%s\n' '#!/bin/sh' 'exit 0' > "$output"
                        else
                            printf '%s\n' '#!/bin/sh' "printf '%s\\n' 'installed SDK smoke'" 'exit 0' > "$output"
                        fi
                        chmod +x "$output"
                        exit 0
                        ;;
                esac
                exit 64
                """,
                new UTF8Encoding(false));

            Directory.CreateDirectory(Path.Combine(archiveRoot, "examples", "hello"));
            File.Copy(
                Path.Combine(repositoryRoot, "examples", "hello.stark"),
                Path.Combine(archiveRoot, "examples", "hello.stark"));
            File.Copy(
                Path.Combine(repositoryRoot, "examples", "hello", "Stark.toml"),
                Path.Combine(archiveRoot, "examples", "hello", "Stark.toml"));

            using var targetsDocument = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(repositoryRoot, "eng", "release", "targets.json")));
            using var dependenciesDocument = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(repositoryRoot, "eng", "release", "dependencies.json")));
            var target = targetsDocument.RootElement.GetProperty("targets").EnumerateArray()
                .Single(value => value.GetProperty("id").GetString() == assetSuffix);
            var llvmVersion = dependenciesDocument.RootElement.GetProperty("dependencies").EnumerateArray()
                .Single(value => value.GetProperty("id").GetString() == "llvm-22.1.8")
                .GetProperty("version").GetString()!;
            var documentationResult = await RunProcessAsync(
                powershell,
                [
                    "-NoProfile", "-NonInteractive", "-File",
                    Path.Combine(repositoryRoot, "scripts", "generate-release-docs.ps1"),
                    "-StageRoot", archiveRoot,
                    "-Version", "v1.2.3",
                    "-AssetSuffix", assetSuffix,
                    "-PackagedRuntimeIdentifier", target.GetProperty("runtimeIdentifier").GetString()!,
                    "-PackagedTargetTriple", targetTriple,
                    "-PackagedArchiveKind", target.GetProperty("archiveKind").GetString()!,
                    "-PackagedLlvmVersion", llvmVersion,
                ],
                repositoryRoot);
            Assert.True(documentationResult.ExitCode == 0, documentationResult.Stderr);

            foreach (var script in new[] { "install.sh", "uninstall.sh", "bin/stark" })
            {
                File.SetUnixFileMode(
                    Path.Combine(archiveRoot, script),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            var checksumLines = Directory.EnumerateFiles(archiveRoot, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    Path = path,
                    Relative = Path.GetRelativePath(archiveRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                })
                .OrderBy(entry => entry.Relative, StringComparer.Ordinal)
                .Select(entry => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry.Path))).ToLowerInvariant()}  {entry.Relative}")
                .ToArray();
            File.WriteAllLines(Path.Combine(archiveRoot, "release-files.sha256"), checksumLines, Encoding.ASCII);

            var tarResult = await RunProcessAsync(
                "tar",
                ["-czf", archivePath, "-C", temporaryRoot, rootName],
                temporaryRoot);
            Assert.True(tarResult.ExitCode == 0, tarResult.Stderr);

            var smokeResult = await RunProcessAsync(
                powershell,
                [
                    "-NoProfile", "-NonInteractive", "-File",
                    Path.Combine(repositoryRoot, "scripts", "smoke-release-install.ps1"),
                    "-ArchivePath", archivePath,
                    "-TargetTriple", targetTriple,
                    "-ReportPath", reportPath,
                    "-DiagnosticsDir", diagnosticsPath,
                ],
                temporaryRoot);
            Assert.True(
                smokeResult.ExitCode == 0,
                $"Installer smoke fixture failed.{Environment.NewLine}{smokeResult.Stdout}{Environment.NewLine}{smokeResult.Stderr}");

            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal("passed", report.RootElement.GetProperty("status").GetString());
            Assert.True(report.RootElement.GetProperty("installSucceeded").GetBoolean());
            Assert.True(report.RootElement.GetProperty("doctorSucceeded").GetBoolean());
            Assert.True(report.RootElement.GetProperty("documentationContractSucceeded").GetBoolean());
            Assert.True(report.RootElement.GetProperty("checkSucceeded").GetBoolean());
            Assert.True(report.RootElement.GetProperty("executableSucceeded").GetBoolean());
            Assert.True(report.RootElement.GetProperty("vendorSucceeded").GetBoolean());
            Assert.True(report.RootElement.GetProperty("uninstallSucceeded").GetBoolean());
            Assert.False(Directory.Exists(report.RootElement.GetProperty("installPrefix").GetString()));
            Assert.True(File.Exists(Path.Combine(diagnosticsPath, "doctor.json")));
            Assert.True(File.Exists(Path.Combine(diagnosticsPath, "check.stdout.log")));
            Assert.True(File.Exists(Path.Combine(diagnosticsPath, "executable-run.stdout.log")));
            Assert.True(File.Exists(Path.Combine(diagnosticsPath, "vendor-sqlite-run.stdout.log")));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void InstallersAreOfflineAndReceiptOwnedByContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var unixInstall = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release-installers", "install.sh"));
        var unixUninstall = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release-installers", "uninstall.sh"));
        var windowsInstall = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release-installers", "install.ps1"));
        var windowsUninstall = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "release-installers", "uninstall.ps1"));
        var stageScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "stage-release-installers.ps1"));
        var packageScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-release.ps1"));

        Assert.Contains("--no-path", unixInstall, StringComparison.Ordinal);
        Assert.Contains("--dry-run", unixInstall, StringComparison.Ordinal);
        Assert.Contains("stark-install-receipt-v1", unixInstall, StringComparison.Ordinal);
        Assert.Contains("verify_release_file_checksums", unixInstall, StringComparison.Ordinal);
        Assert.Contains("release-files.sha256", unixInstall, StringComparison.Ordinal);
        Assert.Contains("installed-file inventory", unixUninstall, StringComparison.Ordinal);
        Assert.Contains("[EnvironmentVariableTarget]::User", windowsInstall, StringComparison.Ordinal);
        Assert.Contains("OSArchitecture", windowsInstall, StringComparison.Ordinal);
        Assert.Contains("Assert-ReleaseFileChecksums", windowsInstall, StringComparison.Ordinal);
        Assert.Contains("release-files.sha256", windowsInstall, StringComparison.Ordinal);
        Assert.Contains("InstalledFiles", windowsUninstall, StringComparison.Ordinal);
        Assert.Contains("windows-arm64", stageScript, StringComparison.Ordinal);
        Assert.Contains("stage-release-installers.ps1", packageScript, StringComparison.Ordinal);
        Assert.Contains("-AssetSuffix $AssetSuffix", packageScript, StringComparison.Ordinal);

        foreach (var script in new[] { unixInstall, windowsInstall })
        {
            Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Start-BitsTransfer", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Start-Process winget", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Start-Process choco", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string?> FindPowerShellAsync(string workingDirectory)
    {
        foreach (var candidate in new[] { "pwsh", "powershell.exe" })
        {
            try
            {
                var result = await RunProcessAsync(candidate, ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], workingDirectory);
                if (result.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Win32Exception)
            {
                // Try the next supported PowerShell host.
            }
        }

        return null;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (OperatingSystem.IsMacOS())
        {
            // BSD tar otherwise synthesizes AppleDouble ._* members from host
            // metadata, which makes an otherwise portable fixture contain a
            // second top-level path.
            startInfo.Environment["COPYFILE_DISABLE"] = "1";
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Stark repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
