#requires -Version 7.0

param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $Commit,

    [Parameter(Mandatory = $true)]
    [string] $Ref,

    [string] $Targets = "all",

    [Alias("Phases")]
    [ValidateSet("Plan", "Quality", "Acquire", "Build", "Package", "Validate", "Smoke", "All")]
    [string[]] $Phase = @("Plan"),

    [switch] $DryRun,

    [switch] $PublishingCandidate,

    [string] $CacheBase = "artifacts/local-release/cache",

    [string] $OutputBase = "artifacts/local-release/output",

    [string] $CMakePath = "",

    [string] $NinjaPath = "",

    [string] $ReleaseToolsPath = "",

    [string] $DotNetPath = "dotnet",

    [string] $PlanOutput = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$fullPhaseOrder = @("Quality", "Acquire", "Build", "Package", "Validate", "Smoke")
function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Get-RequiredJsonProperty {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [string] $Label = "JSON object"
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Label is missing required property '$Name'."
    }
    return $property.Value
}

function Get-ArrayValues {
    param([object] $Value)

    if ($null -eq $Value) {
        return @()
    }
    if ($Value -is [System.Array]) {
        return @($Value)
    }
    return @($Value)
}

function Get-OrdinalSortedStrings {
    param([object[]] $Values = @())

    $valuesByCase = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sorted = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in @($Values)) {
        $text = [string]$value
        if (-not $valuesByCase.Add($text) -or -not $sorted.Add($text)) {
            throw "Duplicate or case-colliding value '$text'."
        }
    }
    return @($sorted)
}

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][string] $Value)

    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Value)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Write-DeterministicJson {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Value
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $json = ($Value | ConvertTo-Json -Depth 30).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string] $FileName,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory = $repositoryRoot,
        [System.Collections.IDictionary] $Environment = $null,
        [int[]] $AllowedExitCodes = @(0)
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    if ($null -ne $Environment) {
        foreach ($entry in $Environment.GetEnumerator()) {
            $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
        }
    }
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start '$FileName'."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($AllowedExitCodes -notcontains $process.ExitCode) {
        $display = "$FileName $($Arguments -join ' ')".Trim()
        throw "Command '$display' exited with code $($process.ExitCode): $stderr"
    }

    return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

function New-ReleaseCommand {
    param(
        [Parameter(Mandatory = $true)][string] $PhaseName,
        [Parameter(Mandatory = $true)][string] $TargetId,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $Executable,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [System.Collections.IDictionary] $Environment = $null
    )

    return [ordered]@{
        phase = $PhaseName
        targetId = $TargetId
        label = $Label
        executable = $Executable
        arguments = [object[]]$Arguments
        environment = $Environment
        workingDirectory = $repositoryRoot
    }
}

function Invoke-ReleaseCommand {
    param([Parameter(Mandatory = $true)][object] $Command)

    $displayArguments = @($Command.arguments | ForEach-Object {
        $argument = [string]$_
        if ($argument.Contains(' ', [StringComparison]::Ordinal)) { '"' + $argument + '"' } else { $argument }
    })
    Write-Host ">> [$($Command.phase)/$($Command.targetId)] $($Command.label)"
    Write-Host "   $($Command.executable) $($displayArguments -join ' ')"
    $result = Invoke-CapturedProcess `
        -FileName ([string]$Command.executable) `
        -Arguments ([string[]]@($Command.arguments)) `
        -WorkingDirectory ([string]$Command.workingDirectory) `
        -Environment $Command.environment
    if (-not [string]::IsNullOrWhiteSpace($result.Stdout)) {
        Write-Host $result.Stdout.TrimEnd()
    }
    if (-not [string]::IsNullOrWhiteSpace($result.Stderr)) {
        Write-Host $result.Stderr.TrimEnd()
    }
}

function Get-HostIdentity {
    $operatingSystem = if ($IsWindows) { "windows" } elseif ($IsLinux) { "linux" } elseif ($IsMacOS) { "macos" } else { "unknown" }
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    return "$operatingSystem-$architecture"
}

