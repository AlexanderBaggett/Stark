param(
    [Parameter(Mandatory = $true)]
    [string] $SdkRoot,

    [Parameter(Mandatory = $true)]
    [string] $CompilerPath,

    [Parameter(Mandatory = $true)]
    [string] $StdlibDist,

    [Parameter(Mandatory = $true)]
    [string] $VendorDist,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $AssetSuffix,

    [string] $TargetTriple = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Assemble only compiler-verified, relocatable artifacts into the runtime SDK
# contract. System is mandatory; the schema-2 vendor release input is a closed
# package/file declaration rather than a best-effort allowlist. Every declared
# package must match the staged package image and its native payload exactly.
# The staged compiler remains the source of truth for package, target, native,
# dependency, and optimization facts carried by each package image.

function Get-JsonPropertyValue {
    param(
        [object] $InputObject,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    # Unary comma keeps a JSON array as one property value while crossing the
    # PowerShell pipeline. Array-specific helpers decide when to enumerate it.
    return ,$property.Value
}

function Get-JsonArray {
    param(
        [object] $InputObject,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $value = Get-JsonPropertyValue -InputObject $InputObject -Name $Name
    if ($null -eq $value) {
        return @()
    }

    return @($value)
}

function Get-RequiredJsonPropertyValue {
    param(
        [object] $InputObject,
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $value = Get-JsonPropertyValue -InputObject $InputObject -Name $Name
    if ($null -eq $value) {
        throw "$Label is missing required property '$Name'."
    }

    return ,$value
}

function Get-RequiredJsonString {
    param(
        [object] $InputObject,
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $value = Get-RequiredJsonPropertyValue -InputObject $InputObject -Name $Name -Label $Label
    if ($value -isnot [string] `
        -or [string]::IsNullOrWhiteSpace($value) `
        -or $value -cne $value.Trim()) {
        throw "$Label property '$Name' must be a nonempty, trimmed string."
    }

    return [string]$value
}

function Get-RequiredJsonArray {
    param(
        [object] $InputObject,
        [Parameter(Mandatory = $true)]
        [string] $Name,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $value = Get-RequiredJsonPropertyValue -InputObject $InputObject -Name $Name -Label $Label
    if ($value -isnot [System.Array]) {
        throw "$Label property '$Name' must be a JSON array."
    }

    return ,([object[]]@($value))
}

function Assert-ExactJsonProperties {
    param(
        [object] $InputObject,
        [Parameter(Mandatory = $true)]
        [string[]] $Names,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if ($null -eq $InputObject -or $InputObject -isnot [pscustomobject]) {
        throw "$Label must be a JSON object."
    }

    $actual = @(Get-OrdinalSortedStrings -Values @($InputObject.PSObject.Properties.Name))
    $expected = @(Get-OrdinalSortedStrings -Values @($Names))
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "$Label properties '$($actual -join ', ')' do not match schema properties '$($expected -join ', ')'."
    }
}

function Assert-SortedUniqueStrings {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Values,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $previous = $null
    foreach ($value in $Values) {
        if ($value -isnot [string] `
            -or [string]::IsNullOrWhiteSpace($value) `
            -or $value -cne $value.Trim()) {
            throw "$Label must contain only nonempty, trimmed strings."
        }

        if ($null -ne $previous `
            -and [System.StringComparer]::Ordinal.Compare($previous, [string]$value) -ge 0) {
            throw "$Label must be strictly ordinal-sorted and duplicate-free."
        }

        $previous = [string]$value
    }
}

function Get-OrdinalSortedStrings {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Values
    )

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($value in $Values) {
        $items.Add([string]$value)
    }

    $items.Sort([System.StringComparer]::Ordinal)
    return [string[]]$items.ToArray()
}

function Get-OrdinalSortedUniqueStrings {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Values
    )

    $items = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($value in $Values) {
        [void]$items.Add([string]$value)
    }

    return [string[]]@($items)
}

function ConvertTo-SdkRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SdkRoot,
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $canonicalRoot = [System.IO.Path]::GetFullPath($SdkRoot)
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    $relativePath = [System.IO.Path]::GetRelativePath($canonicalRoot, $canonicalPath).Replace('\', '/')
    if ([System.IO.Path]::IsPathRooted($relativePath) `
        -or $relativePath -eq ".." `
        -or $relativePath.StartsWith("../", [System.StringComparison]::Ordinal)) {
        throw "$Label '$canonicalPath' is outside SDK root '$canonicalRoot'."
    }

    return $relativePath
}

function Resolve-StagedPackagePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SdkRoot,
        [Parameter(Mandatory = $true)]
        [string] $PackageDirectory,
        [Parameter(Mandatory = $true)]
        [string] $Value,
        [Parameter(Mandatory = $true)]
        [string] $Label,
        [ValidateSet("Leaf", "Container")]
        [string] $PathType = "Leaf"
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Label must not be empty."
    }

    if ([System.IO.Path]::IsPathRooted($Value)) {
        throw "$Label '$Value' is machine-local; release SDK paths must be relative."
    }

    $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $PackageDirectory $Value))
    ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $resolvedPath -Label $Label | Out-Null
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType $PathType)) {
        throw "$Label '$Value' resolves to missing $($PathType.ToLowerInvariant()) '$resolvedPath'."
    }

    return $resolvedPath
}

function Invoke-StagedPackageInspection {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CompilerPath,
        [Parameter(Mandatory = $true)]
        [string] $ImagePath
    )

    $inspectionOutput = @(& $CompilerPath inspect-pkg $ImagePath --format json 2>&1)
    $inspectionExitCode = $LASTEXITCODE
    $inspectionText = $inspectionOutput -join [System.Environment]::NewLine
    if ($inspectionExitCode -ne 0) {
        throw "Staged compiler rejected package image '$ImagePath' (exit $inspectionExitCode):$([System.Environment]::NewLine)$inspectionText"
    }

    try {
        return $inspectionText | ConvertFrom-Json -Depth 100
    } catch {
        throw "Staged compiler returned invalid inspect-pkg JSON for '$ImagePath': $($_.Exception.Message)"
    }
}

function Get-StagedCompilerCompatibility {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CompilerPath
    )

    $compatibilityOutput = @(& $CompilerPath --print-sdk-compatibility 2>&1)
    $compatibilityExitCode = $LASTEXITCODE
    $compatibilityLines = @($compatibilityOutput `
        | ForEach-Object { [string]$_ } `
        | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($compatibilityExitCode -ne 0) {
        $compatibilityText = $compatibilityOutput -join [System.Environment]::NewLine
        throw "Staged compiler could not report its SDK compatibility line (exit $compatibilityExitCode):$([System.Environment]::NewLine)$compatibilityText"
    }

    if ($compatibilityLines.Count -ne 1) {
        $compatibilityText = $compatibilityOutput -join [System.Environment]::NewLine
        throw "Staged compiler must report exactly one nonempty SDK compatibility line; received:$([System.Environment]::NewLine)$compatibilityText"
    }

    $compatibility = $compatibilityLines[0]
    if ($compatibility -cne $compatibility.Trim() `
        -or $compatibility -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
        throw "Staged compiler reported invalid SDK compatibility line '$compatibility'."
    }

    return $compatibility
}

function Get-PackageFormatVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ImagePath
    )

    $bytes = [System.IO.File]::ReadAllBytes($ImagePath)
    if ($bytes.Length -lt 12) {
        throw "Package image '$ImagePath' is too short to contain a binary package header."
    }

    return [System.BitConverter]::ToUInt32($bytes, 8)
}

function Assert-StaticLibraryArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 8) {
        throw "$Label '$Path' is empty or truncated."
    }

    $archiveMagic = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 8)
    if ($archiveMagic -eq "!<arch>`n") {
        return
    }

    if ($archiveMagic -eq "!<thin>`n") {
        throw "$Label '$Path' is a thin archive; release SDK archives must contain their object payloads."
    }

    # A Darwin universal static library wraps one ar archive per architecture
    # in a Mach-O fat container. Validate each slice rather than accepting the
    # fat magic alone (which could otherwise describe an executable).
    $fatMagic = ($bytes[0..3] | ForEach-Object { $_.ToString("X2") }) -join ""
    $isBigEndian = $fatMagic -in @("CAFEBABE", "CAFEBABF")
    $isFat64 = $fatMagic -in @("CAFEBABF", "BFBAFECA")
    if (-not $isBigEndian -and $fatMagic -notin @("BEBAFECA", "BFBAFECA")) {
        throw "$Label '$Path' is not a static-library archive."
    }

    $architectureCount = Read-UnsignedInteger `
        -Bytes $bytes `
        -Offset 4 `
        -Width 4 `
        -BigEndian $isBigEndian
    if ($architectureCount -lt 1 -or $architectureCount -gt 64) {
        throw "$Label '$Path' has an invalid universal-archive architecture count ($architectureCount)."
    }

    $entrySize = if ($isFat64) { 32 } else { 20 }
    if (8 + ($architectureCount * $entrySize) -gt $bytes.Length) {
        throw "$Label '$Path' has a truncated universal-archive header."
    }

    for ($index = 0; $index -lt $architectureCount; $index++) {
        $entryOffset = 8 + ($index * $entrySize)
        $sliceOffset = Read-UnsignedInteger `
            -Bytes $bytes `
            -Offset ($entryOffset + 8) `
            -Width $(if ($isFat64) { 8 } else { 4 }) `
            -BigEndian $isBigEndian
        $sliceSize = Read-UnsignedInteger `
            -Bytes $bytes `
            -Offset ($entryOffset + $(if ($isFat64) { 16 } else { 12 })) `
            -Width $(if ($isFat64) { 8 } else { 4 }) `
            -BigEndian $isBigEndian
        if ($sliceOffset -gt [long]::MaxValue `
            -or $sliceSize -lt 8 `
            -or $sliceSize -gt [long]::MaxValue `
            -or [long]$sliceOffset -gt $bytes.Length - 8 `
            -or [long]$sliceSize -gt $bytes.Length - [long]$sliceOffset) {
            throw "$Label '$Path' has an invalid universal-archive slice $index."
        }

        $sliceMagic = [System.Text.Encoding]::ASCII.GetString($bytes, [int]$sliceOffset, 8)
        if ($sliceMagic -eq "!<thin>`n") {
            throw "$Label '$Path' universal slice $index is a thin archive; release SDK archives must contain their object payloads."
        }

        if ($sliceMagic -ne "!<arch>`n") {
            throw "$Label '$Path' universal slice $index is not a static-library archive."
        }
    }
}

function Read-UnsignedInteger {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]] $Bytes,
        [int] $Offset,
        [ValidateSet(4, 8)]
        [int] $Width,
        [bool] $BigEndian
    )

    [byte[]]$valueBytes = $Bytes[$Offset..($Offset + $Width - 1)]
    if ([System.BitConverter]::IsLittleEndian -eq $BigEndian) {
        [System.Array]::Reverse($valueBytes)
    }

    if ($Width -eq 4) {
        return [uint64][System.BitConverter]::ToUInt32($valueBytes, 0)
    }

    return [System.BitConverter]::ToUInt64($valueBytes, 0)
}

