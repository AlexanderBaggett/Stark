param(
    [string] $Root = ".",

    [string] $OutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-Utf8Sha256 {
    param([Parameter(Mandatory = $true)][string] $Value)

    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}

function Get-OptionalPropertyValue {
    param(
        [object] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-RequiredPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Context
    )

    $value = Get-OptionalPropertyValue -Object $Object -Name $Name
    if ($null -eq $value) {
        throw "$Context is missing required property '$Name'."
    }
    return $value
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

function Assert-SafeRelativePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path.Contains('\') -or
        $Path.Contains(':') -or
        $Path -match '(^|/)\.\.?(/|$)') {
        throw "Release configuration input '$Path' is not a safe canonical relative path."
    }
    $reservedNames = @("CON", "PRN", "AUX", "NUL")
    foreach ($segment in $Path.Split('/', [StringSplitOptions]::None)) {
        $baseName = $segment.Split('.')[0]
        $hasControlCharacter = @($segment.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0
        if ([string]::IsNullOrEmpty($segment) -or
            $segment.Length -gt 255 -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
            $segment.EndsWith('.', [StringComparison]::Ordinal) -or
            $segment.IndexOfAny([char[]]'<>"|?*') -ge 0 -or
            $hasControlCharacter -or
            $reservedNames -contains $baseName.ToUpperInvariant() -or
            $baseName -match '^(?i:COM[1-9]|LPT[1-9])$') {
            throw "Release configuration input '$Path' contains nonportable segment '$segment'."
        }
    }
}

$rootPath = (Resolve-Path -LiteralPath $Root).Path
$paths = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
$portablePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-ConfigurationPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $canonicalPath = $Path.Replace('\', '/')
    Assert-SafeRelativePath -Path $canonicalPath
    if ($paths.Add($canonicalPath) -and -not $portablePaths.Add($canonicalPath)) {
        throw "Release configuration contains a case-colliding path '$canonicalPath'."
    }
}

foreach ($relativePath in @(
    ".gitattributes",
    ".github/workflows/qualify-private-backend.yml",
    ".github/workflows/prepare-llvm-toolchains.yml",
    ".github/workflows/release-contract.yml",
    ".github/workflows/release.yml",
    "eng/release/archive-content.json",
    "eng/release/archive-content.schema.json",
    "eng/release/build-tools.json",
    "eng/release/dependencies.json",
    "eng/release/llvm-toolchain-bundles.json",
    "eng/release/managed-license-evidence.json",
    "eng/release/NuGet.config",
    "eng/release/release-metadata.template.json",
    "eng/release/targets.json",
    "eng/release/vendor-packages.json",
    "eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj",
    "scripts/acquire-llvm-toolchain.ps1",
    "scripts/llvm-source-build.ps1",
    "scripts/acquire-release-build-tools.ps1",
    "scripts/assemble-sdk-manifest.ps1",
    "scripts/audit-public-repository.ps1",
    "scripts/audit-release-native-dependencies.ps1",
    "scripts/build-release.ps1",
    "scripts/generate-release-docs.ps1",
    "scripts/get-release-configuration-identity.ps1",
    "scripts/package-release.ps1",
    "scripts/prepare-release-public-assets.ps1",
    "scripts/prepare-vendor-release-input.ps1",
    "scripts/resolve-release-tools.ps1",
    "scripts/release-archive-extraction.ps1",
    "scripts/release-documentation-contract.ps1",
    "scripts/run-release-quality-gate.ps1",
    "scripts/release-installers/install.ps1",
    "scripts/release-installers/install.sh",
    "scripts/release-installers/uninstall.ps1",
    "scripts/release-installers/uninstall.sh",
    "scripts/release-repository-audit-allowlist.json",
    "scripts/sdl3-work-root-lock.ps1",
    "scripts/smoke-release-archive.ps1",
    "scripts/test-release-target-contract.ps1",
    "scripts/smoke-release-install.ps1",
    "scripts/stage-release-installers.ps1",
    "scripts/stage-release-repository-content.ps1",
    "global.json",
    "src/compiler.csproj"
)) {
    Add-ConfigurationPath -Path $relativePath
}

foreach ($releaseToolSource in (Get-ChildItem -LiteralPath (Join-Path $rootPath "eng/release/Stark.ReleaseTools") -Filter "*.cs" -File | Sort-Object Name)) {
    Add-ConfigurationPath -Path ("eng/release/Stark.ReleaseTools/" + $releaseToolSource.Name)
}

$dependenciesPath = Join-Path $rootPath "eng/release/dependencies.json"
$dependencies = Get-Content -LiteralPath $dependenciesPath -Raw | ConvertFrom-Json
foreach ($dependency in (Get-ArrayValues -Value (
    Get-RequiredPropertyValue -Object $dependencies -Name "dependencies" -Context "dependencies.json"))) {
    $acquisitionManifest = Get-OptionalPropertyValue -Object $dependency -Name "acquisitionManifest"
    if (-not [string]::IsNullOrWhiteSpace([string]$acquisitionManifest)) {
        Add-ConfigurationPath -Path ([string]$acquisitionManifest)
    }
    $qualifiedBundleManifest = Get-OptionalPropertyValue -Object $dependency -Name "qualifiedBundleManifest"
    if (-not [string]::IsNullOrWhiteSpace([string]$qualifiedBundleManifest)) {
        Add-ConfigurationPath -Path ([string]$qualifiedBundleManifest)
    }
    $globalJson = Get-OptionalPropertyValue -Object $dependency -Name "globalJson"
    if (-not [string]::IsNullOrWhiteSpace([string]$globalJson)) {
        Add-ConfigurationPath -Path ([string]$globalJson)
    }
    $nugetConfig = Get-OptionalPropertyValue -Object $dependency -Name "nugetConfig"
    if (-not [string]::IsNullOrWhiteSpace([string]$nugetConfig)) {
        Add-ConfigurationPath -Path ([string]$nugetConfig)
    }
    $generatedParser = Get-OptionalPropertyValue -Object $dependency -Name "generatedParser"
    if ($null -ne $generatedParser) {
        $regenerationScript = Get-OptionalPropertyValue -Object $generatedParser -Name "regenerationScript"
        if (-not [string]::IsNullOrWhiteSpace([string]$regenerationScript)) {
            Add-ConfigurationPath -Path ([string]$regenerationScript)
        }
    }
    foreach ($selection in (Get-ArrayValues -Value (
        Get-OptionalPropertyValue -Object $dependency -Name "selections"))) {
        $lockFile = Get-OptionalPropertyValue -Object $selection -Name "lockFile"
        if (-not [string]::IsNullOrWhiteSpace([string]$lockFile)) {
            Add-ConfigurationPath -Path ([string]$lockFile)
        }
    }
}

$managedLicensesPath = Join-Path $rootPath "eng/release/managed-license-evidence.json"
$managedLicenses = Get-Content -LiteralPath $managedLicensesPath -Raw | ConvertFrom-Json
foreach ($family in (Get-ArrayValues -Value (
    Get-RequiredPropertyValue -Object $managedLicenses -Name "packageFamilies" -Context "managed-license-evidence.json"))) {
    foreach ($file in (Get-ArrayValues -Value (Get-OptionalPropertyValue -Object $family -Name "files"))) {
        if ([string](Get-OptionalPropertyValue -Object $file -Name "sourceKind") -ceq "repository-file") {
            Add-ConfigurationPath -Path ([string](
                Get-RequiredPropertyValue -Object $file -Name "sourcePath" -Context "managed license repository evidence"))
        }
    }
}

$vendorCatalogPath = Join-Path $rootPath "eng/release/vendor-packages.json"
$vendorCatalog = Get-Content -LiteralPath $vendorCatalogPath -Raw | ConvertFrom-Json
foreach ($package in (Get-ArrayValues -Value (
    Get-RequiredPropertyValue -Object $vendorCatalog -Name "packages" -Context "vendor-packages.json"))) {
    Add-ConfigurationPath -Path ([string](
        Get-RequiredPropertyValue -Object $package -Name "buildRecipe" -Context "Vendor package"))
    $acquisitionManifest = Get-OptionalPropertyValue -Object $package -Name "acquisitionManifest"
    if (-not [string]::IsNullOrWhiteSpace([string]$acquisitionManifest)) {
        Add-ConfigurationPath -Path ([string]$acquisitionManifest)
    }
}

$files = @()
$digestLines = @()
foreach ($relativePath in $paths) {
    $absolutePath = Join-Path $rootPath $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        throw "Release configuration input '$relativePath' does not exist."
    }
    $file = Get-Item -LiteralPath $absolutePath -Force
    $sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $absolutePath).Hash.ToLowerInvariant()
    $files += [ordered]@{
        path = $relativePath
        bytes = [int64]$file.Length
        sha256 = $sha256
    }
    $digestLines += "$relativePath`0$($file.Length)`0$sha256"
}
$digestText = if ($digestLines.Count -eq 0) { "" } else { ($digestLines -join "`n") + "`n" }
$identity = [ordered]@{
    schemaVersion = 1
    identityKind = "stark-release-configuration"
    algorithm = "sha256-ordinal-path-size-content-v1"
    sha256 = Get-Utf8Sha256 -Value $digestText
    files = [object[]]$files
}
$json = ($identity | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Write-Output $json
} else {
    $resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        [System.IO.Path]::GetFullPath($OutputPath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputPath))
    }
    $outputParent = Split-Path -Parent $resolvedOutputPath
    New-Item -ItemType Directory -Force -Path $outputParent | Out-Null
    [System.IO.File]::WriteAllText(
        $resolvedOutputPath,
        $json,
        [System.Text.UTF8Encoding]::new($false))
}