if ($Version -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$') {
    throw "Version '$Version' is not a portable release version."
}
$Commit = $Commit.Trim().ToLowerInvariant()
if ($Commit -cnotmatch '^[0-9a-f]{40}$') {
    throw "Commit must be an explicit full 40-character hexadecimal SHA."
}
if ([string]::IsNullOrWhiteSpace($Ref)) {
    throw "Ref must explicitly identify the Git ref or full commit used for this plan."
}
if ($Phase.Count -eq 0) {
    throw "At least one release phase is required."
}
$requestedPhases = @(Get-OrdinalSortedStrings -Values $Phase)
if ($requestedPhases -contains "All" -and $requestedPhases.Count -ne 1) {
    throw "Phase 'All' cannot be combined with another phase."
}
if ($requestedPhases -contains "Plan" -and $requestedPhases.Count -ne 1) {
    throw "Phase 'Plan' cannot be combined with execution phases. Use -DryRun to inspect execution phases."
}
$planOnly = $requestedPhases -contains "Plan"
$selectedPhases = if ($requestedPhases -contains "All" -or $planOnly) {
    @($fullPhaseOrder)
} else {
    @($fullPhaseOrder | Where-Object { $requestedPhases -contains $_ })
}
if ($PublishingCandidate -and (($selectedPhases -join "`n") -cne ($fullPhaseOrder -join "`n"))) {
    throw "A publishing-equivalent local candidate must select the complete Quality, Acquire, Build, Package, Validate, and Smoke pipeline."
}