function Get-NormalizedTargetArchitecture {
    param([string] $Triple)

    $architecture = (($Triple ?? "") -split '-', 2)[0].ToLowerInvariant()
    switch -Regex ($architecture) {
        '^(aarch64|arm64)$' { return "arm64" }
        '^(x86_64|amd64)$' { return "x86_64" }
        '^i[3-6]86$' { return "x86" }
        default { return $architecture }
    }
}

function Get-NormalizedTargetOperatingSystem {
    param([string] $Triple)

    $normalized = ($Triple ?? "").ToLowerInvariant()
    if ($normalized -match '(windows|win32|mingw|msvc)') { return "windows" }
    if ($normalized -match '(darwin|macos|macosx)') { return "macos" }
    if ($normalized -match 'linux') { return "linux" }
    if ($normalized -match 'freebsd') { return "freebsd" }
    if ($normalized -match 'android') { return "android" }
    if ($normalized -match 'wasi') { return "wasi" }
    return "unknown"
}

function Get-NormalizedTargetAbi {
    param([string] $Triple)

    $normalized = ($Triple ?? "").ToLowerInvariant()
    if ($normalized -match '(darwin|macos|macosx)') { return "darwin" }
    if ($normalized -match 'msvc') { return "msvc" }
    if ($normalized -match 'musl') { return "musl" }
    if ($normalized -match 'gnu') { return "gnu" }
    if ($normalized -match 'android') { return "android" }
    if ($normalized -match 'wasi') { return "wasi" }
    return Get-NormalizedTargetOperatingSystem -Triple $Triple
}

function Get-MinimumOperatingSystemVersion {
    param([string] $Triple)

    if (($Triple ?? "") -match '(?i)(?:macosx|macos|ios|tvos|watchos)(?<version>[0-9]+(?:\.[0-9]+){0,2})') {
        return $Matches['version']
    }

    return $null
}

function Get-TargetPointerBitWidth {
    param([object] $Target)

    $cDataModel = Get-JsonPropertyValue -InputObject $Target -Name "CDataModel"
    $pointerBitWidth = Get-JsonPropertyValue -InputObject $cDataModel -Name "PointerBitWidth"
    if ($null -ne $pointerBitWidth -and [int]$pointerBitWidth -gt 0) {
        return [int]$pointerBitWidth
    }

    $aggregateLayout = Get-JsonPropertyValue -InputObject $Target -Name "AggregateLayout"
    $pointerSizeBytes = Get-JsonPropertyValue -InputObject $aggregateLayout -Name "PointerSizeBytes"
    if ($null -ne $pointerSizeBytes -and [int]$pointerSizeBytes -gt 0) {
        return [int]$pointerSizeBytes * 8
    }

    $architecture = Get-NormalizedTargetArchitecture -Triple ([string](Get-JsonPropertyValue -InputObject $Target -Name "Triple"))
    if ($architecture -in @("arm64", "x86_64", "s390x", "powerpc64", "riscv64", "wasm64")) {
        return 64
    }

    return 32
}

function Get-TargetEndianness {
    param([object] $Target)

    $dataLayout = [string](Get-JsonPropertyValue -InputObject $Target -Name "DataLayout")
    if ($dataLayout.StartsWith("E", [System.StringComparison]::Ordinal)) {
        return "big"
    }

    if ($dataLayout.StartsWith("e", [System.StringComparison]::Ordinal)) {
        return "little"
    }

    $architecture = Get-NormalizedTargetArchitecture -Triple ([string](Get-JsonPropertyValue -InputObject $Target -Name "Triple"))
    if ($architecture -in @("s390", "s390x", "powerpc", "powerpc64", "mips", "mips64", "sparc", "sparcv9")) {
        return "big"
    }

    return "little"
}

