#requires -Version 7.0

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64")]
    [string] $TargetId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$targetManifestPath = Join-Path $repositoryRoot "eng/release/targets.json"
$vendorCatalogPath = Join-Path $repositoryRoot "eng/release/vendor-packages.json"
$llvmManifestPath = Join-Path $repositoryRoot "scripts/llvm-22.1.8-assets.json"
$llvmBundleManifestPath = Join-Path $repositoryRoot "eng/release/llvm-toolchain-bundles.json"

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Required release contract property '$Name' was not found."
    }
    return ,$property.Value
}

function Assert-PowerShellParses {
    param([Parameter(Mandatory = $true)][string] $Path)

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if (@($errors).Count -ne 0) {
        $messages = @($errors | ForEach-Object { $_.Message }) -join "; "
        throw "Release script '$Path' does not parse: $messages"
    }
}

$targetManifest = Get-Content -LiteralPath $targetManifestPath -Raw | ConvertFrom-Json
$expectedTargetIds = @("linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64")
$actualTargetIds = @((Get-RequiredProperty -Object $targetManifest -Name "targets") | ForEach-Object { [string]$_.id })
if (($actualTargetIds -join "`n") -cne ($expectedTargetIds -join "`n")) {
    throw "Release target manifest does not contain the exact reviewed six-target order."
}

$targetMatches = @((Get-RequiredProperty -Object $targetManifest -Name "targets") | Where-Object { [string]$_.id -ceq $TargetId })
if ($targetMatches.Count -ne 1) {
    throw "Release target manifest must define exactly one '$TargetId' target."
}
$target = $targetMatches[0]
$targetOperatingSystem = [string](Get-RequiredProperty -Object $target -Name "operatingSystem")
$targetArchitecture = [string](Get-RequiredProperty -Object $target -Name "architecture")
$hostOperatingSystem = if ($IsWindows) { "windows" } elseif ($IsLinux) { "linux" } elseif ($IsMacOS) { "macos" } else { "unknown" }
$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if ($hostOperatingSystem -cne $targetOperatingSystem -or $hostArchitecture -cne $targetArchitecture) {
    throw "Release contract '$TargetId' requires $targetOperatingSystem-$targetArchitecture; runner is $hostOperatingSystem-$hostArchitecture."
}

$vendorCatalog = Get-Content -LiteralPath $vendorCatalogPath -Raw | ConvertFrom-Json
foreach ($package in (Get-RequiredProperty -Object $vendorCatalog -Name "packages")) {
    $packageId = [string](Get-RequiredProperty -Object $package -Name "id")
    $targetSupport = Get-RequiredProperty -Object $package -Name "targetSupport"
    foreach ($expectedTargetId in $expectedTargetIds) {
        $support = [string](Get-RequiredProperty -Object $targetSupport -Name $expectedTargetId)
        if ($support -notin @("required-source-build", "required-binary")) {
            throw "Vendor package '$packageId' has unsupported target contract '$support' for '$expectedTargetId'."
        }
    }
}

