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
# contract. System is mandatory; vendor packages are an allowlist derived from
# target compatibility, complete package/native payloads, and unique module
# ownership. The staged compiler remains the source of truth for package facts.

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

    return $property.Value
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

    return @(Get-JsonArray -InputObject $Inspection -Name "Modules" `
        | ForEach-Object { [string](Get-JsonPropertyValue -InputObject $_ -Name "ModuleName") } `
        | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } `
        | Sort-Object -Unique)
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

    return @($imports | Sort-Object -Unique)
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
    $includeDirectories = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    $libraryDirectories = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
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
        $includeDirectories.Add((ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $resolvedDirectory -Label "package '$PackageId' native include directory")) | Out-Null
        foreach ($includeFile in (Get-ChildItem -LiteralPath $resolvedDirectory -File -Recurse | Sort-Object FullName)) {
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
        $libraryDirectories.Add((ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $resolvedDirectory -Label "package '$PackageId' native library directory")) | Out-Null
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
        foreach ($file in (Get-ChildItem -LiteralPath $resolvedDirectory -File -Recurse | Sort-Object FullName)) {
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

    $checksumPaths = @(
        @($artifacts) + @($runtimeFiles) + @($licenseFiles) |
            Sort-Object -Unique
    )
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

    $dependencies = @($dependencies | Sort-Object Id)

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

function Get-VendorPackageVersions {
    param(
        [Parameter(Mandatory = $true)]
        [string] $VendorDist
    )

    $versions = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal)
    $vendorRoot = Split-Path -Parent $VendorDist
    $releaseInputPath = Join-Path $vendorRoot "release-input.json"
    if (-not (Test-Path -LiteralPath $releaseInputPath -PathType Leaf)) {
        return $versions
    }

    try {
        $releaseInput = Get-Content -LiteralPath $releaseInputPath -Raw | ConvertFrom-Json
        if ([int](Get-JsonPropertyValue -InputObject $releaseInput -Name "schemaVersion") -ne 1) {
            throw "unsupported schema"
        }

        $raylib = Get-JsonPropertyValue -InputObject $releaseInput -Name "raylib"
        $package = Get-JsonPropertyValue -InputObject $raylib -Name "package"
        $packageId = [string](Get-JsonPropertyValue -InputObject $package -Name "rootModule")
        $packageVersion = [string](Get-JsonPropertyValue -InputObject $raylib -Name "raylibVersion")
        if ([string]::IsNullOrWhiteSpace($packageId) -or
            [string]::IsNullOrWhiteSpace($packageVersion)) {
            throw "missing package identity/version"
        }

        $versions.Add($packageId.Trim(), $packageVersion.Trim())
    } catch {
        throw "Vendor release-input manifest '$releaseInputPath' is invalid: $($_.Exception.Message)"
    }

    return $versions
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
    $stdlibImages = @(Get-ChildItem -LiteralPath $StdlibDist -File -Recurse -Filter "*.starkpkg" | Sort-Object FullName)
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
    if (-not [string]::IsNullOrWhiteSpace($TargetTriple)) {
        $requestedArchitecture = Get-NormalizedTargetArchitecture -Triple $TargetTriple
        $requestedOs = Get-NormalizedTargetOperatingSystem -Triple $TargetTriple
        if ($requestedArchitecture -cne (Get-NormalizedTargetArchitecture -Triple $effectiveTargetTriple) `
            -or $requestedOs -cne (Get-NormalizedTargetOperatingSystem -Triple $effectiveTargetTriple)) {
            throw "Staged System package target '$effectiveTargetTriple' does not match requested release target '$TargetTriple'."
        }
    }

    $vendorPackageVersions = Get-VendorPackageVersions -VendorDist $VendorDist
    $candidatePackages = @($systemCandidate)
    if (Test-Path -LiteralPath $VendorDist -PathType Container) {
        foreach ($image in (Get-ChildItem -LiteralPath $VendorDist -File -Recurse -Filter "*.starkpkg" | Sort-Object FullName)) {
            try {
                $candidate = New-StagedPackageCandidate `
                    -SdkRoot $SdkRoot `
                    -CompilerPath $CompilerPath `
                    -ImagePath $image.FullName `
                    -ExpectedTarget $expectedTarget
                if (-not $candidate.Id.StartsWith("Vendor.", [System.StringComparison]::Ordinal)) {
                    Write-Warning "SDK omitted '$($candidate.ImageRelativePath)': package identity '$($candidate.Id)' is not in the official Vendor namespace."
                    continue
                }

                if ($vendorPackageVersions.ContainsKey($candidate.Id)) {
                    $candidate.Version = $vendorPackageVersions[$candidate.Id]
                }

                $candidatePackages += $candidate
            } catch {
                $imageRelativePath = ConvertTo-SdkRelativePath -SdkRoot $SdkRoot -Path $image.FullName -Label "vendor package image"
                Write-Warning "SDK omitted '$imageRelativePath': $($_.Exception.Message)"
            }
        }
    }

    $selectedPackages = @($systemCandidate)
    $ownedModules = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($module in $systemCandidate.Modules) {
        $ownedModules.Add($module, $systemCandidate.Id)
    }

    foreach ($candidate in ($candidatePackages `
        | Where-Object { -not $_.IsRequired } `
        | Sort-Object @{ Expression = "Id"; Ascending = $true }, @{ Expression = "ImageRelativePath"; Ascending = $true })) {
        if ($selectedPackages.Id -contains $candidate.Id) {
            Write-Warning "SDK omitted '$($candidate.ImageRelativePath)': package '$($candidate.Id)' already has a selected target artifact."
            continue
        }

        $duplicates = @($candidate.Modules | Where-Object { $ownedModules.ContainsKey($_) })
        if ($duplicates.Count -ne 0) {
            $duplicateSummary = ($duplicates | Select-Object -First 5) -join ", "
            if ($duplicates.Count -gt 5) {
                $duplicateSummary += ", ..."
            }

            Write-Warning "SDK omitted '$($candidate.ImageRelativePath)': package '$($candidate.Id)' duplicates module ownership ($duplicateSummary). Rebuild it with source-owned package emission before advertising it."
            continue
        }

        $selectedPackages += $candidate
        foreach ($module in $candidate.Modules) {
            $ownedModules.Add($module, $candidate.Id)
        }
    }

    $removedPackage = $true
    while ($removedPackage) {
        $removedPackage = $false
        $ownedModules.Clear()
        foreach ($package in $selectedPackages) {
            foreach ($module in $package.Modules) {
                $ownedModules[$module] = $package.Id
            }
        }

        foreach ($package in @($selectedPackages | Where-Object { -not $_.IsRequired })) {
            $missingOfficialImports = @($package.ImportedModules | Where-Object {
                ($_.StartsWith("System", [System.StringComparison]::Ordinal) `
                    -or $_.StartsWith("Vendor.", [System.StringComparison]::Ordinal)) `
                    -and -not $ownedModules.ContainsKey($_)
            })
            if ($missingOfficialImports.Count -ne 0) {
                Write-Warning "SDK omitted '$($package.ImageRelativePath)': package '$($package.Id)' imports unavailable official modules ($($missingOfficialImports -join ', '))."
                $selectedPackages = @($selectedPackages | Where-Object { $_ -ne $package })
                $removedPackage = $true
            }
        }
    }

    $ownedModules.Clear()
    foreach ($package in $selectedPackages) {
        foreach ($module in $package.Modules) {
            $ownedModules[$module] = $package.Id
        }
    }

    $packageFormatVersions = @($selectedPackages.PackageFormatVersion | Sort-Object -Unique)
    if ($packageFormatVersions.Count -ne 1) {
        throw "Selected SDK packages use incompatible binary package format versions: $($packageFormatVersions -join ', ')."
    }

    $modules = @($ownedModules.GetEnumerator() `
        | Sort-Object Key `
        | ForEach-Object { [ordered]@{ name = $_.Key; package = $_.Value } })
    $packages = @($selectedPackages `
        | Sort-Object Id `
        | ForEach-Object {
            $package = $_
            $derivedDependencyIds = @($package.ImportedModules `
                | Where-Object { $ownedModules.ContainsKey($_) -and $ownedModules[$_] -cne $package.Id } `
                | ForEach-Object { $ownedModules[$_] } `
                | Sort-Object -Unique)
            $declaredDependencyIds = @($package.Dependencies | ForEach-Object { $_.Id } | Sort-Object -Unique)
            if (($derivedDependencyIds -join "`n") -cne ($declaredDependencyIds -join "`n")) {
                throw "package '$($package.Id)' dependency identity set '$($declaredDependencyIds -join ', ')' does not match its cross-package import set '$($derivedDependencyIds -join ', ')'"
            }

            $dependencyManifests = @($package.Dependencies | ForEach-Object {
                $dependency = $_
                $selectedDependency = @($selectedPackages | Where-Object { $_.Id -ceq $dependency.Id })
                if ($selectedDependency.Count -ne 1) {
                    throw "package '$($package.Id)' dependency '$($dependency.Id)' is not uniquely present in the selected SDK package set"
                }

                if ($selectedDependency[0].ApiHash -cne $dependency.ApiHash `
                    -or $selectedDependency[0].ContentHash -cne $dependency.ContentHash) {
                    throw "package '$($package.Id)' dependency '$($dependency.Id)' API/content identity does not match the selected package image"
                }

                [ordered]@{
                    id = $dependency.Id
                    apiHash = $dependency.ApiHash
                    contentHash = $dependency.ContentHash
                }
            })
            [ordered]@{
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
        })

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