$refResult = Invoke-CapturedProcess -FileName "git" -Arguments @("rev-parse", "--verify", "$Ref^{commit}")
$resolvedRefCommit = $refResult.Stdout.Trim().ToLowerInvariant()
if ($resolvedRefCommit -cne $Commit) {
    throw "Ref '$Ref' resolves to '$resolvedRefCommit', not requested commit '$Commit'."
}
$headCommit = (Invoke-CapturedProcess -FileName "git" -Arguments @("rev-parse", "HEAD")).Stdout.Trim().ToLowerInvariant()
if ($headCommit -cne $Commit) {
    throw "Current checkout HEAD '$headCommit' does not match requested commit '$Commit'. Check out the immutable commit first."
}
$workingTreeStatus = (Invoke-CapturedProcess -FileName "git" -Arguments @("status", "--porcelain=v1", "--untracked-files=all")).Stdout
$workingTreeDirty = -not [string]::IsNullOrWhiteSpace($workingTreeStatus)
$willExecute = -not $DryRun -and -not $planOnly
if ($willExecute -and $workingTreeDirty) {
    throw "Release execution requires a clean checkout of '$Commit'. Plan/dry-run is allowed on a dirty tree, but no release phase may execute."
}
$dotnetCommand = Get-Command $DotNetPath -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
$dotnetExecutable = $dotnetCommand.Source
$releaseToolsAssembly = @(& (Join-Path $PSScriptRoot "resolve-release-tools.ps1") `
    -RepositoryRoot $repositoryRoot `
    -DotNetPath $dotnetExecutable `
    -ReleaseToolsPath $ReleaseToolsPath) | Select-Object -Last 1
$releaseToolsProject = Join-Path $repositoryRoot "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj"
$releaseToolsIdentity = [ordered]@{
    implementation = "Stark.ReleaseTools"
    targetFramework = "net10.0"
    assembly = $releaseToolsAssembly
    assemblyBytes = [int64](Get-Item -LiteralPath $releaseToolsAssembly).Length
    assemblySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseToolsAssembly).Hash.ToLowerInvariant()
    projectSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseToolsProject).Hash.ToLowerInvariant()
}
$powerShellPath = (Get-Process -Id $PID).Path
if ([string]::IsNullOrWhiteSpace($powerShellPath)) {
    throw "Could not resolve the current PowerShell executable."
}

$basePlanPath = [System.IO.Path]::GetTempFileName()
try {
    $prerelease = if ($Version.Contains('-', [StringComparison]::Ordinal)) { "true" } else { "false" }
    $prepareArguments = @(
        $releaseToolsAssembly,
        "prepare-release",
        "--event-name", "workflow_dispatch",
        "--resolved-commit", $Commit,
        "--input-version", $Version,
        "--input-ref", $Ref,
        "--input-commit", $Commit,
        "--input-targets", $Targets,
        "--input-publish", $(if ($PublishingCandidate) { "true" } else { "false" }),
        "--input-draft", "true",
        "--input-prerelease", $prerelease,
        "--require-release-tool",
        "--plan-output", $basePlanPath
    )
    [void](Invoke-CapturedProcess -FileName $dotnetExecutable -Arguments $prepareArguments)
    $basePlanSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $basePlanPath).Hash.ToLowerInvariant()
    $basePlan = Get-Content -LiteralPath $basePlanPath -Raw | ConvertFrom-Json -Depth 30
} finally {
    Remove-Item -LiteralPath $basePlanPath -Force -ErrorAction SilentlyContinue
}

$matrixEntries = @(Get-ArrayValues -Value (Get-RequiredJsonProperty -Object (Get-RequiredJsonProperty -Object $basePlan -Name "matrix" -Label "base release plan") -Name "include" -Label "base release matrix"))
$vendorDocument = Get-Content -LiteralPath (Join-Path $repositoryRoot "eng/release/vendor-packages.json") -Raw | ConvertFrom-Json

$sdlPackage = @(Get-ArrayValues -Value (Get-RequiredJsonProperty -Object $vendorDocument -Name "packages" -Label "vendor-packages.json")) |
    Where-Object { [string]$_.id -ceq "Vendor.SDL3" } |
    Select-Object -First 1
$sdlRecipe = if ($null -eq $sdlPackage) { "" } else { [string]$sdlPackage.buildRecipe }
$sdlUsesHermeticContributor = $sdlRecipe.Replace('\', '/').EndsWith(
    "scripts/prepare-sdl3-vendor-release-input.ps1",
    [StringComparison]::Ordinal)
$requiresHermeticVendorBuildTools = $planOnly -or $selectedPhases -contains "Build"
$requiresLlvmSourceBuildTools = $false
if ($planOnly -or $selectedPhases -contains "Acquire") {
    foreach ($entry in $matrixEntries) {
        $llvmManifestPath = Resolve-RepositoryPath -Path ([string]$entry.llvm_manifest)
        $llvmDocument = Get-Content -LiteralPath $llvmManifestPath -Raw | ConvertFrom-Json
        $platformProperty = $llvmDocument.platforms.PSObject.Properties[[string]$entry.asset_suffix]
        if ($null -eq $platformProperty) {
            throw "LLVM acquisition manifest '$llvmManifestPath' has no platform '$([string]$entry.asset_suffix)'."
        }
        if ($null -ne $platformProperty.Value.PSObject.Properties["sourceBuild"]) {
            $requiresLlvmSourceBuildTools = $true
        }
    }
}
if ([string]::IsNullOrWhiteSpace($CMakePath) -xor [string]::IsNullOrWhiteSpace($NinjaPath)) {
    throw "CMakePath and NinjaPath must be supplied together; release builds never mix explicit and ambient build tools."
}
if ((($requiresHermeticVendorBuildTools -and $sdlUsesHermeticContributor) -or
     $requiresLlvmSourceBuildTools) -and
    ([string]::IsNullOrWhiteSpace($CMakePath) -or [string]::IsNullOrWhiteSpace($NinjaPath))) {
    throw "The selected release phases require explicit -CMakePath and -NinjaPath; ambient build-tool discovery is forbidden."
}
$resolvedCMakePath = if ([string]::IsNullOrWhiteSpace($CMakePath)) { $null } else { Resolve-RepositoryPath -Path $CMakePath }
$resolvedNinjaPath = if ([string]::IsNullOrWhiteSpace($NinjaPath)) { $null } else { Resolve-RepositoryPath -Path $NinjaPath }
$externalBuildTools = @()
foreach ($tool in @(
    [pscustomobject]@{ Name = "cmake"; Path = $resolvedCMakePath },
    [pscustomobject]@{ Name = "ninja"; Path = $resolvedNinjaPath }
)) {
    if ($null -eq $tool.Path) {
        continue
    }
    if (-not (Test-Path -LiteralPath $tool.Path -PathType Leaf)) {
        throw "Explicit $($tool.Name) executable '$($tool.Path)' does not exist."
    }
    $externalBuildTools += [ordered]@{
        name = [string]$tool.Name
        path = [string]$tool.Path
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $tool.Path).Hash.ToLowerInvariant()
    }
}

$configurationIdentityOutput = @(& (Join-Path $PSScriptRoot "get-release-configuration-identity.ps1") -Root $repositoryRoot)
$configurationIdentity = ($configurationIdentityOutput -join "`n") | ConvertFrom-Json
if ([int](Get-RequiredJsonProperty -Object $configurationIdentity -Name "schemaVersion" -Label "release configuration identity") -ne 1 -or
    [string](Get-RequiredJsonProperty -Object $configurationIdentity -Name "identityKind" -Label "release configuration identity") -cne "stark-release-configuration" -or
    [string](Get-RequiredJsonProperty -Object $configurationIdentity -Name "algorithm" -Label "release configuration identity") -cne "sha256-ordinal-path-size-content-v1") {
    throw "Release configuration identity helper returned an unsupported contract."
}
$configurationDigest = [string](Get-RequiredJsonProperty -Object $configurationIdentity -Name "sha256" -Label "release configuration identity")
$configurationFiles = @(Get-ArrayValues -Value (
    Get-RequiredJsonProperty -Object $configurationIdentity -Name "files" -Label "release configuration identity"))
if ($configurationDigest -cnotmatch '^[0-9a-f]{64}$' -or $configurationFiles.Count -eq 0) {
    throw "Release configuration identity helper returned an invalid or empty identity."
}
$selectedTargetIds = @((Get-ArrayValues -Value (Get-RequiredJsonProperty -Object $basePlan -Name "targetIds" -Label "base release plan")) | ForEach-Object { [string]$_ })
$outputIdentity = @(
    "schema=1",
    "version=$Version",
    "commit=$Commit",
    "ref=$Ref",
    "targets=$($selectedTargetIds -join ',')",
    "publishingCandidate=$($PublishingCandidate.IsPresent.ToString().ToLowerInvariant())",
    "configuration=$configurationDigest",
    "releaseToolsProject=$([string]$releaseToolsIdentity.projectSha256)",
    "releaseToolsAssembly=$([string]$releaseToolsIdentity.assemblySha256)",
    "externalBuildTools=$(@($externalBuildTools | ForEach-Object { "$($_.name):$($_.sha256)" }) -join ',')"
) -join "`n"
$outputDigest = Get-StringSha256 -Value ($outputIdentity + "`n")
$cacheBasePath = Resolve-RepositoryPath -Path $CacheBase
$outputBasePath = Resolve-RepositoryPath -Path $OutputBase
$cacheRoot = Join-Path $cacheBasePath $configurationDigest
$outputRoot = Join-Path $outputBasePath $outputDigest
$releaseRoot = Join-Path $outputRoot "release"
$diagnosticsRoot = Join-Path $outputRoot "diagnostics"
$dotnetEnvironment = [ordered]@{
    DOTNET_CLI_HOME = (Join-Path $cacheRoot "dotnet-cli-home")
    DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    DOTNET_NOLOGO = "1"
    NUGET_HTTP_CACHE_PATH = (Join-Path $cacheRoot "nuget-http")
    NUGET_PACKAGES = (Join-Path $cacheRoot "nuget-packages")
}

$dotnetVersionSet = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$dotnetRuntimeVersionSet = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $matrixEntries) {
    [void]$dotnetVersionSet.Add([string]$entry.dotnet_version)
    [void]$dotnetRuntimeVersionSet.Add([string]$entry.dotnet_runtime_version)
}
$requiredDotnetVersions = @($dotnetVersionSet)
$requiredDotnetRuntimeVersions = @($dotnetRuntimeVersionSet)
if ($requiredDotnetVersions.Count -ne 1 -or $requiredDotnetRuntimeVersions.Count -ne 1) {
    throw "Selected release matrix must use one exact managed SDK/runtime version pair."
}
$requiredDotnetVersion = $requiredDotnetVersions[0]
$requiredDotnetRuntimeVersion = $requiredDotnetRuntimeVersions[0]
$hostIdentity = Get-HostIdentity
if ($willExecute) {
    $executesTargetPhases = @($selectedPhases | Where-Object { $_ -cne "Quality" }).Count -gt 0
    if ($executesTargetPhases) {
        foreach ($entry in $matrixEntries) {
            $requiredHost = "$([string]$entry.target_id)"
            if ($requiredHost -cne $hostIdentity) {
                throw "Target '$requiredHost' cannot execute on local host '$hostIdentity'. Run that target on its matching 64-bit host; dry-run may plan cross-host matrices."
            }
        }
    }
    if ($selectedPhases -contains "Quality" -or $selectedPhases -contains "Build") {
        $actualDotnetVersion = (Invoke-CapturedProcess -FileName $dotnetExecutable -Arguments @("--version")).Stdout.Trim()
        if ($actualDotnetVersion -cne $requiredDotnetVersion) {
            throw "Quality and Build phases require .NET SDK '$requiredDotnetVersion' from the release matrix; local dotnet is '$actualDotnetVersion'."
        }
    }
}

