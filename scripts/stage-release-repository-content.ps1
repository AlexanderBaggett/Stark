param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string] $StageRoot,

    [Parameter(Mandatory = $true)]
    [string] $ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

function Get-RequiredPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Context is missing required property '$Name'."
    }
    return $property.Value
}

function Assert-SafeRelativePath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Context
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [System.IO.Path]::IsPathRooted($Path) `
        -or $Path.Contains('\') `
        -or $Path.Contains(':') `
        -or $Path.IndexOf([char]0) -ge 0 `
        -or $Path -match '(^|/)\.\.?(/|$)') {
        throw "$Context '$Path' is not a safe canonical relative path."
    }
}

function Test-IsSameOrDescendantPath {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root
    )

    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $candidate = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    $rootPath = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Root))
    return [string]::Equals($candidate, $rootPath, $comparison) `
        -or $candidate.StartsWith($rootPath + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Test-NativeBinaryMagic {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $header = [byte[]]::new(8)
        $count = $stream.Read($header, 0, $header.Length)
        if ($count -ge 4) {
            if (($header[0] -eq 0x7f -and $header[1] -eq 0x45 -and $header[2] -eq 0x4c -and $header[3] -eq 0x46) `
                -or ($header[0] -eq 0x4d -and $header[1] -eq 0x5a)) {
                return $true
            }

            $magic = ('{0:x2}{1:x2}{2:x2}{3:x2}' -f $header[0], $header[1], $header[2], $header[3])
            if ($magic -in @("feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")) {
                return $true
            }
        }

        return $count -eq 8 `
            -and [System.Text.Encoding]::ASCII.GetString($header, 0, 8) -ceq "!<arch>`n"
    } finally {
        $stream.Dispose()
    }
}

$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$stageCandidate = [System.IO.Path]::GetFullPath($StageRoot)
$manifest = (Resolve-Path -LiteralPath $ManifestPath).Path
if (-not (Test-Path -LiteralPath $stageCandidate -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $stageCandidate | Out-Null
}
$stage = (Resolve-Path -LiteralPath $stageCandidate).Path

$document = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ([int](Get-RequiredPropertyValue -Object $document -Name "schemaVersion" -Context "archive content manifest") -ne 1) {
    throw "archive-content.json schemaVersion must be 1."
}
$content = Get-RequiredPropertyValue -Object $document -Name "repositoryContent" -Context "archive content manifest"
$forbiddenExtensions = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($extensionValue in (Get-ArrayValues -Value (
        Get-RequiredPropertyValue -Object $content -Name "forbiddenExtensions" -Context "repository content policy"))) {
    $extension = [string]$extensionValue
    if ($extension -cnotmatch '^\.[a-z0-9]+$' -or -not $forbiddenExtensions.Add($extension)) {
        throw "Repository content forbidden extension '$extension' is invalid or duplicated."
    }
}

$destinationPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$treeIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$copiedFiles = 0
foreach ($tree in (Get-ArrayValues -Value (
        Get-RequiredPropertyValue -Object $content -Name "trees" -Context "repository content policy"))) {
    $treeId = [string](Get-RequiredPropertyValue -Object $tree -Name "id" -Context "repository content tree")
    if ($treeId -cnotmatch '^[a-z][a-z0-9-]*$' -or -not $treeIds.Add($treeId)) {
        throw "Repository content tree ID '$treeId' is invalid or duplicated."
    }

    $sourceRelative = [string](Get-RequiredPropertyValue -Object $tree -Name "source" -Context "repository content tree '$treeId'")
    $destinationRelative = [string](Get-RequiredPropertyValue -Object $tree -Name "destination" -Context "repository content tree '$treeId'")
    Assert-SafeRelativePath -Path $sourceRelative -Context "Repository content source"
    Assert-SafeRelativePath -Path $destinationRelative -Context "Repository content destination"

    $sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $repository $sourceRelative))
    $destinationRoot = [System.IO.Path]::GetFullPath((Join-Path $stage $destinationRelative))
    if (-not (Test-IsSameOrDescendantPath -Path $sourceRoot -Root $repository) `
        -or -not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Repository content source '$sourceRelative' is missing or escapes the repository."
    }
    if (-not (Test-IsSameOrDescendantPath -Path $destinationRoot -Root $stage)) {
        throw "Repository content destination '$destinationRelative' escapes the release stage."
    }

    $includeExtensions = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($extensionValue in (Get-ArrayValues -Value (
            Get-RequiredPropertyValue -Object $tree -Name "includeExtensions" -Context "repository content tree '$treeId'"))) {
        $extension = [string]$extensionValue
        if ($extension -cnotmatch '^\.[a-z0-9]+$' `
            -or $forbiddenExtensions.Contains($extension) `
            -or -not $includeExtensions.Add($extension)) {
            throw "Repository content tree '$treeId' extension '$extension' is invalid, forbidden, or duplicated."
        }
    }

    $includeFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relativeValue in (Get-ArrayValues -Value (
            Get-RequiredPropertyValue -Object $tree -Name "includeFiles" -Context "repository content tree '$treeId'"))) {
        $relative = [string]$relativeValue
        Assert-SafeRelativePath -Path $relative -Context "Repository content explicit file"
        if (-not $includeFiles.Add($relative)) {
            throw "Repository content tree '$treeId' duplicates explicit file '$relative'."
        }
        $explicitSource = [System.IO.Path]::GetFullPath((Join-Path $sourceRoot $relative))
        if (-not (Test-IsSameOrDescendantPath -Path $explicitSource -Root $sourceRoot) `
            -or -not (Test-Path -LiteralPath $explicitSource -PathType Leaf)) {
            throw "Repository content tree '$treeId' explicit file '$relative' is missing or escapes its source root."
        }
    }

    if ($includeExtensions.Count -eq 0 -and $includeFiles.Count -eq 0) {
        throw "Repository content tree '$treeId' has no inclusion allowlist."
    }

    $treeCopiedFiles = 0
    foreach ($file in (Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force | Sort-Object FullName)) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Repository content source '$($file.FullName)' is a reparse point."
        }

        $relative = [System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
        $included = $includeFiles.Contains($relative) -or $includeExtensions.Contains($file.Extension)
        if (-not $included) {
            continue
        }
        if ($forbiddenExtensions.Contains($file.Extension)) {
            throw "Repository content tree '$treeId' selected forbidden artifact '$relative'."
        }
        if (Test-NativeBinaryMagic -Path $file.FullName) {
            throw "Repository content tree '$treeId' selected native binary '$relative'."
        }

        $destinationRelativePath = "$destinationRelative/$relative"
        if (-not $destinationPaths.Add($destinationRelativePath)) {
            throw "Repository content destination '$destinationRelativePath' collides with another staged file."
        }

        $destinationPath = Join-Path $stage $destinationRelativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
        $treeCopiedFiles++
        $copiedFiles++
    }

    foreach ($explicitFile in $includeFiles) {
        if (-not $destinationPaths.Contains("$destinationRelative/$explicitFile")) {
            throw "Repository content tree '$treeId' did not stage required explicit file '$explicitFile'."
        }
    }
    if ($treeCopiedFiles -eq 0) {
        throw "Repository content tree '$treeId' selected no files."
    }
}

Write-Host "Staged $copiedFiles allowlisted repository documentation/example file(s)."
