param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("sdl3", "glfw", "sqlite")]
    [string] $Dependency,

    [Parameter(Mandatory = $true)]
    [string] $PackageId,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $SourceIdentity,

    [Parameter(Mandatory = $true)]
    [ValidateSet("linux-x64", "linux-arm64", "windows-x64", "windows-arm64", "macos-x64", "macos-arm64")]
    [string] $TargetId,

    [Parameter(Mandatory = $true)]
    [string] $TargetTriple,

    [Parameter(Mandatory = $true)]
    [ValidateSet("zip", "targz")]
    [string] $ArchiveKind,

    [Parameter(Mandatory = $true)]
    [string] $BundleTag,

    [Parameter(Mandatory = $true)]
    [string] $VendorRoot,

    [Parameter(Mandatory = $true)]
    [string] $ContributionManifestPath,

    [Parameter(Mandatory = $true)]
    [string] $ReleaseToolsPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $QualificationCommit,

    [Parameter(Mandatory = $true)]
    [string] $QualificationWorkflow,

    [Parameter(Mandatory = $true)]
    [string] $ReleaseRepository,

    [Parameter(Mandatory = $true)]
    [string] $OutputDir,

    [Parameter(Mandatory = $true)]
    [string] $StagingDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))

function Resolve-InputPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Resolve-ExistingFile {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][string] $Label)

    $resolved = Resolve-Path -LiteralPath (Resolve-InputPath -Path $Path) -ErrorAction SilentlyContinue
    if ($null -eq $resolved -or -not (Test-Path -LiteralPath $resolved.Path -PathType Leaf)) {
        throw "$Label '$Path' does not exist."
    }
    return $resolved.Path
}

function Resolve-ExistingDirectory {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][string] $Label)

    $resolved = Resolve-Path -LiteralPath (Resolve-InputPath -Path $Path) -ErrorAction SilentlyContinue
    if ($null -eq $resolved -or -not (Test-Path -LiteralPath $resolved.Path -PathType Container)) {
        throw "$Label '$Path' does not exist."
    }
    return $resolved.Path
}

function Get-Inventory {
    param([Parameter(Mandatory = $true)][string] $Root, [Parameter(Mandatory = $true)][string] $ReleaseTools)

    $output = @(& dotnet $ReleaseTools inventory-tree --root $Root 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inventory native bundle tree '$Root': $($output -join [Environment]::NewLine)"
    }
    return (($output | Select-Object -Last 1) | ConvertFrom-Json)
}

function Assert-MatchingInventory {
    param([Parameter(Mandatory = $true)][object] $Expected, [Parameter(Mandatory = $true)][object] $Actual)

    foreach ($property in @("fileCount", "logicalBytes", "directoryCount", "symlinkCount", "treeSha256")) {
        if ([string]$Expected.$property -cne [string]$Actual.$property) {
            throw "Extracted native bundle inventory differs at '$property'."
        }
    }
}

if ($BundleTag -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') {
    throw "Bundle tag '$BundleTag' is not a portable release tag."
}
if ($ReleaseRepository -notmatch '^[0-9A-Za-z_.-]+/[0-9A-Za-z_.-]+$') {
    throw "Release repository '$ReleaseRepository' must be an owner/repository pair."
}

$vendorRootPath = Resolve-ExistingDirectory -Path $VendorRoot -Label "Vendor contribution root"
$contributionPath = Resolve-ExistingFile -Path $ContributionManifestPath -Label "Vendor contribution manifest"
$releaseTools = Resolve-ExistingFile -Path $ReleaseToolsPath -Label "Release tools assembly"
$nativeRoot = Resolve-ExistingDirectory -Path (Join-Path $vendorRootPath "dist/$TargetId/native/$Dependency") -Label "$PackageId native payload"
$provenancePath = Resolve-ExistingFile -Path (Join-Path $nativeRoot "PROVENANCE.json") -Label "$PackageId native provenance"

$contribution = Get-Content -LiteralPath $contributionPath -Raw | ConvertFrom-Json
$packages = @($contribution.packages)
if ([int]$contribution.schemaVersion -ne 1 -or
    [string]$contribution.targetId -cne $TargetId -or
    [string]$contribution.targetTriple -cne $TargetTriple -or
    $packages.Count -ne 1) {
    throw "Vendor contribution does not describe exactly one '$TargetId' package."
}
$package = $packages[0]
if ([string]$package.id -cne $PackageId -or
    [string]$package.version -cne $Version -or
    [string]$package.sourceIdentity -cne $SourceIdentity) {
    throw "Vendor contribution identity does not match '$PackageId' '$Version' '$SourceIdentity'."
}

$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
if ([int]$provenance.schemaVersion -ne 1 -or
    [string]$provenance.packageId -cne $PackageId -or
    [string]$provenance.version -cne $Version -or
    [string]$provenance.sourceIdentity -cne $SourceIdentity -or
    [string]$provenance.target.id -cne $TargetId -or
    [string]$provenance.target.targetTriple -cne $TargetTriple) {
    throw "Native provenance identity does not match the requested bundle."
}
$recipePath = [string]$provenance.buildRecipe
$recipe = Resolve-ExistingFile -Path $recipePath -Label "Vendor build recipe"