$commands = @()
$commands += New-ReleaseCommand `
    -PhaseName "Quality" `
    -TargetId "repository" `
    -Label "Run mandatory repository quality gate" `
    -Executable $powerShellPath `
    -Arguments @(
        "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRoot "scripts/run-release-quality-gate.ps1"),
        "-RepositoryRoot", $repositoryRoot,
        "-OutputDir", (Join-Path $diagnosticsRoot "quality"),
        "-DotNetPath", $dotnetExecutable,
        "-ReleaseToolsPath", $releaseToolsAssembly,
        "-BashPath", "bash"
    ) `
    -Environment $dotnetEnvironment
foreach ($entry in $matrixEntries) {
    $targetId = [string]$entry.target_id
    $toolchainRoot = Join-Path $outputRoot ("toolchain/" + $targetId)
    $llvmArguments = @(
        "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRoot "scripts/acquire-llvm-toolchain.ps1"),
        "-AssetSuffix", [string]$entry.asset_suffix,
        "-ManifestPath", (Join-Path $repositoryRoot ([string]$entry.llvm_manifest)),
        "-OutputDir", $toolchainRoot,
        "-CacheDir", (Join-Path $cacheRoot ("llvm/" + $targetId))
    )
    if ($null -ne $resolvedCMakePath) {
        $llvmArguments += @("-CMakePath", $resolvedCMakePath, "-NinjaPath", $resolvedNinjaPath)
    }
    $commands += New-ReleaseCommand -PhaseName "Acquire" -TargetId $targetId -Label "Acquire compiler-private LLVM backend" -Executable $powerShellPath -Arguments $llvmArguments
}