$glfw = @((Get-RequiredProperty -Object $vendorCatalog -Name "packages") | Where-Object { [string]$_.id -ceq "Vendor.GLFW" })
if ($glfw.Count -ne 1) {
    throw "Vendor catalog must contain exactly one Vendor.GLFW package."
}
$glfwSupport = [string](Get-RequiredProperty -Object (Get-RequiredProperty -Object $glfw[0] -Name "targetSupport") -Name $TargetId)
$glfwBinaries = @((Get-RequiredProperty -Object $glfw[0] -Name "binaryInputs") | Where-Object { [string]$_.target -ceq $TargetId })
if ($targetOperatingSystem -ceq "macos") {
    if ($glfwSupport -cne "required-binary" -or $glfwBinaries.Count -ne 1) {
        throw "macOS GLFW target '$TargetId' must select exactly one reviewed pinned universal-binary input."
    }
    $glfwBinary = $glfwBinaries[0]
    if ([string]$glfwBinary.name -cne "glfw-3.4.bin.MACOS.zip" `
        -or [string]$glfwBinary.sha256 -cne "6775085bdae60312a3002bff2e39779a83bc72a7e1c810bd806fddb00cb35fd0") {
        throw "macOS GLFW target '$TargetId' does not pin the reviewed GLFW 3.4 universal archive."
    }
} elseif ($glfwSupport -cne "required-source-build" -or $glfwBinaries.Count -ne 0) {
    throw "Non-macOS GLFW target '$TargetId' must use the pinned source-build contract."
}

$llvmManifest = Get-Content -LiteralPath $llvmManifestPath -Raw | ConvertFrom-Json
$llvmPlatform = Get-RequiredProperty -Object (Get-RequiredProperty -Object $llvmManifest -Name "platforms") -Name $TargetId
foreach ($pattern in @(Get-RequiredProperty -Object $llvmPlatform -Name "requiredPatterns")) {
    $normalizedPattern = ([string]$pattern).Replace('\', '/').ToLowerInvariant()
    if ($normalizedPattern.EndsWith(".a", [StringComparison]::Ordinal) `
        -or $normalizedPattern.EndsWith(".lib", [StringComparison]::Ordinal) `
        -or $normalizedPattern -ceq "lib/libllvm*") {
        throw "LLVM target '$TargetId' declares development-only runtime pattern '$pattern'."
    }
}

$llvmBundleManifest = Get-Content -LiteralPath $llvmBundleManifestPath -Raw | ConvertFrom-Json
if ([int](Get-RequiredProperty -Object $llvmBundleManifest -Name "schemaVersion") -ne 1 -or
    [string](Get-RequiredProperty -Object $llvmBundleManifest -Name "llvmVersion") -cne "22.1.8") {
    throw "Qualified LLVM bundle manifest has an unexpected identity."
}
$bundleEntries = @((Get-RequiredProperty -Object $llvmBundleManifest -Name "targets"))
$bundleTargetIds = @($bundleEntries | ForEach-Object { [string](Get-RequiredProperty -Object $_ -Name "target") })
if (($bundleTargetIds -join "`n") -cne ($expectedTargetIds -join "`n")) {
    throw "Qualified LLVM bundle manifest does not contain the exact reviewed six-target order."
}
$bundleEntry = @($bundleEntries | Where-Object { [string]$_.target -ceq $TargetId })[0]
$bundleStatus = [string](Get-RequiredProperty -Object $bundleEntry -Name "status")
if ($bundleStatus -notin @("build-required", "published")) {
    throw "Qualified LLVM bundle '$TargetId' has unsupported state '$bundleStatus'."
}
$expectedArchiveKind = [string](Get-RequiredProperty -Object $target -Name "archiveKind")
$expectedArchiveExtension = [string](Get-RequiredProperty -Object $target -Name "archiveExtension")
$expectedBundleName = "stark-llvm-22.1.8-stark.1-$TargetId$expectedArchiveExtension"
if ([string](Get-RequiredProperty -Object $bundleEntry -Name "archiveKind") -cne $expectedArchiveKind -or
    [string](Get-RequiredProperty -Object $bundleEntry -Name "assetName") -cne $expectedBundleName) {
    throw "Qualified LLVM bundle '$TargetId' differs from the target archive contract."
}
if ($bundleStatus -ceq "published") {
    $bundleArchive = Get-RequiredProperty -Object $bundleEntry -Name "archive"
    if ([string](Get-RequiredProperty -Object $bundleArchive -Name "name") -cne $expectedBundleName -or
        [string](Get-RequiredProperty -Object $bundleArchive -Name "sha256") -notmatch '^[0-9a-f]{64}$' -or
        [int64](Get-RequiredProperty -Object $bundleArchive -Name "size") -le 0 -or
        [string](Get-RequiredProperty -Object $bundleEntry -Name "manifestSha256") -notmatch '^[0-9a-f]{64}$') {
        throw "Published LLVM bundle '$TargetId' is not fully size/hash pinned."
    }
}

$criticalScripts = @(
    "scripts/acquire-llvm-toolchain.ps1",
    "scripts/assemble-sdk-manifest.ps1",
    "scripts/prepare-glfw-vendor-release-input.ps1",
    "scripts/prepare-sqlite-vendor-release-input.ps1",
    "scripts/qualify-assembly-bridge.ps1"
)
foreach ($relativePath in $criticalScripts) {
    Assert-PowerShellParses -Path (Join-Path $repositoryRoot $relativePath)
}

$assemblyBridge = Get-Content -LiteralPath (Join-Path $repositoryRoot "scripts/qualify-assembly-bridge.ps1") -Raw
if (-not $assemblyBridge.Contains('"--package-image-output", $manifestPath', [StringComparison]::Ordinal)) {
    throw "Assembly bridge qualification must explicitly bind its producer package-image path."
}

$sqlite = Get-Content -LiteralPath (Join-Path $repositoryRoot "scripts/prepare-sqlite-vendor-release-input.ps1") -Raw
if (-not $sqlite.Contains('"' + $TargetId + '" {', [StringComparison]::Ordinal)) {
    throw "SQLite native-object validation has no explicit '$TargetId' target contract."
}

Write-Host "Release contract passed for $TargetId on $hostOperatingSystem-$hostArchitecture."
