Set-StrictMode -Version Latest

$script:ReleaseArchiveMaximumEntries = 250000
$script:ReleaseArchiveMaximumPathLength = 4096
$script:ReleaseArchiveMaximumSegmentLength = 255
$script:ReleaseArchiveMaximumFileBytes = 4L * 1024L * 1024L * 1024L
$script:ReleaseArchiveMaximumTotalBytes = 16L * 1024L * 1024L * 1024L
$script:ReleaseArchiveReservedWindowsNames = [System.Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
    "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9")) {
    [void]$script:ReleaseArchiveReservedWindowsNames.Add($name)
}

function Assert-ReleaseArchivePortableSegment {
    param(
        [Parameter(Mandatory = $true)] [string] $Segment,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    if ([string]::IsNullOrEmpty($Segment) -or $Segment -eq "." -or $Segment -eq "..") {
        throw "$Label path '$Path' contains an empty or traversal segment."
    }
    if ($Segment.Length -gt $script:ReleaseArchiveMaximumSegmentLength) {
        throw "$Label path '$Path' contains a segment longer than $($script:ReleaseArchiveMaximumSegmentLength) bytes."
    }
    if ($Segment.EndsWith(" ", [StringComparison]::Ordinal) -or
        $Segment.EndsWith(".", [StringComparison]::Ordinal)) {
        throw "$Label path '$Path' contains a Windows-ambiguous segment."
    }

    foreach ($character in $Segment.ToCharArray()) {
        $code = [int]$character
        if ($code -gt 0x7f) {
            throw "$Label path '$Path' contains a non-ASCII segment."
        }
        if ($code -lt 0x20 -or $code -eq 0x7f -or '<>"|?*'.Contains($character)) {
            throw "$Label path '$Path' contains a non-portable character."
        }
    }

    $deviceName = $Segment.Split('.', 2)[0]
    if ($script:ReleaseArchiveReservedWindowsNames.Contains($deviceName)) {
        throw "$Label path '$Path' contains reserved Windows segment '$Segment'."
    }
}

function ConvertTo-ReleaseArchivePortablePath {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Label,
        [switch] $Directory
    )

    $portablePath = $Path
    if ($Directory -and $portablePath.EndsWith("/", [StringComparison]::Ordinal)) {
        if ($portablePath.Length -lt 2 -or $portablePath[$portablePath.Length - 2] -eq '/') {
            throw "$Label path '$Path' contains an empty trailing segment."
        }
        $portablePath = $portablePath.Substring(0, $portablePath.Length - 1)
    }
    if ([string]::IsNullOrEmpty($portablePath) -or
        $portablePath.StartsWith("/", [StringComparison]::Ordinal) -or
        $portablePath.Contains('\') -or
        $portablePath.Contains(':') -or
        $portablePath.Contains([char]0)) {
        throw "$Label path '$Path' is not a portable relative path."
    }
    if ($portablePath.Length -gt $script:ReleaseArchiveMaximumPathLength) {
        throw "$Label path '$Path' exceeds $($script:ReleaseArchiveMaximumPathLength) bytes."
    }

    foreach ($segment in $portablePath.Split('/')) {
        Assert-ReleaseArchivePortableSegment -Segment $segment -Path $portablePath -Label $Label
    }
    return $portablePath
}

function Resolve-ReleaseArchiveSymbolicLinkTarget {
    param(
        [Parameter(Mandatory = $true)] [string] $LinkPath,
        [Parameter(Mandatory = $true)] [string] $Target,
        [Parameter(Mandatory = $true)] [string] $RootName,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    if ([string]::IsNullOrEmpty($Target) -or
        $Target.StartsWith("/", [StringComparison]::Ordinal) -or
        $Target.Contains('\') -or
        $Target.Contains(':') -or
        $Target.Contains([char]0)) {
        throw "$Label symbolic link '$LinkPath' has unsafe target '$Target'."
    }
    foreach ($character in $Target.ToCharArray()) {
        if ([int]$character -gt 0x7f) {
            throw "$Label symbolic link '$LinkPath' has non-ASCII target '$Target'."
        }
    }

    $parts = [System.Collections.Generic.List[string]]::new()
    $linkParts = $LinkPath.Split('/')
    for ($index = 0; $index -lt $linkParts.Length - 1; $index++) {
        $parts.Add($linkParts[$index])
    }
    foreach ($segment in $Target.Split('/')) {
        if ($segment -eq ".") {
            continue
        }
        if ([string]::IsNullOrEmpty($segment)) {
            throw "$Label symbolic link '$LinkPath' has an empty target segment in '$Target'."
        }
        if ($segment -eq "..") {
            if ($parts.Count -le 1) {
                throw "$Label symbolic link '$LinkPath' escapes archive root through '$Target'."
            }
            $parts.RemoveAt($parts.Count - 1)
            continue
        }
        Assert-ReleaseArchivePortableSegment -Segment $segment -Path $Target -Label "$Label symbolic-link target"
        $parts.Add($segment)
    }

    $resolved = [string]::Join('/', $parts)
    if ($resolved -ne $RootName -and
        -not $resolved.StartsWith("$RootName/", [StringComparison]::Ordinal)) {
        throw "$Label symbolic link '$LinkPath' resolves outside archive root '$RootName'."
    }
    return $resolved
}

function New-ReleaseArchiveRecord {
    param(
        [string] $Path,
        [string] $RawName,
        [string] $Kind,
        [long] $Size,
        [int] $Mode,
        [string] $Target,
        [int] $Index
    )

    return [pscustomobject]@{
        Path = $Path
        RawName = $RawName
        Kind = $Kind
        Size = $Size
        Mode = $Mode
        Target = $Target
        Index = $Index
        ResolvedTarget = ""
        UltimateTarget = ""
    }
}

function Get-ReleaseZipArchiveRecords {
    param(
        [Parameter(Mandatory = $true)] [System.IO.Stream] $Stream,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $records = [System.Collections.Generic.List[object]]::new()
    $archive = [System.IO.Compression.ZipArchive]::new(
        $Stream,
        [System.IO.Compression.ZipArchiveMode]::Read,
        $true)
    try {
        $index = 0
        foreach ($entry in $archive.Entries) {
            $isDirectory = $entry.FullName.EndsWith("/", [StringComparison]::Ordinal)
            $path = ConvertTo-ReleaseArchivePortablePath `
                -Path $entry.FullName `
                -Label "$Label ZIP entry" `
                -Directory:$isDirectory
            $attributes = [uint32]([int64]$entry.ExternalAttributes -band 0xffffffffL)
            $unixMode = [int](($attributes -shr 16) -band 0xffff)
            $fileType = $unixMode -band 0xf000
            $isReparsePoint = ($attributes -band 0x0400) -ne 0
            if ($isReparsePoint) {
                throw "$Label ZIP entry '$path' is a forbidden Windows reparse point."
            }
            if ($fileType -eq 0xa000) {
                throw "$Label ZIP entry '$path' is a forbidden symbolic link."
            }
            if ($isDirectory) {
                if ($fileType -ne 0 -and $fileType -ne 0x4000) {
                    throw "$Label ZIP directory '$path' has forbidden file type $($fileType.ToString('x'))."
                }
                if ($entry.Length -ne 0) {
                    throw "$Label ZIP directory '$path' contains a forbidden data payload."
                }
                $kind = "directory"
                $size = 0L
            } else {
                if ($fileType -ne 0 -and $fileType -ne 0x8000) {
                    throw "$Label ZIP entry '$path' has forbidden file type $($fileType.ToString('x'))."
                }
                $kind = "file"
                $size = [long]$entry.Length
            }
            $records.Add((New-ReleaseArchiveRecord `
                -Path $path `
                -RawName $entry.FullName `
                -Kind $kind `
                -Size $size `
                -Mode $unixMode `
                -Target "" `
                -Index $index))
            $index++
        }
    } finally {
        $archive.Dispose()
    }
    return ,$records.ToArray()
}

function Get-ReleaseTarArchiveRecord {
    param(
        [Parameter(Mandatory = $true)] [System.Formats.Tar.TarEntry] $Entry,
        [Parameter(Mandatory = $true)] [string] $Label,
        [Parameter(Mandatory = $true)] [int] $Index
    )

    $type = $Entry.EntryType
    $isDirectory = $type -eq [System.Formats.Tar.TarEntryType]::Directory
    $path = ConvertTo-ReleaseArchivePortablePath `
        -Path $Entry.Name `
        -Label "$Label TAR entry" `
        -Directory:$isDirectory
    switch ($type) {
        ([System.Formats.Tar.TarEntryType]::Directory) { $kind = "directory"; break }
        ([System.Formats.Tar.TarEntryType]::RegularFile) { $kind = "file"; break }
        ([System.Formats.Tar.TarEntryType]::V7RegularFile) { $kind = "file"; break }
        ([System.Formats.Tar.TarEntryType]::SymbolicLink) { $kind = "symlink"; break }
        ([System.Formats.Tar.TarEntryType]::HardLink) { $kind = "hardlink"; break }
        default {
            throw "$Label TAR entry '$path' has forbidden type '$type'."
        }
    }
    $size = if ($kind -eq "file") { [long]$Entry.Length } else { 0L }
    if ($kind -ne "file" -and $Entry.Length -ne 0) {
        throw "$Label TAR $kind entry '$path' contains a forbidden data payload."
    }
    $target = if ($kind -eq "symlink" -or $kind -eq "hardlink") { [string]$Entry.LinkName } else { "" }
    return New-ReleaseArchiveRecord `
        -Path $path `
        -RawName $Entry.Name `
        -Kind $kind `
        -Size $size `
        -Mode ([int]$Entry.Mode) `
        -Target $target `
        -Index $Index
}

function Get-ReleaseTarArchiveRecords {
    param(
        [Parameter(Mandatory = $true)] [System.IO.Stream] $Stream,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $records = [System.Collections.Generic.List[object]]::new()
    $gzip = [System.IO.Compression.GZipStream]::new(
        $Stream,
        [System.IO.Compression.CompressionMode]::Decompress,
        $true)
    $reader = [System.Formats.Tar.TarReader]::new($gzip, $true)
    try {
        $index = 0
        while ($null -ne ($entry = $reader.GetNextEntry($false))) {
            $records.Add((Get-ReleaseTarArchiveRecord -Entry $entry -Label $Label -Index $index))
            $index++
        }
    } finally {
        $reader.Dispose()
        $gzip.Dispose()
    }
    return ,$records.ToArray()
}

function Resolve-ReleaseArchiveGraphPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [System.Collections.Generic.Dictionary[string,object]] $ByPath,
        [Parameter(Mandatory = $true)] [string] $RootName,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]] $ActiveLinks,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    if ($Path -ne $RootName -and -not $Path.StartsWith("$RootName/", [StringComparison]::Ordinal)) {
        throw "$Label link graph path '$Path' escapes archive root '$RootName'."
    }

    $parts = $Path.Split('/')
    for ($index = 0; $index -lt $parts.Length; $index++) {
        $prefix = [string]::Join('/', $parts[0..$index])
        if (-not $ByPath.ContainsKey($prefix)) {
            throw "$Label link graph has dangling path '$Path' at '$prefix'."
        }
        $record = $ByPath[$prefix]
        $isFinal = $index -eq $parts.Length - 1
        if ($record.Kind -eq "directory") {
            if ($isFinal) {
                return $record
            }
            continue
        }
        if ($record.Kind -eq "file") {
            if (-not $isFinal) {
                throw "$Label link graph descends through file '$prefix'."
            }
            return $record
        }
        if (-not $isFinal -and $record.Kind -eq "hardlink") {
            throw "$Label link graph descends through hard link '$prefix'."
        }
        if ($record.Kind -ne "symlink" -and $record.Kind -ne "hardlink") {
            throw "$Label link graph contains unsupported node '$prefix' of kind '$($record.Kind)'."
        }
        if (-not $ActiveLinks.Add($prefix)) {
            throw "$Label link graph contains a cycle through '$prefix'."
        }
        try {
            $redirected = $record.ResolvedTarget
            if (-not $isFinal) {
                $remaining = [string]::Join('/', $parts[($index + 1)..($parts.Length - 1)])
                $redirected = "$redirected/$remaining"
            }
            return Resolve-ReleaseArchiveGraphPath `
                -Path $redirected `
                -ByPath $ByPath `
                -RootName $RootName `
                -ActiveLinks $ActiveLinks `
                -Label $Label
        } finally {
            [void]$ActiveLinks.Remove($prefix)
        }
    }
    throw "$Label could not resolve link graph path '$Path'."
}

function Resolve-ReleaseArchiveSymbolicGraphTarget {
    param(
        [Parameter(Mandatory = $true)] [object] $LinkRecord,
        [Parameter(Mandatory = $true)] [System.Collections.Generic.Dictionary[string,object]] $ByPath,
        [Parameter(Mandatory = $true)] [string] $RootName,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()]
        [System.Collections.Generic.HashSet[string]] $ActiveLinks,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    if (-not $ActiveLinks.Add($LinkRecord.Path)) {
        throw "$Label symbolic-link graph contains a cycle through '$($LinkRecord.Path)'."
    }
    try {
        $current = [System.Collections.Generic.List[string]]::new()
        $linkParts = $LinkRecord.Path.Split('/')
        for ($index = 0; $index -lt $linkParts.Length - 1; $index++) {
            $current.Add($linkParts[$index])
        }
        $segments = $LinkRecord.Target.Split('/')
        for ($index = 0; $index -lt $segments.Length; $index++) {
            $segment = $segments[$index]
            if ($segment -eq ".") {
                continue
            }
            if ($segment -eq "..") {
                if ($current.Count -le 1) {
                    throw "$Label symbolic link '$($LinkRecord.Path)' escapes archive root through '$($LinkRecord.Target)'."
                }
                $current.RemoveAt($current.Count - 1)
                continue
            }

            $candidate = [string]::Join('/', @($current.ToArray()) + @($segment))
            if (-not $ByPath.ContainsKey($candidate)) {
                throw "$Label symbolic link '$($LinkRecord.Path)' has dangling graph path '$candidate'."
            }
            $candidateRecord = $ByPath[$candidate]
            $hasRemaining = $index -lt $segments.Length - 1
            if ($candidateRecord.Kind -eq "directory") {
                if (-not $hasRemaining) {
                    return $candidateRecord
                }
                $current.Add($segment)
                continue
            }
            if ($candidateRecord.Kind -eq "symlink") {
                $resolved = Resolve-ReleaseArchiveSymbolicGraphTarget `
                    -LinkRecord $candidateRecord `
                    -ByPath $ByPath `
                    -RootName $RootName `
                    -ActiveLinks $ActiveLinks `
                    -Label $Label
                if (-not $hasRemaining) {
                    return $resolved
                }
                if ($resolved.Kind -ne "directory") {
                    throw "$Label symbolic link '$($LinkRecord.Path)' descends through non-directory link '$candidate'."
                }
                $current.Clear()
                foreach ($part in $resolved.Path.Split('/')) {
                    $current.Add($part)
                }
                continue
            }
            if ($hasRemaining) {
                throw "$Label symbolic link '$($LinkRecord.Path)' descends through non-directory '$candidate' ($($candidateRecord.Kind))."
            }
            if ($candidateRecord.Kind -eq "file") {
                return $candidateRecord
            }
            if ($candidateRecord.Kind -eq "hardlink") {
                $hardLinkActive = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                return Resolve-ReleaseArchiveGraphPath `
                    -Path $candidateRecord.ResolvedTarget `
                    -ByPath $ByPath `
                    -RootName $RootName `
                    -ActiveLinks $hardLinkActive `
                    -Label $Label
            }
            throw "$Label symbolic link '$($LinkRecord.Path)' targets unsupported node '$candidate'."
        }

        $resolvedPath = [string]::Join('/', $current)
        if (-not $ByPath.ContainsKey($resolvedPath) -or $ByPath[$resolvedPath].Kind -ne "directory") {
            throw "$Label symbolic link '$($LinkRecord.Path)' has dangling directory target '$($LinkRecord.Target)'."
        }
        return $ByPath[$resolvedPath]
    } finally {
        [void]$ActiveLinks.Remove($LinkRecord.Path)
    }
}

function Test-ReleaseArchiveRecords {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Records,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    if ($Records.Count -eq 0) {
        throw "$Label is empty."
    }
    if ($Records.Count -gt $script:ReleaseArchiveMaximumEntries) {
        throw "$Label contains more than $($script:ReleaseArchiveMaximumEntries) entries."
    }

    $portablePaths = [System.Collections.Generic.Dictionary[string,string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $byPath = [System.Collections.Generic.Dictionary[string,object]]::new(
        [StringComparer]::Ordinal)
    $rootName = $null
    $totalBytes = 0L
    foreach ($record in $Records) {
        $firstSeparator = $record.Path.IndexOf('/')
        $candidateRoot = if ($firstSeparator -lt 0) { $record.Path } else { $record.Path.Substring(0, $firstSeparator) }
        if ($null -eq $rootName) {
            $rootName = $candidateRoot
        } elseif (-not [string]::Equals($rootName, $candidateRoot, [StringComparison]::Ordinal)) {
            throw "$Label contains more than one top-level path ('$rootName' and '$candidateRoot')."
        }
        if ($portablePaths.ContainsKey($record.Path)) {
            throw "$Label contains duplicate or portable-colliding paths '$($portablePaths[$record.Path])' and '$($record.Path)'."
        }
        $portablePaths.Add($record.Path, $record.Path)
        $byPath.Add($record.Path, $record)

        if ($record.Kind -eq "file") {
            if ($record.Size -lt 0 -or $record.Size -gt $script:ReleaseArchiveMaximumFileBytes) {
                throw "$Label file '$($record.Path)' has unsafe size $($record.Size)."
            }
            $totalBytes += $record.Size
            if ($totalBytes -gt $script:ReleaseArchiveMaximumTotalBytes) {
                throw "$Label expands beyond the $($script:ReleaseArchiveMaximumTotalBytes)-byte safety limit."
            }
        }
    }

    if (-not $byPath.ContainsKey($rootName) -or $byPath[$rootName].Kind -ne "directory") {
        throw "$Label must contain its sole top-level path '$rootName' as a directory entry."
    }

    foreach ($record in $Records) {
        $parts = $record.Path.Split('/')
        for ($index = 1; $index -lt $parts.Length; $index++) {
            $ancestor = [string]::Join('/', $parts[0..($index - 1)])
            if (-not $byPath.ContainsKey($ancestor)) {
                throw "$Label path '$($record.Path)' has missing directory entry '$ancestor'."
            }
            if ($byPath[$ancestor].Kind -ne "directory") {
                throw "$Label path '$($record.Path)' descends through non-directory '$ancestor' ($($byPath[$ancestor].Kind))."
            }
        }

        if ($record.Kind -eq "symlink") {
            $record.ResolvedTarget = Resolve-ReleaseArchiveSymbolicLinkTarget `
                -LinkPath $record.Path `
                -Target $record.Target `
                -RootName $rootName `
                -Label $Label
        } elseif ($record.Kind -eq "hardlink") {
            $record.ResolvedTarget = ConvertTo-ReleaseArchivePortablePath `
                -Path $record.Target `
                -Label "$Label hard-link target"
            if ($record.ResolvedTarget -ne $rootName -and
                -not $record.ResolvedTarget.StartsWith("$rootName/", [StringComparison]::Ordinal)) {
                throw "$Label hard link '$($record.Path)' targets path '$($record.Target)' outside archive root '$rootName'."
            }
        }
    }

    foreach ($record in $Records) {
        if ($record.Kind -eq "hardlink") {
            if (-not $byPath.ContainsKey($record.ResolvedTarget)) {
                throw "$Label hard link '$($record.Path)' has dangling target '$($record.Target)'."
            }
            $directTarget = $byPath[$record.ResolvedTarget]
            if ($directTarget.Kind -ne "file" -and $directTarget.Kind -ne "hardlink") {
                throw "$Label hard link '$($record.Path)' targets non-file '$($record.Target)' ($($directTarget.Kind))."
            }
        }

        if ($record.Kind -eq "symlink") {
            $active = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            [void](Resolve-ReleaseArchiveSymbolicGraphTarget `
                -LinkRecord $record `
                -ByPath $byPath `
                -RootName $rootName `
                -ActiveLinks $active `
                -Label $Label)
        } elseif ($record.Kind -eq "hardlink") {
            $active = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $resolved = Resolve-ReleaseArchiveGraphPath `
                -Path $record.ResolvedTarget `
                -ByPath $byPath `
                -RootName $rootName `
                -ActiveLinks $active `
                -Label $Label
            if ($resolved.Kind -ne "file") {
                throw "$Label hard link '$($record.Path)' does not resolve to a regular file."
            }
            $record.UltimateTarget = $resolved.Path
        }
    }

    return [pscustomobject]@{
        RootName = $rootName
        Records = $Records
    }
}

function Get-ReleaseArchiveDestination {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $PortablePath
    )

    $candidate = [System.IO.Path]::GetFullPath((Join-Path $Root ($PortablePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $rootWithSeparator = $Root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($rootWithSeparator, $comparison)) {
        throw "Validated archive path '$PortablePath' escaped extraction root '$Root'."
    }
    return $candidate
}

function Set-ReleaseArchiveExtractedMode {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Kind,
        [Parameter(Mandatory = $true)] [int] $SourceMode
    )

    if ($IsWindows -or $Kind -eq "symlink" -or $Kind -eq "hardlink") {
        return
    }
    $mode = if ($Kind -eq "directory" -or ($SourceMode -band 0x49) -ne 0) { 0x1ed } else { 0x1a4 }
    [System.IO.File]::SetUnixFileMode($Path, [System.IO.UnixFileMode]$mode)
}

function Write-ReleaseArchiveRegularFile {
    param(
        [Parameter(Mandatory = $true)] [System.IO.Stream] $InputStream,
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [long] $ExpectedLength,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $output = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $copied = 0L
    $buffer = [byte[]]::new(1024 * 1024)
    try {
        while (($read = $InputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $copied += $read
            if ($copied -gt $ExpectedLength) {
                throw "$Label yielded more than its declared $ExpectedLength bytes."
            }
            $output.Write($buffer, 0, $read)
        }
    } catch {
        $output.Dispose()
        [System.IO.File]::Delete($Path)
        throw
    } finally {
        $output.Dispose()
    }
    if ($copied -ne $ExpectedLength) {
        [System.IO.File]::Delete($Path)
        throw "$Label yielded $copied bytes, expected $ExpectedLength."
    }
}

function Initialize-ReleaseArchiveDirectories {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Records,
        [Parameter(Mandatory = $true)] [string] $DestinationRoot
    )

    $directories = @($Records | Where-Object Kind -eq "directory" | Sort-Object `
        @{ Expression = { $_.Path.Split('/').Length }; Ascending = $true },
        @{ Expression = { $_.Path }; Ascending = $true })
    foreach ($record in $directories) {
        $path = Get-ReleaseArchiveDestination -Root $DestinationRoot -PortablePath $record.Path
        [void][System.IO.Directory]::CreateDirectory($path)
        Set-ReleaseArchiveExtractedMode -Path $path -Kind "directory" -SourceMode $record.Mode
    }
}