foreach ($entry in $matrixEntries) {
    $targetId = [string]$entry.target_id
    $publishRoot = Join-Path $outputRoot ("publish/" + $targetId)
    $toolchainRoot = Join-Path $outputRoot ("toolchain/" + $targetId)
    $stdlibRoot = Join-Path $outputRoot ("stdlib/" + $targetId)
    $vendorRoot = Join-Path $outputRoot ("vendor/" + $targetId)
    $managedRestoreReport = Join-Path $diagnosticsRoot ("managed-dependencies-" + $targetId + ".json")
    $managedLicenseRoot = Join-Path $outputRoot ("managed-licenses/" + $targetId)
    $commands += New-ReleaseCommand `
        -PhaseName "Build" `
        -TargetId $targetId `
        -Label "Restore exact managed dependency graph" `
        -Executable $dotnetExecutable `
        -Arguments @(
            "restore", (Join-Path $repositoryRoot "src/compiler.csproj"),
            "-r", [string]$entry.rid,
            "--configfile", (Join-Path $repositoryRoot ([string]$entry.nuget_config)),
            "--use-lock-file", "--locked-mode",
            "--lock-file-path", (Join-Path $repositoryRoot ([string]$entry.nuget_lock_file)),
            "-p:RuntimeFrameworkVersion=$([string]$entry.dotnet_runtime_version)"
        ) `
        -Environment $dotnetEnvironment
    $commands += New-ReleaseCommand `
        -PhaseName "Build" `
        -TargetId $targetId `
        -Label "Validate exact managed dependency graph" `
        -Executable $dotnetExecutable `
        -Arguments @(
            $releaseToolsAssembly,
            "validate-managed-restore",
            "--root", $repositoryRoot,
            "--rid", [string]$entry.rid,
            "--assets", (Join-Path $repositoryRoot "src/obj/project.assets.json"),
            "--dotnet-version", $requiredDotnetVersion,
            "--restore-only",
            "--output", $managedRestoreReport
        ) `
        -Environment $dotnetEnvironment
    $commands += New-ReleaseCommand `
        -PhaseName "Build" `
        -TargetId $targetId `
        -Label "Publish self-contained Stage0 compiler" `
        -Executable $dotnetExecutable `
        -Arguments @(
            "publish", (Join-Path $repositoryRoot "src/compiler.csproj"),
            "-c", "Release", "-r", [string]$entry.rid,
            "--self-contained", "true",
            "--no-restore",
            "-p:RuntimeFrameworkVersion=$([string]$entry.dotnet_runtime_version)",
            "-p:PublishSingleFile=false",
            "-p:InformationalVersion=$Version",
            "-p:IncludeSourceRevisionInInformationalVersion=false",
            "-p:WriteCompilerLauncherToRepoRoot=false",
            "-o", $publishRoot
        ) `
        -Environment $dotnetEnvironment
    $commands += New-ReleaseCommand `
        -PhaseName "Build" `
        -TargetId $targetId `
        -Label "Prepare exact managed license inventory" `
        -Executable $dotnetExecutable `
        -Arguments @(
            $releaseToolsAssembly,
            "prepare-managed-licenses",
            "--root", $repositoryRoot,
            "--target-id", $targetId,
            "--assets", (Join-Path $repositoryRoot "src/obj/project.assets.json"),
            "--output-root", $managedLicenseRoot
        ) `
        -Environment $dotnetEnvironment
    $commands += New-ReleaseCommand `
        -PhaseName "Build" `
        -TargetId $targetId `
        -Label "Build release System package" `
        -Executable $dotnetExecutable `
        -Arguments @(
            "run", "--no-restore", "--project", (Join-Path $repositoryRoot "src/compiler.csproj"), "--",
            (Join-Path $repositoryRoot "stdlib/src/System.stark"),
            "--emit-lib", "--target", [string]$entry.target_triple,
            "--package-profile", "release", "--toolchain-dir", $toolchainRoot,
            "-o", (Join-Path $stdlibRoot ([string]$entry.stdlib_library))
        ) `
        -Environment $dotnetEnvironment
    $vendorArguments = @(
        "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRoot "scripts/prepare-vendor-release-input.ps1"),
        "-AssetSuffix", [string]$entry.asset_suffix,
        "-TargetTriple", [string]$entry.target_triple,
        "-OutputVendorRoot", $vendorRoot,
        "-StdlibPackageDir", $stdlibRoot,
        "-RaylibManifestPath", (Join-Path $repositoryRoot ([string]$entry.raylib_manifest)),
        "-ToolchainDir", $toolchainRoot,
        "-CacheDir", (Join-Path $cacheRoot ("vendor/" + $targetId))
    )
    if ($null -ne $resolvedCMakePath) {
        $vendorArguments += @("-CMakePath", $resolvedCMakePath, "-NinjaPath", $resolvedNinjaPath)
    }
    $commands += New-ReleaseCommand -PhaseName "Build" -TargetId $targetId -Label "Prepare official Vendor package images" -Executable $powerShellPath -Arguments $vendorArguments
}