$outputRoot = Resolve-InputPath -Path $OutputDir
$stagingRoot = Resolve-InputPath -Path $StagingDir
$artifactsPrefix = [System.IO.Path]::TrimEndingDirectorySeparator($artifactsRoot) + [System.IO.Path]::DirectorySeparatorChar
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
foreach ($generatedRoot in @($outputRoot, $stagingRoot)) {
    if (-not $generatedRoot.StartsWith($artifactsPrefix, $pathComparison)) {
        throw "Generated native bundle directory '$generatedRoot' must be below repository artifacts."
    }
}
if ([System.IO.Path]::GetFileName($stagingRoot) -cne $TargetId) {
    throw "Staging directory must end with the target ID '$TargetId' so the archive has the required top-level directory."
}
foreach ($generatedRoot in @($outputRoot, $stagingRoot)) {
    if (Test-Path -LiteralPath $generatedRoot) {
        Remove-Item -LiteralPath $generatedRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $generatedRoot | Out-Null
}

$stagedNativeRoot = Join-Path $stagingRoot "native/$Dependency"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stagedNativeRoot) | Out-Null
Copy-Item -LiteralPath $nativeRoot -Destination $stagedNativeRoot -Recurse
$nativeInventory = Get-Inventory -Root $stagedNativeRoot -ReleaseTools $releaseTools
$sourceProvenanceSha = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $stagedNativeRoot "PROVENANCE.json")).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    schemaVersion = 1
    bundleKind = "stark-vendor-native-bundle"
    dependency = $Dependency
    packageId = $PackageId
    version = $Version
    sourceIdentity = $SourceIdentity
    bundleTag = $BundleTag
    target = [ordered]@{
        id = $TargetId
        targetTriple = $TargetTriple
    }
    nativePayload = [ordered]@{
        path = "native/$Dependency"
        inventory = $nativeInventory
        sourceProvenanceSha256 = $sourceProvenanceSha
    }
    buildRecipe = [ordered]@{
        path = $recipePath.Replace('\', '/')
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $recipe).Hash.ToLowerInvariant()
    }
    qualification = [ordered]@{
        commit = $QualificationCommit
        workflow = $QualificationWorkflow
    }
}
$manifestPath = Join-Path $stagingRoot "manifest.json"
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8
$manifestSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()

$extension = if ($ArchiveKind -ceq "zip") { ".zip" } else { ".tar.gz" }
$assetName = "stark-$BundleTag-$TargetId$extension"
$archivePath = Join-Path $outputRoot $assetName
$archiveOutput = @(& dotnet $releaseTools create-archive --source-root $stagingRoot --output $archivePath --kind $ArchiveKind 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Deterministic native bundle creation failed: $($archiveOutput -join [Environment]::NewLine)"
}
$archiveResult = ($archiveOutput | Select-Object -Last 1) | ConvertFrom-Json

$smokeRoot = "$stagingRoot-smoke"
if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}
try {
    $extractOutput = @(& dotnet $releaseTools extract-archive --archive $archivePath --kind $ArchiveKind --destination $smokeRoot --required-root $TargetId --label "$PackageId native bundle" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Native bundle extraction smoke failed: $($extractOutput -join [Environment]::NewLine)"
    }
    $extractedRoot = Join-Path $smokeRoot $TargetId
    $extractedManifest = Resolve-ExistingFile -Path (Join-Path $extractedRoot "manifest.json") -Label "Extracted native bundle manifest"
    $extractedManifestSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $extractedManifest).Hash.ToLowerInvariant()
    if ($extractedManifestSha -cne $manifestSha) {
        throw "Extracted native bundle manifest differs from the staged manifest."
    }
    $extractedInventory = Get-Inventory -Root (Join-Path $extractedRoot "native/$Dependency") -ReleaseTools $releaseTools
    Assert-MatchingInventory -Expected $nativeInventory -Actual $extractedInventory
} finally {
    if (Test-Path -LiteralPath $smokeRoot) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
}

$record = [ordered]@{
    schemaVersion = 1
    bundleKind = "stark-vendor-native-bundle"
    dependency = $Dependency
    packageId = $PackageId
    version = $Version
    sourceIdentity = $SourceIdentity
    bundleTag = $BundleTag
    target = [ordered]@{
        id = $TargetId
        targetTriple = $TargetTriple
    }
    archiveKind = $ArchiveKind
    archive = [ordered]@{
        name = $assetName
        url = "https://github.com/$ReleaseRepository/releases/download/$BundleTag/$assetName"
        sha256 = [string]$archiveResult.sha256
        size = [int64]$archiveResult.bytes
    }
    manifestSha256 = $manifestSha
    nativeInventory = $nativeInventory
    qualification = [ordered]@{
        commit = $QualificationCommit
        workflow = $QualificationWorkflow
    }
}
$recordPath = Join-Path $outputRoot "$TargetId.bundle.json"
$record | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $recordPath -Encoding utf8

Write-Host "Created and smoke-tested $PackageId $Version native bundle for '$TargetId'."
Write-Host "Archive: $archivePath"
Write-Host "Record: $recordPath"