function Get-ValidatedTargetFeatures {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Target,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $features = [System.Collections.Generic.List[string]]::new()
    $states = [System.Collections.Generic.Dictionary[string, bool]]::new([System.StringComparer]::Ordinal)
    foreach ($value in (Get-JsonArray -InputObject $Target -Name "Features")) {
        $feature = [string]$value
        if ([string]::IsNullOrWhiteSpace($feature)) {
            throw "$Label target feature switches must not be empty."
        }

        $feature = $feature.Trim()
        $enabled = -not $feature.StartsWith("-", [System.StringComparison]::Ordinal)
        $name = if ($feature.StartsWith("+", [System.StringComparison]::Ordinal) `
            -or $feature.StartsWith("-", [System.StringComparison]::Ordinal)) {
            $feature.Substring(1)
        }
        else {
            $feature
        }
        if ([string]::IsNullOrWhiteSpace($name) -or $name -match '\s') {
            throw "$Label target feature switch '$feature' does not contain a valid feature name."
        }

        $name = $name.ToLowerInvariant()
        $previousState = $false
        if ($states.TryGetValue($name, [ref]$previousState)) {
            if ($previousState -ne $enabled) {
                throw "$Label target feature '$name' has conflicting enable and disable switches."
            }

            throw "$Label target feature '$name' is declared more than once."
        }

        $states.Add($name, $enabled)
        $features.Add($feature)
    }

    return $features.ToArray()
}

function Get-EnabledTargetFeatureNames {
    param([string[]] $Features)

    foreach ($feature in $Features) {
        if ($feature.StartsWith("-", [System.StringComparison]::Ordinal)) {
            continue
        }

        $name = if ($feature.StartsWith("+", [System.StringComparison]::Ordinal)) {
            $feature.Substring(1)
        }
        else {
            $feature
        }
        $name.ToLowerInvariant()
    }
}

function Test-PackageTargetCompatibility {
    param(
        [Parameter(Mandatory = $true)]
        [object] $ExpectedTarget,
        [Parameter(Mandatory = $true)]
        [object] $CandidateTarget,
        [ref] $Reason
    )

    $expectedTriple = [string](Get-JsonPropertyValue -InputObject $ExpectedTarget -Name "Triple")
    $candidateTriple = [string](Get-JsonPropertyValue -InputObject $CandidateTarget -Name "Triple")
    if ([string]::IsNullOrWhiteSpace($candidateTriple)) {
        $Reason.Value = "package image does not contain a target triple"
        return $false
    }

    $expectedFacts = @(
        @("architecture", (Get-NormalizedTargetArchitecture -Triple $expectedTriple), (Get-NormalizedTargetArchitecture -Triple $candidateTriple)),
        @("operating system", (Get-NormalizedTargetOperatingSystem -Triple $expectedTriple), (Get-NormalizedTargetOperatingSystem -Triple $candidateTriple)),
        @("ABI", (Get-NormalizedTargetAbi -Triple $expectedTriple), (Get-NormalizedTargetAbi -Triple $candidateTriple)),
        @("pointer width", (Get-TargetPointerBitWidth -Target $ExpectedTarget), (Get-TargetPointerBitWidth -Target $CandidateTarget))
    )
    foreach ($fact in $expectedFacts) {
        if ([string]$fact[1] -cne [string]$fact[2]) {
            $Reason.Value = "$($fact[0]) mismatch ('$($fact[2])' versus '$($fact[1])')"
            return $false
        }
    }

    $expectedCDataModel = Get-JsonPropertyValue -InputObject (Get-JsonPropertyValue -InputObject $ExpectedTarget -Name "CDataModel") -Name "Kind"
    $candidateCDataModel = Get-JsonPropertyValue -InputObject (Get-JsonPropertyValue -InputObject $CandidateTarget -Name "CDataModel") -Name "Kind"
    if (-not [string]::IsNullOrWhiteSpace([string]$expectedCDataModel) `
        -and -not [string]::IsNullOrWhiteSpace([string]$candidateCDataModel) `
        -and -not [string]::Equals([string]$expectedCDataModel, [string]$candidateCDataModel, [System.StringComparison]::OrdinalIgnoreCase)) {
        $Reason.Value = "C data model mismatch ('$candidateCDataModel' versus '$expectedCDataModel')"
        return $false
    }

    $expectedMinimum = Get-MinimumOperatingSystemVersion -Triple $expectedTriple
    $candidateMinimum = Get-MinimumOperatingSystemVersion -Triple $candidateTriple
    if ($null -ne $candidateMinimum `
        -and ($null -eq $expectedMinimum -or -not [string]::Equals($expectedMinimum, $candidateMinimum, [System.StringComparison]::Ordinal))) {
        $Reason.Value = "deployment minimum mismatch ('$candidateMinimum' versus '$($expectedMinimum ?? '<unspecified>')')"
        return $false
    }

    $expectedFeatures = @(Get-ValidatedTargetFeatures -Target $ExpectedTarget -Label "SDK baseline package")
    $candidateFeatures = @(Get-ValidatedTargetFeatures -Target $CandidateTarget -Label "Candidate package")
    $expectedEnabledFeatures = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($feature in (Get-EnabledTargetFeatureNames -Features $expectedFeatures)) {
        [void]$expectedEnabledFeatures.Add($feature)
    }

    foreach ($feature in (Get-EnabledTargetFeatureNames -Features $candidateFeatures)) {
        if (-not $expectedEnabledFeatures.Contains($feature)) {
            $Reason.Value = "package requires target feature '$feature' outside the SDK baseline"
            return $false
        }
    }

    $Reason.Value = ""
    return $true
}

function Get-PackageModuleNames {
    param([object] $Inspection)

    $moduleNames = @(Get-JsonArray -InputObject $Inspection -Name "Modules" `
        | ForEach-Object { [string](Get-JsonPropertyValue -InputObject $_ -Name "ModuleName") } `
        | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return @(Get-OrdinalSortedUniqueStrings -Values $moduleNames)
}

function Get-PackageImportedModuleNames {
    param([object] $Inspection)

    $imports = @()
    foreach ($module in (Get-JsonArray -InputObject $Inspection -Name "Modules")) {
        $sourceSurface = Get-JsonPropertyValue -InputObject $module -Name "SourceSurface"
        $moduleImports = @(Get-JsonArray -InputObject $sourceSurface -Name "Imports")
        if ($moduleImports.Count -eq 0) {
            $moduleImports = @(Get-JsonArray -InputObject $module -Name "Imports")
        }

        foreach ($import in $moduleImports) {
            $moduleName = [string](Get-JsonPropertyValue -InputObject $import -Name "ModuleName")
            if (-not [string]::IsNullOrWhiteSpace($moduleName)) {
                $imports += $moduleName
            }
        }
    }

    return @(Get-OrdinalSortedUniqueStrings -Values $imports)
}

function Get-PackageNativeDescriptor {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SdkRoot,
        [Parameter(Mandatory = $true)]
        [string] $PackageDirectory,
        [object] $NativeDependencies,
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $artifacts = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    $includeDirectories = [System.Collections.Generic.List[string]]::new()
    $includeDirectorySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $libraryDirectories = [System.Collections.Generic.List[string]]::new()
    $libraryDirectorySet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $runtimeFiles = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    $licenseFiles = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)

    $pkgConfigPackages = @(Get-JsonArray -InputObject $NativeDependencies -Name "PkgConfigPackages")
    if ($pkgConfigPackages.Count -ne 0) {
        throw "package '$PackageId' retains unresolved pkg-config dependencies: $($pkgConfigPackages -join ', ')"
    }

    foreach ($source in (Get-JsonArray -InputObject $NativeDependencies -Name "Sources")) {
        $resolvedSource = Resolve-StagedPackagePath `
            -SdkRoot $SdkRoot `
            -PackageDirectory $PackageDirectory `
            -Value ([string]$source) `
            -Label "package '$PackageId' native source"
        $artifacts.Add((ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $resolvedSource -Label "package '$PackageId' native source")) | Out-Null
    }

    $resolvedLibraryDirectories = @()
    foreach ($includeDirectory in (Get-JsonArray -InputObject $NativeDependencies -Name "IncludeDirectories")) {
        $resolvedDirectory = Resolve-StagedPackagePath `
            -SdkRoot $SdkRoot `
            -PackageDirectory $PackageDirectory `
            -Value ([string]$includeDirectory) `
            -Label "package '$PackageId' native include directory" `
            -PathType Container
        $relativeDirectory = ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $resolvedDirectory -Label "package '$PackageId' native include directory"
        if (-not $includeDirectorySet.Add($relativeDirectory)) {
            throw "package '$PackageId' contains duplicate native include directory '$relativeDirectory'"
        }
        $includeDirectories.Add($relativeDirectory)
        $includeFilePaths = @(Get-OrdinalSortedStrings -Values @(
            Get-ChildItem -LiteralPath $resolvedDirectory -File -Recurse | ForEach-Object { $_.FullName }
        ))
        foreach ($includeFilePath in $includeFilePaths) {
            $includeFile = Get-Item -LiteralPath $includeFilePath
            $artifacts.Add((ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $includeFile.FullName -Label "package '$PackageId' native include file")) | Out-Null
        }
    }

    foreach ($libraryDirectory in (Get-JsonArray -InputObject $NativeDependencies -Name "LibraryDirectories")) {
        $resolvedDirectory = Resolve-StagedPackagePath `
            -SdkRoot $SdkRoot `
            -PackageDirectory $PackageDirectory `
            -Value ([string]$libraryDirectory) `
            -Label "package '$PackageId' native library directory" `
            -PathType Container
        $resolvedLibraryDirectories += $resolvedDirectory
        $relativeDirectory = ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $resolvedDirectory -Label "package '$PackageId' native library directory"
        if (-not $libraryDirectorySet.Add($relativeDirectory)) {
            throw "package '$PackageId' contains duplicate native library directory '$relativeDirectory'"
        }
        $libraryDirectories.Add($relativeDirectory)
    }

    $libraries = @(Get-JsonArray -InputObject $NativeDependencies -Name "Libraries" | ForEach-Object { [string]$_ })
    foreach ($library in $libraries) {
        if ([string]::IsNullOrWhiteSpace($library) `
            -or $library -notmatch '^[A-Za-z0-9][A-Za-z0-9_.+\-]*$' `
            -or [System.IO.Path]::IsPathRooted($library) `
            -or $library.Contains('/', [StringComparison]::Ordinal) `
            -or $library.Contains('\', [StringComparison]::Ordinal) `
            -or $library.Contains('..', [StringComparison]::Ordinal)) {
            throw "package '$PackageId' contains non-relocatable native library value '$library'; release metadata must use logical linker names"
        }
    }

    $packagedNativeLibraries = @()
    foreach ($resolvedDirectory in $resolvedLibraryDirectories) {
        $libraryFilePaths = @(Get-OrdinalSortedStrings -Values @(
            Get-ChildItem -LiteralPath $resolvedDirectory -File -Recurse | ForEach-Object { $_.FullName }
        ))
        foreach ($libraryFilePath in $libraryFilePaths) {
            $file = Get-Item -LiteralPath $libraryFilePath
            foreach ($library in $libraries) {
                $escapedLibrary = [System.Text.RegularExpressions.Regex]::Escape($library)
                if ($file.Name -match "(?i)^(?:lib)?$escapedLibrary(?:\.(?:a|lib|dylib|dll)|\.so(?:\..+)?)$") {
                    if ($file.Extension -in @(".a", ".lib")) {
                        Assert-StaticLibraryArchive -Path $file.FullName -Label "package '$PackageId' native library"
                    } elseif ($file.Length -eq 0) {
                        throw "package '$PackageId' native library '$($file.FullName)' is empty."
                    }

                    $packagedNativeLibraries += $file.FullName
                    $artifacts.Add((ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $file.FullName -Label "package '$PackageId' native library")) | Out-Null
                    if ($file.Name -match '(?i)\.(?:dll|dylib)$' `
                        -or $file.Name -match '(?i)\.so(?:\..+)?$') {
                        $runtimeFiles.Add((ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $file.FullName -Label "package '$PackageId' runtime library")) | Out-Null
                    }
                }
            }

            if ($file.Name -match '(?i)^(LICENSE|LICENCE|COPYING|NOTICE)(\..*)?$') {
                $licenseFiles.Add((ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $file.FullName -Label "package '$PackageId' native license")) | Out-Null
            }
        }
    }

    if ($resolvedLibraryDirectories.Count -ne 0 `
        -and $libraries.Count -ne 0 `
        -and $packagedNativeLibraries.Count -eq 0) {
        throw "package '$PackageId' declares SDK-local native library directories, but none contains an archive/runtime file for its declared libraries ($($libraries -join ', '))"
    }

    $linkArguments = @(Get-JsonArray -InputObject $NativeDependencies -Name "LinkArguments" | ForEach-Object { [string]$_ })
    foreach ($argument in $linkArguments) {
        if ([System.IO.Path]::IsPathRooted($argument) `
            -or $argument -match '(?i)^-(?:L|F)[/\\]' `
            -or $argument -match '(?i)(?:^|[,=])(?:/[A-Za-z0-9_.-]|[A-Za-z]:[/\\])' `
            -or $argument.Contains('@', [StringComparison]::Ordinal) `
            -or $argument.Contains('..', [StringComparison]::Ordinal)) {
            throw "package '$PackageId' contains machine-local native link argument '$argument'"
        }
    }

    $checksumPaths = @(Get-OrdinalSortedUniqueStrings -Values @(
        @($artifacts) + @($runtimeFiles) + @($licenseFiles)
    ))
    $fileChecksums = @($checksumPaths | ForEach-Object {
        $relativePath = [string]$_
        $absolutePath = Join-Path $SdkRoot ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
            throw "package '$PackageId' native checksum input '$absolutePath' is missing or is not a file"
        }

        [ordered]@{
            path = $relativePath
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $absolutePath).Hash.ToLowerInvariant()
        }
    })

    return [ordered]@{
        artifacts = [object[]]@($artifacts)
        includeDirectories = [object[]]@($includeDirectories)
        libraryDirectories = [object[]]@($libraryDirectories)
        runtimeFiles = [object[]]@($runtimeFiles)
        licenseFiles = [object[]]@($licenseFiles)
        fileChecksums = [object[]]$fileChecksums
        pkgConfigPackages = [object[]]$pkgConfigPackages
        libraries = [object[]]$libraries
        linkArguments = [object[]]$linkArguments
    }
}

function New-StagedPackageCandidate {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SdkRoot,
        [Parameter(Mandatory = $true)]
        [string] $CompilerPath,
        [Parameter(Mandatory = $true)]
        [string] $ImagePath,
        [Parameter(Mandatory = $true)]
        [object] $ExpectedTarget,
        [switch] $IsRequired
    )

    $inspection = Invoke-StagedPackageInspection -CompilerPath $CompilerPath -ImagePath $ImagePath
    $packageId = [string](Get-JsonPropertyValue -InputObject $inspection -Name "RootModule")
    if ([string]::IsNullOrWhiteSpace($packageId)) {
        throw "Package image '$ImagePath' has no root module/package identity."
    }

    $identity = Get-JsonPropertyValue -InputObject $inspection -Name "Identity"
    if ($null -eq $identity) {
        throw "package '$packageId' does not contain explicit API/content identity facts"
    }

    $identityPackageId = [string](Get-JsonPropertyValue -InputObject $identity -Name "PackageId")
    $apiHash = [string](Get-JsonPropertyValue -InputObject $identity -Name "ApiHash")
    $contentHash = [string](Get-JsonPropertyValue -InputObject $identity -Name "ContentHash")
    if ($identityPackageId -cne $packageId) {
        throw "package '$packageId' identity names package '$identityPackageId'"
    }

    if ($apiHash -cnotmatch '^[0-9a-f]{64}$' -or $contentHash -cnotmatch '^[0-9a-f]{64}$') {
        throw "package '$packageId' identity does not contain lowercase SHA-256 API/content hashes"
    }

    $identityDependencies = @(Get-JsonArray -InputObject $identity -Name "Dependencies")
    $dependencyIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $dependencies = @()
    foreach ($dependency in $identityDependencies) {
        $dependencyId = [string](Get-JsonPropertyValue -InputObject $dependency -Name "PackageId")
        $dependencyApiHash = [string](Get-JsonPropertyValue -InputObject $dependency -Name "ApiHash")
        $dependencyContentHash = [string](Get-JsonPropertyValue -InputObject $dependency -Name "ContentHash")
        if ([string]::IsNullOrWhiteSpace($dependencyId) `
            -or $dependencyId -ceq $packageId `
            -or -not $dependencyIds.Add($dependencyId) `
            -or $dependencyApiHash -cnotmatch '^[0-9a-f]{64}$' `
            -or $dependencyContentHash -cnotmatch '^[0-9a-f]{64}$') {
            throw "package '$packageId' contains an invalid, duplicate, self-referential, or unhashed dependency identity '$dependencyId'"
        }

        $dependencies += [pscustomobject]@{
            Id = $dependencyId
            ApiHash = $dependencyApiHash
            ContentHash = $dependencyContentHash
        }
    }

    $orderedDependencies = @()
    foreach ($dependencyId in (Get-OrdinalSortedStrings -Values @($dependencies | ForEach-Object { $_.Id }))) {
        $orderedDependencies += @($dependencies | Where-Object { $_.Id -ceq $dependencyId })[0]
    }
    $dependencies = @($orderedDependencies)

    $candidateTarget = Get-JsonPropertyValue -InputObject $inspection -Name "Target"
    $targetReason = ""
    if ($null -eq $candidateTarget) {
        throw "package '$packageId' is not compatible with this release target: package image does not contain structured target facts"
    }

    $candidateDataLayout = [string](Get-JsonPropertyValue -InputObject $candidateTarget -Name "DataLayout")
    if ([string]::IsNullOrWhiteSpace($candidateDataLayout)) {
        throw "package '$packageId' is not compatible with this release target: package image does not contain a nonempty LLVM data layout"
    }

    if (-not (Test-PackageTargetCompatibility -ExpectedTarget $ExpectedTarget -CandidateTarget $candidateTarget -Reason ([ref]$targetReason))) {
        if ([string]::IsNullOrWhiteSpace($targetReason)) {
            $targetReason = "structured target facts do not match"
        }

        throw "package '$packageId' is not compatible with this release target: $targetReason"
    }

    $packageDirectory = Split-Path -Parent $ImagePath
    $libraryFileName = [string](Get-JsonPropertyValue -InputObject $inspection -Name "LibraryFileName")
    $libraryPath = Resolve-StagedPackagePath `
        -SdkRoot $SdkRoot `
        -PackageDirectory $packageDirectory `
        -Value $libraryFileName `
        -Label "package '$packageId' declared archive"
    Assert-StaticLibraryArchive -Path $libraryPath -Label "package '$packageId' declared archive"
    $native = Get-PackageNativeDescriptor `
        -SdkRoot $SdkRoot `
        -PackageDirectory $packageDirectory `
        -NativeDependencies (Get-JsonPropertyValue -InputObject $inspection -Name "NativeDependencies") `
        -PackageId $packageId

    $buildProfile = Get-JsonPropertyValue -InputObject $inspection -Name "BuildProfile"
    if ($null -eq $buildProfile) {
        throw "package '$packageId' does not declare BuildProfile facts; release SDK packages must be explicitly built with --package-profile release"
    }

    $profile = [string](Get-JsonPropertyValue -InputObject $buildProfile -Name "Name")
    if ([string]::IsNullOrWhiteSpace($profile)) {
        throw "package '$packageId' declares an empty BuildProfile; release SDK packages must be explicitly built with --package-profile release"
    }

    if (-not $profile.Equals("release", [System.StringComparison]::Ordinal)) {
        throw "package '$packageId' was built for profile '$profile'; release SDK assembly requires release-built packages"
    }

    return [pscustomobject]@{
        Id = $packageId
        Version = $Version
        Profile = $profile
        ImagePath = [System.IO.Path]::GetFullPath($ImagePath)
        ImageRelativePath = ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $ImagePath -Label "package '$packageId' image"
        LibraryPath = $libraryPath
        LibraryRelativePath = ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $libraryPath -Label "package '$packageId' archive"
        PackageFormatVersion = Get-PackageFormatVersion -ImagePath $ImagePath
        Inspection = $inspection
        Target = $candidateTarget
        ApiHash = $apiHash
        ContentHash = $contentHash
        Dependencies = $dependencies
        Modules = @(Get-PackageModuleNames -Inspection $inspection)
        ImportedModules = @(Get-PackageImportedModuleNames -Inspection $inspection)
        Native = $native
        IsRequired = [bool]$IsRequired
    }
}

function Resolve-VendorManifestFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $VendorRoot,
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or $Path -cne $Path.Trim() `
        -or [System.IO.Path]::IsPathRooted($Path) `
        -or $Path.Contains('\', [System.StringComparison]::Ordinal)) {
        throw "$Label path '$Path' must be a nonempty, canonical, forward-slash vendor-root-relative path."
    }

    $segments = @($Path.Split('/'))
    if ($segments.Count -eq 0 `
        -or @($segments | Where-Object { $_ -in @("", ".", "..") }).Count -ne 0) {
        throw "$Label path '$Path' contains an empty or traversal segment."
    }

    $canonicalRoot = [System.IO.Path]::GetFullPath($VendorRoot)
    $absolutePath = [System.IO.Path]::GetFullPath((Join-Path $canonicalRoot $Path))
    $relativePath = [System.IO.Path]::GetRelativePath($canonicalRoot, $absolutePath).Replace('\', '/')
    if ($relativePath -cne $Path `
        -or [System.IO.Path]::IsPathRooted($relativePath) `
        -or $relativePath -eq ".." `
        -or $relativePath.StartsWith("../", [System.StringComparison]::Ordinal)) {
        throw "$Label path '$Path' is not a canonical path inside vendor root '$canonicalRoot'."
    }

    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        throw "$Label path '$Path' is missing from vendor root '$canonicalRoot'."
    }

    $item = Get-Item -LiteralPath $absolutePath -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label path '$Path' is a reparse point; release inputs must contain regular files."
    }

    return $absolutePath
}

function Get-VendorRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $VendorRoot,
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $canonicalRoot = [System.IO.Path]::GetFullPath($VendorRoot)
    $canonicalPath = [System.IO.Path]::GetFullPath($Path)
    $relativePath = [System.IO.Path]::GetRelativePath($canonicalRoot, $canonicalPath).Replace('\', '/')
    if ([System.IO.Path]::IsPathRooted($relativePath) `
        -or $relativePath -eq ".." `
        -or $relativePath.StartsWith("../", [System.StringComparison]::Ordinal)) {
        throw "$Label '$canonicalPath' is outside vendor root '$canonicalRoot'."
    }

    return $relativePath
}

function Get-ValidatedVendorFileEntry {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Entry,
        [Parameter(Mandatory = $true)]
        [string] $VendorRoot,
        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    Assert-ExactJsonProperties -InputObject $Entry -Names @("path", "bytes", "sha256") -Label $Label
    $path = Get-RequiredJsonString -InputObject $Entry -Name "path" -Label $Label
    $sha256 = Get-RequiredJsonString -InputObject $Entry -Name "sha256" -Label $Label
    if ($sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw "$Label SHA-256 '$sha256' must be 64 lowercase hexadecimal characters."
    }

    $bytesValue = Get-RequiredJsonPropertyValue -InputObject $Entry -Name "bytes" -Label $Label
    if ($bytesValue -isnot [byte] `
        -and $bytesValue -isnot [uint16] `
        -and $bytesValue -isnot [uint32] `
        -and $bytesValue -isnot [uint64] `
        -and $bytesValue -isnot [sbyte] `
        -and $bytesValue -isnot [int16] `
        -and $bytesValue -isnot [int32] `
        -and $bytesValue -isnot [int64]) {
        throw "$Label property 'bytes' must be a nonnegative JSON integer."
    }

    $bytes = [int64]$bytesValue
    if ($bytes -lt 0) {
        throw "$Label property 'bytes' must be nonnegative."
    }

    $absolutePath = Resolve-VendorManifestFile -VendorRoot $VendorRoot -Path $path -Label $Label
    $file = Get-Item -LiteralPath $absolutePath -Force
    if ($file.Length -ne $bytes) {
        throw "$Label '$path' has $($file.Length) bytes, not declared size $bytes."
    }

    $actualSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $absolutePath).Hash.ToLowerInvariant()
    if ($actualSha256 -cne $sha256) {
        throw "$Label '$path' failed SHA-256 validation."
    }

    return [pscustomobject]@{
        Path = $path
        AbsolutePath = $absolutePath
        Bytes = $bytes
        Sha256 = $sha256
    }
}

function Get-ReferencedVendorFile {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.Dictionary[string, object]] $FilesByPath,
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [string] $Label,
        [string] $Sha256 = "",
        [Nullable[int64]] $Bytes = $null
    )

    if (-not $FilesByPath.ContainsKey($Path)) {
        throw "$Label path '$Path' is absent from the schema-2 release-input files inventory."
    }

    $file = $FilesByPath[$Path]
    if (-not [string]::IsNullOrWhiteSpace($Sha256) -and $file.Sha256 -cne $Sha256) {
        throw "$Label path '$Path' SHA-256 does not match the files inventory."
    }

    if ($null -ne $Bytes -and $file.Bytes -ne [int64]$Bytes) {
        throw "$Label path '$Path' byte count does not match the files inventory."
    }

    return $file
}

function Read-VendorReleaseInput {
    param(
        [Parameter(Mandatory = $true)]
        [string] $VendorDist,
        [Parameter(Mandatory = $true)]
        [string] $AssetSuffix,
        [Parameter(Mandatory = $true)]
        [string] $ExpectedTargetTriple
    )

    if (-not (Test-Path -LiteralPath $VendorDist -PathType Container)) {
        throw "Release SDK assembly requires vendor staging directory '$VendorDist'."
    }

    $vendorRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $VendorDist))
    $releaseInputPath = Join-Path $vendorRoot "release-input.json"
    if (-not (Test-Path -LiteralPath $releaseInputPath -PathType Leaf)) {
        throw "Release SDK assembly requires schema-2 Vendor manifest '$releaseInputPath'."
    }

    try {
        $releaseInput = Get-Content -LiteralPath $releaseInputPath -Raw | ConvertFrom-Json -Depth 100
        Assert-ExactJsonProperties `
            -InputObject $releaseInput `
            -Names @("schemaVersion", "manifestKind", "state", "target", "catalog", "packages", "files") `
            -Label "Vendor release-input manifest"
        $schemaVersion = Get-RequiredJsonPropertyValue -InputObject $releaseInput -Name "schemaVersion" -Label "Vendor release-input manifest"
        if (($schemaVersion -isnot [int] -and $schemaVersion -isnot [long]) `
            -or [int64]$schemaVersion -ne 2) {
            throw "Vendor release-input manifest schemaVersion must be integer 2."
        }

        if ((Get-RequiredJsonString -InputObject $releaseInput -Name "manifestKind" -Label "Vendor release-input manifest") -cne "stark-vendor-release-input") {
            throw "Vendor release-input manifestKind must be 'stark-vendor-release-input'."
        }

        if ((Get-RequiredJsonString -InputObject $releaseInput -Name "state" -Label "Vendor release-input manifest") -cne "ready") {
            throw "Vendor release-input manifest must be in the fail-closed 'ready' state."
        }

        $target = Get-RequiredJsonPropertyValue -InputObject $releaseInput -Name "target" -Label "Vendor release-input manifest"
        Assert-ExactJsonProperties `
            -InputObject $target `
            -Names @("id", "assetSuffix", "runtimeIdentifier", "targetTriple", "operatingSystem", "architecture") `
            -Label "Vendor release-input target"
        $targetId = Get-RequiredJsonString -InputObject $target -Name "id" -Label "Vendor release-input target"
        $targetAssetSuffix = Get-RequiredJsonString -InputObject $target -Name "assetSuffix" -Label "Vendor release-input target"
        $runtimeIdentifier = Get-RequiredJsonString -InputObject $target -Name "runtimeIdentifier" -Label "Vendor release-input target"
        $targetTriple = Get-RequiredJsonString -InputObject $target -Name "targetTriple" -Label "Vendor release-input target"
        $targetOperatingSystem = Get-RequiredJsonString -InputObject $target -Name "operatingSystem" -Label "Vendor release-input target"
        $targetArchitecture = Get-RequiredJsonString -InputObject $target -Name "architecture" -Label "Vendor release-input target"
        if ($targetId -cne $AssetSuffix -or $targetAssetSuffix -cne $AssetSuffix) {
            throw "Vendor release-input target id/assetSuffix '$targetId'/'$targetAssetSuffix' do not match release asset '$AssetSuffix'."
        }

        if ($targetTriple -cne $ExpectedTargetTriple) {
            throw "Vendor release-input target '$targetTriple' does not exactly match staged System package target '$ExpectedTargetTriple'."
        }

        $expectedOperatingSystem = Get-NormalizedTargetOperatingSystem -Triple $ExpectedTargetTriple
        $normalizedTargetArchitecture = Get-NormalizedTargetArchitecture -Triple $ExpectedTargetTriple
        $expectedArchitecture = if ($normalizedTargetArchitecture -ceq "x86_64") {
            "x64"
        } else {
            $normalizedTargetArchitecture
        }
        if ($targetOperatingSystem -cne $expectedOperatingSystem `
            -or $targetArchitecture -cne $expectedArchitecture) {
            throw "Vendor release-input target OS/architecture '$targetOperatingSystem/$targetArchitecture' do not match '$expectedOperatingSystem/$expectedArchitecture'."
        }

        $expectedRuntimeIdentifier = switch ($AssetSuffix) {
            "linux-x64" { "linux-x64" }
            "linux-arm64" { "linux-arm64" }
            "windows-x64" { "win-x64" }
            "windows-arm64" { "win-arm64" }
            "macos-x64" { "osx-x64" }
            "macos-arm64" { "osx-arm64" }
            default { throw "Release asset '$AssetSuffix' has no supported runtime identifier mapping." }
        }
        if ($runtimeIdentifier -cne $expectedRuntimeIdentifier) {
            throw "Vendor release-input runtime identifier '$runtimeIdentifier' does not match release asset '$AssetSuffix' expected runtime '$expectedRuntimeIdentifier'."
        }

        $filesByPath = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
        $caseInsensitivePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $previousFilePath = $null
        $fileEntries = Get-RequiredJsonArray -InputObject $releaseInput -Name "files" -Label "Vendor release-input manifest"
        if ($fileEntries.Count -eq 0) {
            throw "Vendor release-input files inventory must not be empty."
        }

        foreach ($entry in $fileEntries) {
            $file = Get-ValidatedVendorFileEntry -Entry $entry -VendorRoot $vendorRoot -Label "Vendor release-input file"
            if ($file.Path -ceq "release-input.json") {
                throw "Vendor release-input files inventory must exclude release-input.json because it cannot hash itself."
            }

            if ($null -ne $previousFilePath `
                -and [System.StringComparer]::Ordinal.Compare($previousFilePath, $file.Path) -ge 0) {
                throw "Vendor release-input files inventory must be strictly ordinal-sorted and duplicate-free."
            }

            if (-not $caseInsensitivePaths.Add($file.Path) -or $filesByPath.ContainsKey($file.Path)) {
                throw "Vendor release-input files inventory contains duplicate or case-colliding path '$($file.Path)'."
            }

            $filesByPath.Add($file.Path, $file)
            $previousFilePath = $file.Path
        }

        foreach ($item in (Get-ChildItem -LiteralPath $vendorRoot -Recurse -Force)) {
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $relativeItemPath = Get-VendorRelativePath -VendorRoot $vendorRoot -Path $item.FullName -Label "Vendor payload entry"
                throw "Vendor payload entry '$relativeItemPath' is a reparse point; release inputs must be self-contained."
            }
        }

        $actualFilePaths = @(Get-ChildItem -LiteralPath $vendorRoot -File -Recurse -Force `
            | Where-Object { -not [string]::Equals($_.FullName, $releaseInputPath, [System.StringComparison]::OrdinalIgnoreCase) } `
            | ForEach-Object { Get-VendorRelativePath -VendorRoot $vendorRoot -Path $_.FullName -Label "Vendor payload file" })
        $actualFiles = @(Get-OrdinalSortedStrings -Values $actualFilePaths)
        $declaredFiles = @(Get-OrdinalSortedStrings -Values @($filesByPath.Keys))
        if (($actualFiles -join "`n") -cne ($declaredFiles -join "`n")) {
            throw "Vendor release-input files inventory does not exactly match the staged vendor file set."
        }

        $catalog = Get-RequiredJsonPropertyValue -InputObject $releaseInput -Name "catalog" -Label "Vendor release-input manifest"
        Assert-ExactJsonProperties -InputObject $catalog -Names @("id", "path", "sha256") -Label "Vendor release-input catalog"
        $catalogId = Get-RequiredJsonString -InputObject $catalog -Name "id" -Label "Vendor release-input catalog"
        $catalogPath = Get-RequiredJsonString -InputObject $catalog -Name "path" -Label "Vendor release-input catalog"
        $catalogSha256 = Get-RequiredJsonString -InputObject $catalog -Name "sha256" -Label "Vendor release-input catalog"
        if ($catalogId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $catalogSha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw "Vendor release-input catalog identity or SHA-256 is invalid."
        }

        [void](Get-ReferencedVendorFile -FilesByPath $filesByPath -Path $catalogPath -Sha256 $catalogSha256 -Label "Vendor catalog")

        $packageEntries = Get-RequiredJsonArray -InputObject $releaseInput -Name "packages" -Label "Vendor release-input manifest"
        if ($packageEntries.Count -eq 0) {
            throw "Vendor release-input packages array must not be empty."
        }

        $packages = @()
        $packagesById = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
        $caseInsensitivePackageIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $ownedPayloadPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $allowedArtifactKinds = [System.Collections.Generic.HashSet[string]]::new(
            [string[]]@("header", "static-library", "runtime-library", "native-source", "documentation", "license", "provenance"),
            [System.StringComparer]::Ordinal)
        $previousPackageId = $null
        foreach ($entry in $packageEntries) {
            Assert-ExactJsonProperties `
                -InputObject $entry `
                -Names @("id", "version", "sourceIdentity", "target", "package", "nativePayload", "provenance") `
                -Label "Vendor release-input package"
            $packageId = Get-RequiredJsonString -InputObject $entry -Name "id" -Label "Vendor release-input package"
            $packageVersion = Get-RequiredJsonString -InputObject $entry -Name "version" -Label "Vendor release-input package '$packageId'"
            $sourceIdentity = Get-RequiredJsonString -InputObject $entry -Name "sourceIdentity" -Label "Vendor release-input package '$packageId'"
            if ($packageId -cnotmatch '^Vendor(?:\.[A-Za-z0-9][A-Za-z0-9_]*)+$') {
                throw "Vendor release-input package ID '$packageId' is not a canonical official Vendor package ID."
            }

            if ($packageVersion -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*$' `
                -or $sourceIdentity.Contains("`n", [System.StringComparison]::Ordinal) `
                -or $sourceIdentity.Contains("`r", [System.StringComparison]::Ordinal)) {
                throw "Vendor release-input package '$packageId' has an invalid version or source identity."
            }

            if ($null -ne $previousPackageId `
                -and [System.StringComparer]::Ordinal.Compare($previousPackageId, $packageId) -ge 0) {
                throw "Vendor release-input packages must be strictly ordinal-sorted by ID and duplicate-free."
            }

            if (-not $caseInsensitivePackageIds.Add($packageId) -or $packagesById.ContainsKey($packageId)) {
                throw "Vendor release-input contains duplicate or case-colliding package ID '$packageId'."
            }

            $packageTarget = Get-RequiredJsonPropertyValue -InputObject $entry -Name "target" -Label "Vendor release-input package '$packageId'"
            Assert-ExactJsonProperties -InputObject $packageTarget -Names @("id", "targetTriple") -Label "Vendor release-input package '$packageId' target"
            $packageTargetId = Get-RequiredJsonString -InputObject $packageTarget -Name "id" -Label "Vendor release-input package '$packageId' target"
            $packageTargetTriple = Get-RequiredJsonString -InputObject $packageTarget -Name "targetTriple" -Label "Vendor release-input package '$packageId' target"
            if ($packageTargetId -cne $targetId -or $packageTargetTriple -cne $targetTriple) {
                throw "Vendor release-input package '$packageId' target does not exactly match the release-input target."
            }

            $package = Get-RequiredJsonPropertyValue -InputObject $entry -Name "package" -Label "Vendor release-input package '$packageId'"
            Assert-ExactJsonProperties `
                -InputObject $package `
                -Names @("rootModule", "image", "imageSha256", "library", "librarySha256", "modules") `
                -Label "Vendor release-input package '$packageId' package image"
            $rootModule = Get-RequiredJsonString -InputObject $package -Name "rootModule" -Label "Vendor release-input package '$packageId' package image"
            $imagePath = Get-RequiredJsonString -InputObject $package -Name "image" -Label "Vendor release-input package '$packageId' package image"
            $imageSha256 = Get-RequiredJsonString -InputObject $package -Name "imageSha256" -Label "Vendor release-input package '$packageId' package image"
            $libraryPath = Get-RequiredJsonString -InputObject $package -Name "library" -Label "Vendor release-input package '$packageId' package image"
            $librarySha256 = Get-RequiredJsonString -InputObject $package -Name "librarySha256" -Label "Vendor release-input package '$packageId' package image"
            if ($rootModule -cne $packageId) {
                throw "Vendor release-input package '$packageId' package root '$rootModule' does not match its ID."
            }

            if ($imageSha256 -cnotmatch '^[0-9a-f]{64}$' -or $librarySha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw "Vendor release-input package '$packageId' image/library SHA-256 is invalid."
            }

            $imageFile = Get-ReferencedVendorFile -FilesByPath $filesByPath -Path $imagePath -Sha256 $imageSha256 -Label "Vendor package '$packageId' image"
            $libraryFile = Get-ReferencedVendorFile -FilesByPath $filesByPath -Path $libraryPath -Sha256 $librarySha256 -Label "Vendor package '$packageId' library"
            if (-not $imagePath.EndsWith(".starkpkg", [System.StringComparison]::Ordinal) `
                -or (-not $libraryPath.EndsWith(".a", [System.StringComparison]::Ordinal) `
                    -and -not $libraryPath.EndsWith(".lib", [System.StringComparison]::Ordinal))) {
                throw "Vendor release-input package '$packageId' image/library file names are not supported release artifacts."
            }

            foreach ($payloadPath in @($imagePath, $libraryPath)) {
                if (-not $ownedPayloadPaths.Add($payloadPath)) {
                    throw "Vendor release-input payload path '$payloadPath' is owned by more than one package artifact."
                }
            }

            foreach ($packageArtifact in @($imageFile, $libraryFile)) {
                $distRelativePath = [System.IO.Path]::GetRelativePath(
                    [System.IO.Path]::GetFullPath($VendorDist),
                    [System.IO.Path]::GetFullPath($packageArtifact.AbsolutePath)).Replace('\', '/')
                if ([System.IO.Path]::IsPathRooted($distRelativePath) `
                    -or $distRelativePath -eq ".." `
                    -or $distRelativePath.StartsWith("../", [System.StringComparison]::Ordinal)) {
                    throw "Vendor release-input package '$packageId' artifact '$($packageArtifact.Path)' is outside vendor dist '$VendorDist'."
                }
            }

            $modules = Get-RequiredJsonArray -InputObject $package -Name "modules" -Label "Vendor release-input package '$packageId' package image"
            if ($modules.Count -eq 0) {
                throw "Vendor release-input package '$packageId' must declare at least one module."
            }

            Assert-SortedUniqueStrings -Values $modules -Label "Vendor release-input package '$packageId' modules"
            if (@($modules | Where-Object { [string]$_ -ceq $rootModule }).Count -ne 1) {
                throw "Vendor release-input package '$packageId' modules do not contain its root module exactly once."
            }

            $nativePayload = Get-RequiredJsonPropertyValue -InputObject $entry -Name "nativePayload" -Label "Vendor release-input package '$packageId'"
            Assert-ExactJsonProperties -InputObject $nativePayload -Names @("artifacts", "licenseFiles") -Label "Vendor release-input package '$packageId' native payload"
            $nativeArtifacts = @()
            $nativeArtifactPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $previousNativeArtifactPath = $null
            foreach ($nativeArtifact in (Get-RequiredJsonArray -InputObject $nativePayload -Name "artifacts" -Label "Vendor release-input package '$packageId' native payload")) {
                Assert-ExactJsonProperties -InputObject $nativeArtifact -Names @("kind", "path", "bytes", "sha256") -Label "Vendor release-input package '$packageId' native artifact"
                $kind = Get-RequiredJsonString -InputObject $nativeArtifact -Name "kind" -Label "Vendor release-input package '$packageId' native artifact"
                if (-not $allowedArtifactKinds.Contains($kind)) {
                    throw "Vendor release-input package '$packageId' native artifact kind '$kind' is unsupported."
                }

                $path = Get-RequiredJsonString -InputObject $nativeArtifact -Name "path" -Label "Vendor release-input package '$packageId' native artifact"
                $sha256 = Get-RequiredJsonString -InputObject $nativeArtifact -Name "sha256" -Label "Vendor release-input package '$packageId' native artifact"
                $bytesValue = Get-RequiredJsonPropertyValue -InputObject $nativeArtifact -Name "bytes" -Label "Vendor release-input package '$packageId' native artifact"
                if ($bytesValue -isnot [int] -and $bytesValue -isnot [long]) {
                    throw "Vendor release-input package '$packageId' native artifact '$path' bytes must be an integer."
                }

                $bytes = [int64]$bytesValue
                if ($bytes -lt 0 -or $sha256 -cnotmatch '^[0-9a-f]{64}$') {
                    throw "Vendor release-input package '$packageId' native artifact '$path' size or SHA-256 is invalid."
                }

                if ($null -ne $previousNativeArtifactPath `
                    -and [System.StringComparer]::Ordinal.Compare($previousNativeArtifactPath, $path) -ge 0) {
                    throw "Vendor release-input package '$packageId' native artifacts must be strictly ordinal-sorted by path."
                }

                if (-not $nativeArtifactPaths.Add($path) -or -not $ownedPayloadPaths.Add($path)) {
                    throw "Vendor release-input package '$packageId' native artifact path '$path' is duplicate, case-colliding, or multiply owned."
                }

                $file = Get-ReferencedVendorFile -FilesByPath $filesByPath -Path $path -Sha256 $sha256 -Bytes $bytes -Label "Vendor package '$packageId' native artifact"
                $nativeArtifacts += [pscustomobject]@{ Kind = $kind; File = $file }
                $previousNativeArtifactPath = $path
            }

            $licenseFiles = @()
            $licensePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $previousLicensePath = $null
            foreach ($licenseEntry in (Get-RequiredJsonArray -InputObject $nativePayload -Name "licenseFiles" -Label "Vendor release-input package '$packageId' native payload")) {
                Assert-ExactJsonProperties -InputObject $licenseEntry -Names @("path", "bytes", "sha256") -Label "Vendor release-input package '$packageId' license file"
                $path = Get-RequiredJsonString -InputObject $licenseEntry -Name "path" -Label "Vendor release-input package '$packageId' license file"
                $sha256 = Get-RequiredJsonString -InputObject $licenseEntry -Name "sha256" -Label "Vendor release-input package '$packageId' license file"
                $bytesValue = Get-RequiredJsonPropertyValue -InputObject $licenseEntry -Name "bytes" -Label "Vendor release-input package '$packageId' license file"
                if ($bytesValue -isnot [int] -and $bytesValue -isnot [long]) {
                    throw "Vendor release-input package '$packageId' license file '$path' bytes must be an integer."
                }

                $bytes = [int64]$bytesValue
                if ($bytes -lt 0 -or $sha256 -cnotmatch '^[0-9a-f]{64}$') {
                    throw "Vendor release-input package '$packageId' license file '$path' size or SHA-256 is invalid."
                }

                if ($null -ne $previousLicensePath `
                    -and [System.StringComparer]::Ordinal.Compare($previousLicensePath, $path) -ge 0) {
                    throw "Vendor release-input package '$packageId' license files must be strictly ordinal-sorted by path."
                }

                if (-not $licensePaths.Add($path)) {
                    throw "Vendor release-input package '$packageId' license file path '$path' is duplicate or case-colliding."
                }

                $licenseFiles += Get-ReferencedVendorFile -FilesByPath $filesByPath -Path $path -Sha256 $sha256 -Bytes $bytes -Label "Vendor package '$packageId' license file"
                $previousLicensePath = $path
            }

            if ($licenseFiles.Count -eq 0) {
                throw "Vendor release-input package '$packageId' must declare at least one license evidence file."
            }

            $provenance = Get-RequiredJsonPropertyValue -InputObject $entry -Name "provenance" -Label "Vendor release-input package '$packageId'"
            Assert-ExactJsonProperties -InputObject $provenance -Names @("path", "bytes", "sha256") -Label "Vendor release-input package '$packageId' provenance"
            $provenancePath = Get-RequiredJsonString -InputObject $provenance -Name "path" -Label "Vendor release-input package '$packageId' provenance"
            $provenanceSha256 = Get-RequiredJsonString -InputObject $provenance -Name "sha256" -Label "Vendor release-input package '$packageId' provenance"
            $provenanceBytesValue = Get-RequiredJsonPropertyValue -InputObject $provenance -Name "bytes" -Label "Vendor release-input package '$packageId' provenance"
            if (($provenanceBytesValue -isnot [int] -and $provenanceBytesValue -isnot [long]) `
                -or [int64]$provenanceBytesValue -lt 0 `
                -or $provenanceSha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw "Vendor release-input package '$packageId' provenance size or SHA-256 is invalid."
            }

            $provenanceFile = Get-ReferencedVendorFile `
                -FilesByPath $filesByPath `
                -Path $provenancePath `
                -Sha256 $provenanceSha256 `
                -Bytes ([int64]$provenanceBytesValue) `
                -Label "Vendor package '$packageId' provenance"

            $releasePackage = [pscustomobject]@{
                Id = $packageId
                Version = $packageVersion
                SourceIdentity = $sourceIdentity
                TargetId = $packageTargetId
                TargetTriple = $packageTargetTriple
                RootModule = $rootModule
                Image = $imageFile
                Library = $libraryFile
                Modules = [string[]]@($modules)
                NativeArtifacts = [object[]]$nativeArtifacts
                LicenseFiles = [object[]]$licenseFiles
                Provenance = $provenanceFile
            }
            $packages += $releasePackage
            $packagesById.Add($packageId, $releasePackage)
            $previousPackageId = $packageId
        }

        return [pscustomobject]@{
            Path = $releaseInputPath
            VendorRoot = $vendorRoot
            TargetId = $targetId
            TargetTriple = $targetTriple
            Packages = [object[]]$packages
            PackagesById = $packagesById
            FilesByPath = $filesByPath
        }
    } catch {
        throw "Vendor release-input manifest '$releaseInputPath' is invalid: $($_.Exception.Message)"
    }
}

function Merge-ReleaseInputLicensesIntoNativeDescriptor {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SdkRoot,
        [Parameter(Mandatory = $true)]
        [object] $Native,
        [Parameter(Mandatory = $true)]
        [object] $ReleasePackage
    )

    $licenseFiles = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in @($Native.licenseFiles)) {
        [void]$licenseFiles.Add([string]$path)
    }

    foreach ($license in @($ReleasePackage.LicenseFiles)) {
        $sdkRelativePath = ConvertTo-SdkRelativePath `
            -SdkRoot $SdkRoot `
            -Path $license.AbsolutePath `
            -Label "package '$($ReleasePackage.Id)' release-input license"
        [void]$licenseFiles.Add($sdkRelativePath)
    }

    $checksumPaths = @(Get-OrdinalSortedUniqueStrings -Values @(
        @($Native.artifacts) + @($Native.runtimeFiles) + @($licenseFiles)
    ))
    $fileChecksums = @($checksumPaths | ForEach-Object {
        $relativePath = [string]$_
        $absolutePath = Join-Path $SdkRoot ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
            throw "package '$($ReleasePackage.Id)' native checksum input '$absolutePath' is missing or is not a file"
        }

        [ordered]@{
            path = $relativePath
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $absolutePath).Hash.ToLowerInvariant()
        }
    })

    $Native.licenseFiles = [object[]]@($licenseFiles)
    $Native.fileChecksums = [object[]]$fileChecksums
}

function Assert-VendorCandidateMatchesReleaseInput {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SdkRoot,
        [Parameter(Mandatory = $true)]
        [string] $VendorRoot,
        [Parameter(Mandatory = $true)]
        [object] $Candidate,
        [Parameter(Mandatory = $true)]
        [object] $ReleasePackage
    )

    if ($Candidate.Id -cne $ReleasePackage.Id) {
        throw "Declared Vendor package '$($ReleasePackage.Id)' image identifies itself as '$($Candidate.Id)'."
    }

    $candidateTargetTriple = [string](Get-JsonPropertyValue -InputObject $Candidate.Target -Name "Triple")
    if ($candidateTargetTriple -cne $ReleasePackage.TargetTriple) {
        throw "Vendor package '$($Candidate.Id)' image target '$candidateTargetTriple' does not match release-input target '$($ReleasePackage.TargetTriple)'."
    }

    $candidateImagePath = Get-VendorRelativePath -VendorRoot $VendorRoot -Path $Candidate.ImagePath -Label "Vendor package '$($Candidate.Id)' image"
    $candidateLibraryPath = Get-VendorRelativePath -VendorRoot $VendorRoot -Path $Candidate.LibraryPath -Label "Vendor package '$($Candidate.Id)' library"
    if ($candidateImagePath -cne $ReleasePackage.Image.Path `
        -or $candidateLibraryPath -cne $ReleasePackage.Library.Path) {
        throw "Vendor package '$($Candidate.Id)' image/library paths do not match release-input artifacts."
    }

    $imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Candidate.ImagePath).Hash.ToLowerInvariant()
    $librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $Candidate.LibraryPath).Hash.ToLowerInvariant()
    if ($imageSha256 -cne $ReleasePackage.Image.Sha256 `
        -or $librarySha256 -cne $ReleasePackage.Library.Sha256) {
        throw "Vendor package '$($Candidate.Id)' image/library hashes do not match release-input artifacts."
    }

    $candidateModules = @(Get-OrdinalSortedUniqueStrings -Values @($Candidate.Modules))
    if (($candidateModules -join "`n") -cne (@($ReleasePackage.Modules) -join "`n")) {
        throw "Vendor package '$($Candidate.Id)' module set does not match release-input package metadata."
    }

    $candidateNativeArtifactPaths = @($Candidate.Native.artifacts | ForEach-Object {
        $absolutePath = Join-Path $SdkRoot ([string]$_).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        Get-VendorRelativePath -VendorRoot $VendorRoot -Path $absolutePath -Label "Vendor package '$($Candidate.Id)' native artifact"
    })
    $candidateNativeArtifacts = @(Get-OrdinalSortedUniqueStrings -Values $candidateNativeArtifactPaths)
    $releaseNativeArtifacts = @(Get-OrdinalSortedUniqueStrings -Values @(
        $ReleasePackage.NativeArtifacts | ForEach-Object { $_.File.Path }
    ))
    if (($candidateNativeArtifacts -join "`n") -cne ($releaseNativeArtifacts -join "`n")) {
        throw "Vendor package '$($Candidate.Id)' package-image native artifact set does not exactly match release-input nativePayload.artifacts."
    }

    $releaseLicensePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($license in @($ReleasePackage.LicenseFiles)) {
        [void]$releaseLicensePaths.Add($license.Path)
    }

    foreach ($sdkRelativePath in @($Candidate.Native.licenseFiles)) {
        $absolutePath = Join-Path $SdkRoot ([string]$sdkRelativePath).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $vendorRelativePath = Get-VendorRelativePath -VendorRoot $VendorRoot -Path $absolutePath -Label "Vendor package '$($Candidate.Id)' native license"
        if (-not $releaseLicensePaths.Contains($vendorRelativePath)) {
            throw "Vendor package '$($Candidate.Id)' package image discovers license '$vendorRelativePath' that release-input does not declare."
        }
    }

    Merge-ReleaseInputLicensesIntoNativeDescriptor -SdkRoot $SdkRoot -Native $Candidate.Native -ReleasePackage $ReleasePackage
    $Candidate.Version = $ReleasePackage.Version
}