foreach ($entry in $matrixEntries) {
    $targetId = [string]$entry.target_id
    $commands += New-ReleaseCommand -PhaseName "Package" -TargetId $targetId -Label "Package release archive" -Executable $powerShellPath -Arguments @(
        "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRoot "scripts/package-release.ps1"),
        "-Version", $Version,
        "-AssetSuffix", [string]$entry.asset_suffix,
        "-PublishDir", (Join-Path $outputRoot ("publish/" + $targetId)),
        "-StdlibPackageDir", (Join-Path $outputRoot ("stdlib/" + $targetId)),
        "-ManagedLicenseDir", (Join-Path $outputRoot ("managed-licenses/" + $targetId)),
        "-VendorRoot", (Join-Path $outputRoot ("vendor/" + $targetId)),
        "-ToolchainDir", (Join-Path $outputRoot ("toolchain/" + $targetId)),
        "-ReleaseToolsPath", $releaseToolsAssembly,
        "-DotNetPath", $dotnetExecutable,
        "-RuntimeIdentifier", [string]$entry.rid,
        "-TargetTriple", [string]$entry.target_triple,
        "-CommitSha", $Commit,
        "-BuildConfigurationSha256", $configurationDigest,
        "-BuildPlanSha256", $basePlanSha256,
        "-OutputDir", $releaseRoot,
        "-ArchiveKind", [string]$entry.archive_kind,
        "-LlvmVersion", [string]$entry.llvm_version
    )
}