function Complete-ReleaseArchiveLinks {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Records,
        [Parameter(Mandatory = $true)] [string] $DestinationRoot,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    foreach ($record in @($Records | Where-Object Kind -eq "hardlink" | Sort-Object Path)) {
        $path = Get-ReleaseArchiveDestination -Root $DestinationRoot -PortablePath $record.Path
        $target = Get-ReleaseArchiveDestination -Root $DestinationRoot -PortablePath $record.UltimateTarget
        [void](New-Item -ItemType HardLink -Path $path -Target $target -ErrorAction Stop)
    }
    foreach ($record in @($Records | Where-Object Kind -eq "symlink" | Sort-Object Path)) {
        $path = Get-ReleaseArchiveDestination -Root $DestinationRoot -PortablePath $record.Path
        [void][System.IO.File]::CreateSymbolicLink($path, $record.Target)
    }

    foreach ($record in @($Records | Where-Object Kind -eq "symlink")) {
        $path = Get-ReleaseArchiveDestination -Root $DestinationRoot -PortablePath $record.Path
        try {
            $target = [System.IO.File]::ResolveLinkTarget($path, $true)
        } catch {
            throw "$Label symbolic link '$($record.Path)' could not be resolved after extraction: $($_.Exception.Message)"
        }
        if ($null -eq $target) {
            throw "$Label symbolic link '$($record.Path)' is dangling after extraction."
        }
        $resolved = [System.IO.Path]::GetFullPath($target.FullName)
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        $rootWithSeparator = $DestinationRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not [string]::Equals($resolved, $DestinationRoot, $comparison) -and
            -not $resolved.StartsWith($rootWithSeparator, $comparison)) {
            throw "$Label symbolic link '$($record.Path)' resolves outside extraction root."
        }
    }
}