function Write-StagedSdkManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SdkRoot,
        [Parameter(Mandatory = $true)]
        [string] $CompilerPath,
        [Parameter(Mandatory = $true)]
        [string] $StdlibDist,
        [Parameter(Mandatory = $true)]
        [string] $VendorDist
    )

    $compilerCompatibility = Get-StagedCompilerCompatibility -CompilerPath $CompilerPath
    $stdlibImagePaths = @(Get-OrdinalSortedStrings -Values @(
        Get-ChildItem -LiteralPath $StdlibDist -File -Recurse -Filter "*.starkpkg" |
            ForEach-Object { $_.FullName }
    ))
    $stdlibImages = @($stdlibImagePaths | ForEach-Object { Get-Item -LiteralPath $_ })
    if ($stdlibImages.Count -eq 0) {
        throw "Standard library staging directory '$StdlibDist' contains no .starkpkg image."
    }

    $inspectedStdlib = @()
    foreach ($image in $stdlibImages) {
        $inspection = Invoke-StagedPackageInspection -CompilerPath $CompilerPath -ImagePath $image.FullName
        $inspectedStdlib += [pscustomobject]@{ Image = $image; Inspection = $inspection }
    }

    $systemPackages = @($inspectedStdlib | Where-Object {
        [string](Get-JsonPropertyValue -InputObject $_.Inspection -Name "RootModule") -eq "System"
    })
    if ($systemPackages.Count -ne 1) {
        throw "Release SDK assembly requires exactly one staged System package image; found $($systemPackages.Count)."
    }

    $expectedTarget = Get-JsonPropertyValue -InputObject $systemPackages[0].Inspection -Name "Target"
    if ($null -eq $expectedTarget) {
        throw "The staged System package does not contain target facts."
    }

    $systemCandidate = New-StagedPackageCandidate `
        -SdkRoot $SdkRoot `
        -CompilerPath $CompilerPath `
        -ImagePath $systemPackages[0].Image.FullName `
        -ExpectedTarget $expectedTarget `
        -IsRequired

    $effectiveTargetTriple = [string](Get-JsonPropertyValue -InputObject $expectedTarget -Name "Triple")
    if (-not [string]::IsNullOrWhiteSpace($TargetTriple) `
        -and $TargetTriple -cne $effectiveTargetTriple) {
        throw "Staged System package target '$effectiveTargetTriple' does not exactly match requested release target '$TargetTriple'."
    }

    $vendorReleaseInput = Read-VendorReleaseInput `
        -VendorDist $VendorDist `
        -AssetSuffix $AssetSuffix `
        -ExpectedTargetTriple $effectiveTargetTriple
    $discoveredVendorImages = @(Get-OrdinalSortedStrings -Values @(
        Get-ChildItem -LiteralPath $VendorDist -File -Recurse -Filter "*.starkpkg" |
            ForEach-Object { [System.IO.Path]::GetFullPath($_.FullName) }
    ))
    $declaredVendorImages = @(Get-OrdinalSortedStrings -Values @(
        $vendorReleaseInput.Packages |
            ForEach-Object { [System.IO.Path]::GetFullPath($_.Image.AbsolutePath) }
    ))
    if (($discoveredVendorImages -join "`n") -cne ($declaredVendorImages -join "`n")) {
        throw "Staged Vendor package image set does not exactly match the schema-2 release-input package declarations."
    }

    $candidatePackages = @($systemCandidate)
    foreach ($releasePackage in @($vendorReleaseInput.Packages)) {
        $candidate = New-StagedPackageCandidate `
            -SdkRoot $SdkRoot `
            -CompilerPath $CompilerPath `
            -ImagePath $releasePackage.Image.AbsolutePath `
            -ExpectedTarget $expectedTarget
        Assert-VendorCandidateMatchesReleaseInput `
            -SdkRoot $SdkRoot `
            -VendorRoot $vendorReleaseInput.VendorRoot `
            -Candidate $candidate `
            -ReleasePackage $releasePackage
        $candidatePackages += $candidate
    }

    $declaredPackageIds = @(Get-OrdinalSortedStrings -Values @(
        $vendorReleaseInput.Packages | ForEach-Object { $_.Id }
    ))
    $candidatePackageIds = @(Get-OrdinalSortedStrings -Values @($candidatePackages `
        | Where-Object { -not $_.IsRequired } `
        | ForEach-Object { $_.Id }))
    if (($candidatePackageIds -join "`n") -cne ($declaredPackageIds -join "`n")) {
        throw "Compiler-inspected Vendor package ID set does not exactly match the schema-2 release-input package IDs."
    }

    $selectedPackages = @($candidatePackages)
    $selectedPackagesById = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    foreach ($package in $selectedPackages) {
        if ($selectedPackagesById.ContainsKey($package.Id)) {
            throw "SDK package ID '$($package.Id)' is declared by more than one package image."
        }

        $selectedPackagesById.Add($package.Id, $package)
    }
    $selectedPackageIds = @(Get-OrdinalSortedStrings -Values @($selectedPackagesById.Keys))
    $ownedModules = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($packageId in $selectedPackageIds) {
        $package = $selectedPackagesById[$packageId]
        foreach ($module in $package.Modules) {
            if ($ownedModules.ContainsKey($module)) {
                throw "SDK package '$($package.Id)' duplicates module ownership '$module' already held by '$($ownedModules[$module])'."
            }

            $ownedModules.Add($module, $package.Id)
        }
    }

    foreach ($package in $selectedPackages) {
        $missingOfficialImports = @($package.ImportedModules | Where-Object {
            $isOfficial = $_ -ceq "System" `
                -or $_.StartsWith("System.", [System.StringComparison]::Ordinal) `
                -or $_.StartsWith("Vendor.", [System.StringComparison]::Ordinal)
            $isOfficial -and -not $ownedModules.ContainsKey($_)
        })
        if ($missingOfficialImports.Count -ne 0) {
            throw "SDK package '$($package.Id)' imports unavailable official modules ($($missingOfficialImports -join ', '))."
        }
    }

    $packageFormatVersionSet = [System.Collections.Generic.HashSet[uint32]]::new()
    foreach ($package in $selectedPackages) {
        [void]$packageFormatVersionSet.Add([uint32]$package.PackageFormatVersion)
    }
    $packageFormatVersions = @($packageFormatVersionSet)
    if ($packageFormatVersions.Count -ne 1) {
        throw "Selected SDK packages use incompatible binary package format versions: $($packageFormatVersions -join ', ')."
    }

    $modules = @((Get-OrdinalSortedStrings -Values @($ownedModules.Keys)) | ForEach-Object {
        [ordered]@{ name = $_; package = $ownedModules[$_] }
    })
    $packages = @()
    foreach ($packageId in $selectedPackageIds) {
        $package = $selectedPackagesById[$packageId]
        $derivedDependencyIds = @(Get-OrdinalSortedUniqueStrings -Values @($package.ImportedModules `
            | Where-Object { $ownedModules.ContainsKey($_) -and $ownedModules[$_] -cne $package.Id } `
            | ForEach-Object { $ownedModules[$_] }))
        $declaredDependencyIds = @(Get-OrdinalSortedUniqueStrings -Values @(
            $package.Dependencies | ForEach-Object { $_.Id }
        ))
        if (($derivedDependencyIds -join "`n") -cne ($declaredDependencyIds -join "`n")) {
            throw "package '$($package.Id)' dependency identity set '$($declaredDependencyIds -join ', ')' does not match its cross-package import set '$($derivedDependencyIds -join ', ')'"
        }

        $dependencyManifests = @($package.Dependencies | ForEach-Object {
            $dependency = $_
            if (-not $selectedPackagesById.ContainsKey($dependency.Id)) {
                throw "package '$($package.Id)' dependency '$($dependency.Id)' is not uniquely present in the selected SDK package set"
            }
            $selectedDependency = $selectedPackagesById[$dependency.Id]

            if ($selectedDependency.ApiHash -cne $dependency.ApiHash `
                -or $selectedDependency.ContentHash -cne $dependency.ContentHash) {
                throw "package '$($package.Id)' dependency '$($dependency.Id)' API/content identity does not match the selected package image"
            }

            [ordered]@{
                id = $dependency.Id
                apiHash = $dependency.ApiHash
                contentHash = $dependency.ContentHash
            }
        })
        $packages += [ordered]@{
            id = $package.Id
            version = $package.Version
            profile = $package.Profile
            image = $package.ImageRelativePath
            library = $package.LibraryRelativePath
            apiHash = $package.ApiHash
            contentHash = $package.ContentHash
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $package.ImagePath).Hash.ToLowerInvariant()
            librarySha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $package.LibraryPath).Hash.ToLowerInvariant()
            dependencies = [object[]]$dependencyManifests
            native = $package.Native
        }
    }

    $targetTriple = [string](Get-JsonPropertyValue -InputObject $expectedTarget -Name "Triple")
    $targetDataLayout = [string](Get-JsonPropertyValue -InputObject $expectedTarget -Name "DataLayout")
    $targetCpu = [string](Get-JsonPropertyValue -InputObject $expectedTarget -Name "Cpu")
    $targetRelocationModel = [string](Get-JsonPropertyValue -InputObject $expectedTarget -Name "RelocationModel")
    $targetCodeModel = [string](Get-JsonPropertyValue -InputObject $expectedTarget -Name "CodeModel")
    $targetCDataModel = [string](Get-JsonPropertyValue `
        -InputObject (Get-JsonPropertyValue -InputObject $expectedTarget -Name "CDataModel") `
        -Name "Kind")
    if ([string]::IsNullOrWhiteSpace($targetRelocationModel)) {
        $targetRelocationModel = "default"
    }

    if ([string]::IsNullOrWhiteSpace($targetDataLayout)) {
        throw "Release SDK target '$targetTriple' does not contain a nonempty LLVM data layout."
    }

    $target = [ordered]@{
        id = $AssetSuffix
        llvmTriple = $targetTriple
        architecture = Get-NormalizedTargetArchitecture -Triple $targetTriple
        operatingSystem = Get-NormalizedTargetOperatingSystem -Triple $targetTriple
        abi = Get-NormalizedTargetAbi -Triple $targetTriple
        pointerBitWidth = Get-TargetPointerBitWidth -Target $expectedTarget
        endianness = Get-TargetEndianness -Target $expectedTarget
        dataLayout = $targetDataLayout
        baselineCpu = if ([string]::IsNullOrWhiteSpace($targetCpu)) { $null } else { $targetCpu }
        baselineFeatures = [object[]]@(Get-ValidatedTargetFeatures -Target $expectedTarget -Label "SDK baseline package")
        relocationModel = $targetRelocationModel
        codeModel = if ([string]::IsNullOrWhiteSpace($targetCodeModel)) { $null } else { $targetCodeModel }
        cDataModel = if ([string]::IsNullOrWhiteSpace($targetCDataModel)) { $null } else { $targetCDataModel }
        minimumOperatingSystemVersion = Get-MinimumOperatingSystemVersion -Triple $targetTriple
    }

    $sdkManifest = [ordered]@{
        schemaVersion = 1
        kind = "release"
        sdkVersion = $Version
        compilerCompatibility = $compilerCompatibility
        packageFormatVersion = [int]$packageFormatVersions[0]
        target = $target
        modules = [object[]]$modules
        packages = [object[]]$packages
    }

    $sdkManifestPath = Join-Path $SdkRoot "sdk.json"
    $sdkJson = $sdkManifest | ConvertTo-Json -Depth 100
    $sdkJson = $sdkJson.Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText(
        $sdkManifestPath,
        $sdkJson + "`n",
        [System.Text.UTF8Encoding]::new($false))

    $doctorOutput = @(& $CompilerPath doctor --sdk-root $SdkRoot --target $targetTriple --format json 2>&1)
    $doctorExitCode = $LASTEXITCODE
    $doctorText = $doctorOutput -join [System.Environment]::NewLine
    if ($doctorExitCode -ne 0) {
        throw "Staged compiler rejected generated sdk.json (doctor exit $doctorExitCode):$([System.Environment]::NewLine)$doctorText"
    }

    try {
        $doctor = $doctorText | ConvertFrom-Json
    } catch {
        throw "Staged compiler produced invalid doctor JSON:$([System.Environment]::NewLine)$doctorText"
    }

    if ([int](Get-JsonPropertyValue -InputObject $doctor -Name "schemaVersion") -ne 1) {
        throw "Staged compiler produced an unsupported doctor JSON schema:$([System.Environment]::NewLine)$doctorText"
    }

    $doctorCompiler = Get-JsonPropertyValue -InputObject $doctor -Name "compiler"
    $reportedCompilerVersion = [string](Get-JsonPropertyValue -InputObject $doctorCompiler -Name "version")
    if (-not [string]::Equals($reportedCompilerVersion, $Version, [System.StringComparison]::Ordinal)) {
        throw "Staged compiler informational version '$reportedCompilerVersion' does not match release version '$Version'. Publish with -p:InformationalVersion=$Version and -p:IncludeSourceRevisionInInformationalVersion=false."
    }

    $doctorSdk = Get-JsonPropertyValue -InputObject $doctor -Name "sdk"
    if (-not [string]::Equals(
        [string](Get-JsonPropertyValue -InputObject $doctorSdk -Name "status"),
        "ok",
        [System.StringComparison]::Ordinal)) {
        throw "Staged compiler did not accept generated sdk.json:$([System.Environment]::NewLine)$doctorText"
    }

    Write-Host "Generated sdk.json for target '$targetTriple' with $($packages.Count) package(s) and $($modules.Count) module owner(s)."
}

Write-StagedSdkManifest `
    -SdkRoot $SdkRoot `
    -CompilerPath $CompilerPath `
    -StdlibDist $StdlibDist `
    -VendorDist $VendorDist