foreach ($entry in $matrixEntries) {
    $targetId = [string]$entry.target_id
    $stageRoot = Join-Path $releaseRoot ("stage/stark-$Version-$($entry.asset_suffix)")
    $commands += New-ReleaseCommand `
        -PhaseName "Validate" `
        -TargetId $targetId `
        -Label "Validate staged SDK completeness" `
        -Executable $dotnetExecutable `
        -Arguments @(
            $releaseToolsAssembly,
            "validate-stage",
            "--sdk-root", $stageRoot,
            "--target-id", $targetId,
            "--output", (Join-Path $diagnosticsRoot ("stage-validation-$targetId.json"))
        ) `
        -Environment $dotnetEnvironment
    $commands += New-ReleaseCommand -PhaseName "Validate" -TargetId $targetId -Label "Audit native dependency closure" -Executable $powerShellPath -Arguments @(
        "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRoot "scripts/audit-release-native-dependencies.ps1"),
        "-SdkRoot", $stageRoot,
        "-OutputPath", (Join-Path $diagnosticsRoot ("native-dependencies-$targetId.json"))
    )
}

foreach ($entry in $matrixEntries) {
    $targetId = [string]$entry.target_id
    $archiveExtension = if ([string]$entry.archive_kind -ceq "zip") { ".zip" } else { ".tar.gz" }
    $archivePath = Join-Path $releaseRoot ("stark-$Version-$($entry.asset_suffix)$archiveExtension")
    $commands += New-ReleaseCommand -PhaseName "Smoke" -TargetId $targetId -Label "Smoke packaged release archive" -Executable $powerShellPath -Arguments @(
        "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRoot "scripts/smoke-release-archive.ps1"),
        "-ArchivePath", $archivePath,
        "-TargetTriple", [string]$entry.target_triple,
        "-WorkDir", (Join-Path $diagnosticsRoot ("archive-smoke-$targetId")),
        "-IsolatePath"
    )
    $commands += New-ReleaseCommand -PhaseName "Smoke" -TargetId $targetId -Label "Qualify archive-local installer lifecycle" -Executable $powerShellPath -Arguments @(
        "-NoProfile", "-NonInteractive", "-File", (Join-Path $repositoryRoot "scripts/smoke-release-install.ps1"),
        "-ArchivePath", $archivePath,
        "-TargetTriple", [string]$entry.target_triple,
        "-WorkDir", (Join-Path $diagnosticsRoot ("install-smoke-work-$targetId")),
        "-ReportPath", (Join-Path $diagnosticsRoot ("installer-lifecycle-$targetId.json")),
        "-DiagnosticsDir", (Join-Path $diagnosticsRoot ("installer-lifecycle-$targetId"))
    )
}