function Expand-ValidatedReleaseZipArchive {
    param(
        [Parameter(Mandatory = $true)] [System.IO.Stream] $Stream,
        [Parameter(Mandatory = $true)] [object[]] $Records,
        [Parameter(Mandatory = $true)] [string] $DestinationRoot,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $archive = [System.IO.Compression.ZipArchive]::new(
        $Stream,
        [System.IO.Compression.ZipArchiveMode]::Read,
        $true)
    try {
        if ($archive.Entries.Count -ne $Records.Count) {
            throw "$Label changed between preflight and extraction."
        }
        foreach ($record in $Records) {
            $entry = $archive.Entries[$record.Index]
            if (-not [string]::Equals($entry.FullName, $record.RawName, [StringComparison]::Ordinal) -or
                ($record.Kind -eq "file" -and [long]$entry.Length -ne $record.Size)) {
                throw "$Label changed between preflight and extraction at '$($record.Path)'."
            }
            if ($record.Kind -ne "file") {
                continue
            }
            $path = Get-ReleaseArchiveDestination -Root $DestinationRoot -PortablePath $record.Path
            $input = $entry.Open()
            try {
                Write-ReleaseArchiveRegularFile `
                    -InputStream $input `
                    -Path $path `
                    -ExpectedLength $record.Size `
                    -Label "$Label ZIP entry '$($record.Path)'"
            } finally {
                $input.Dispose()
            }
            Set-ReleaseArchiveExtractedMode -Path $path -Kind "file" -SourceMode $record.Mode
        }
    } finally {
        $archive.Dispose()
    }
}

function Expand-ValidatedReleaseTarArchive {
    param(
        [Parameter(Mandatory = $true)] [System.IO.Stream] $Stream,
        [Parameter(Mandatory = $true)] [object[]] $Records,
        [Parameter(Mandatory = $true)] [string] $DestinationRoot,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $gzip = [System.IO.Compression.GZipStream]::new(
        $Stream,
        [System.IO.Compression.CompressionMode]::Decompress,
        $true)
    $reader = [System.Formats.Tar.TarReader]::new($gzip, $true)
    try {
        $index = 0
        while ($null -ne ($entry = $reader.GetNextEntry($false))) {
            if ($index -ge $Records.Count) {
                throw "$Label changed between preflight and extraction."
            }
            $record = $Records[$index]
            $current = Get-ReleaseTarArchiveRecord -Entry $entry -Label $Label -Index $index
            if (-not [string]::Equals($current.Path, $record.Path, [StringComparison]::Ordinal) -or
                -not [string]::Equals($current.Kind, $record.Kind, [StringComparison]::Ordinal) -or
                -not [string]::Equals($current.Target, $record.Target, [StringComparison]::Ordinal) -or
                $current.Size -ne $record.Size) {
                throw "$Label changed between preflight and extraction at '$($record.Path)'."
            }
            if ($record.Kind -eq "file") {
                if ($null -eq $entry.DataStream) {
                    throw "$Label TAR entry '$($record.Path)' has no readable payload."
                }
                $path = Get-ReleaseArchiveDestination -Root $DestinationRoot -PortablePath $record.Path
                Write-ReleaseArchiveRegularFile `
                    -InputStream $entry.DataStream `
                    -Path $path `
                    -ExpectedLength $record.Size `
                    -Label "$Label TAR entry '$($record.Path)'"
                Set-ReleaseArchiveExtractedMode -Path $path -Kind "file" -SourceMode $record.Mode
            }
            $index++
        }
        if ($index -ne $Records.Count) {
            throw "$Label changed between preflight and extraction."
        }
    } finally {
        $reader.Dispose()
        $gzip.Dispose()
    }
}

function Expand-ValidatedReleaseArchive {
    param(
        [Parameter(Mandatory = $true)] [string] $ArchivePath,
        [Parameter(Mandatory = $true)] [string] $DestinationPath
    )

    $ErrorActionPreference = "Stop"

    $archive = [System.IO.Path]::GetFullPath($ArchivePath)
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "Release archive '$archive' does not exist."
    }
    if ($archive.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase)) {
        $kind = "zip"
    } elseif ($archive.EndsWith(".tar.gz", [StringComparison]::OrdinalIgnoreCase)) {
        $kind = "targz"
    } else {
        throw "Release archive '$archive' must end with .zip or .tar.gz."
    }

    $destination = [System.IO.Path]::GetFullPath($DestinationPath)
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "Release extraction destination '$destination' must be an existing directory."
    }
    $destinationItem = Get-Item -LiteralPath $destination -Force
    if (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release extraction destination '$destination' must not be a reparse point."
    }
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -ne 0) {
        throw "Release extraction destination '$destination' must be empty."
    }

    $label = "release archive '$archive'"
    $stream = [System.IO.FileStream]::new(
        $archive,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    try {
        $records = if ($kind -eq "zip") {
            @(Get-ReleaseZipArchiveRecords -Stream $stream -Label $label)
        } else {
            @(Get-ReleaseTarArchiveRecords -Stream $stream -Label $label)
        }
        $preflight = Test-ReleaseArchiveRecords -Records $records -Label $label

        # No extraction path is created before the complete member and link
        # graph has passed the preflight above. The same locked stream is then
        # rewound so the validated archive cannot be replaced between passes.
        $stream.Position = 0
        Initialize-ReleaseArchiveDirectories -Records $preflight.Records -DestinationRoot $destination
        if ($kind -eq "zip") {
            Expand-ValidatedReleaseZipArchive `
                -Stream $stream `
                -Records $preflight.Records `
                -DestinationRoot $destination `
                -Label $label
        } else {
            Expand-ValidatedReleaseTarArchive `
                -Stream $stream `
                -Records $preflight.Records `
                -DestinationRoot $destination `
                -Label $label
        }
        Complete-ReleaseArchiveLinks `
            -Records $preflight.Records `
            -DestinationRoot $destination `
            -Label $label
    } finally {
        $stream.Dispose()
    }

    return Get-ReleaseArchiveDestination -Root $destination -PortablePath $preflight.RootName
}