$selectedCommands = @($commands | Where-Object { $selectedPhases -contains [string]$_.phase })
$plan = [ordered]@{
    schemaVersion = 1
    planKind = "stark-local-release"
    version = $Version
    source = [ordered]@{
        ref = $Ref
        commit = $Commit
        head = $headCommit
        workingTreeDirty = $workingTreeDirty
    }
    targets = [object[]]$selectedTargetIds
    phases = [object[]]$selectedPhases
    planOnly = $planOnly
    dryRun = [bool]$DryRun
    willExecute = $willExecute
    publishingCandidate = [bool]$PublishingCandidate
    publicationAction = $false
    host = $hostIdentity
    releaseTools = [ordered]@{
        implementation = [string]$releaseToolsIdentity.implementation
        targetFramework = [string]$releaseToolsIdentity.targetFramework
        assembly = [string]$releaseToolsIdentity.assembly
        assemblyBytes = [int64]$releaseToolsIdentity.assemblyBytes
        assemblySha256 = [string]$releaseToolsIdentity.assemblySha256
        projectSha256 = [string]$releaseToolsIdentity.projectSha256
        dotnetSdkVersion = $requiredDotnetVersion
        dotnetRuntimeVersion = $requiredDotnetRuntimeVersion
    }
    workflowSemantics = [ordered]@{
        eventName = [string]$basePlan.eventName
        draft = [bool]$basePlan.draft
        prerelease = [bool]$basePlan.prerelease
        releasePlanSha256 = $basePlanSha256
        requiredDotnetVersion = $requiredDotnetVersion
        requiredDotnetRuntimeVersion = $requiredDotnetRuntimeVersion
    }
    configuration = [ordered]@{
        identityKind = [string]$configurationIdentity.identityKind
        algorithm = [string]$configurationIdentity.algorithm
        sha256 = $configurationDigest
        files = [object[]]$configurationFiles
        externalBuildTools = [object[]]$externalBuildTools
    }
    roots = [ordered]@{
        cacheBase = $cacheBasePath
        cache = $cacheRoot
        outputBase = $outputBasePath
        output = $outputRoot
        outputIdentitySha256 = $outputDigest
    }
    commands = [object[]]$selectedCommands
    warnings = [object[]]@(Get-ArrayValues -Value (Get-RequiredJsonProperty -Object $basePlan -Name "configurationWarnings" -Label "base release plan"))
}

$planJson = ($plan | ConvertTo-Json -Depth 30).Replace("`r`n", "`n")
if (-not [string]::IsNullOrWhiteSpace($PlanOutput)) {
    Write-DeterministicJson -Path (Resolve-RepositoryPath -Path $PlanOutput) -Value $plan
}
Write-Output $planJson

if (-not $willExecute) {
    return
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
foreach ($command in $selectedCommands) {
    Invoke-ReleaseCommand -Command $command
}

Write-Host "Completed local release phase(s) $($selectedPhases -join ', ') for $($selectedTargetIds -join ', ')."
