param(
    [string] $RepositoryRoot = "",

    [string] $OutputPath = "",

    [string] $AllowlistPath = "",

    [ValidateRange(1, [long]::MaxValue)]
    [long] $LargeFileThresholdBytes = 10MB,

    [ValidateRange(1, [long]::MaxValue)]
    [long] $MaxContentScanBytes = 64MB,

    [switch] $IncludeHistory,

    [switch] $TrackedOnly,

    [switch] $NoFail
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$findings = [System.Collections.ArrayList]::new()
$suppressedFindings = [System.Collections.ArrayList]::new()
$scannerErrors = [System.Collections.ArrayList]::new()
$currentFileCount = 0
$historyBlobCount = 0
$allowlistEntries = @()

function Invoke-CapturedTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FileName,

        [string[]] $Arguments = @()
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "tool-start-failed"
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    [void] $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        # Deliberately do not surface stderr: a failed content command could have
        # included material that this audit exists to keep out of logs.
        throw "tool-exit-$($process.ExitCode)"
    }

    return $stdout
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    return Invoke-CapturedTool -FileName "git" -Arguments (@("-C", $Root) + $Arguments)
}

function Get-Sha256Hex {
    param([byte[]] $Bytes)

    $hash = [System.Security.Cryptography.SHA256]::HashData($Bytes)
    return [Convert]::ToHexString($hash).ToLowerInvariant()
}

function Get-FindingFingerprint {
    param(
        [string] $RuleId,
        [string] $MatchedValue
    )

    return Get-Sha256Hex -Bytes ([Text.Encoding]::UTF8.GetBytes($RuleId + [char]0 + $MatchedValue))
}

function Add-ScannerError {
    param(
        [string] $Code,
        [string] $Path = $null,
        [string] $Message = "The audit could not completely inspect this input."
    )

    [void] $scannerErrors.Add([pscustomobject][ordered]@{
        code = $Code
        path = $Path
        message = $Message
    })
}

function Get-RequiredJsonProperty {
    param(
        [object] $Object,
        [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "allowlist-invalid"
    }

    return $property.Value
}

function Read-Allowlist {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "allowlist-missing"
    }

    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ((Get-RequiredJsonProperty -Object $manifest -Name "schemaVersion") -ne 1) {
        throw "allowlist-schema-unsupported"
    }

    $entries = @(Get-RequiredJsonProperty -Object $manifest -Name "entries")
    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        $ruleId = [string](Get-RequiredJsonProperty -Object $entry -Name "ruleId")
        $path = [string](Get-RequiredJsonProperty -Object $entry -Name "path")
        $fingerprint = [string](Get-RequiredJsonProperty -Object $entry -Name "fingerprint")
        $reason = [string](Get-RequiredJsonProperty -Object $entry -Name "reason")
        $sourceProperty = $entry.PSObject.Properties["source"]
        $objectIdProperty = $entry.PSObject.Properties["objectId"]
        $source = if ($null -eq $sourceProperty) { "" } else { [string]$sourceProperty.Value }
        $objectId = if ($null -eq $objectIdProperty) { "" } else { [string]$objectIdProperty.Value }

        if ([string]::IsNullOrWhiteSpace($ruleId) `
            -or [string]::IsNullOrWhiteSpace($path) `
            -or $path.Contains("*") `
            -or $path.Contains("?") `
            -or $fingerprint -notmatch '^[a-f0-9]{64}$' `
            -or [string]::IsNullOrWhiteSpace($reason) `
            -or ($source -ne "" -and $source -notin @("current-tree", "history")) `
            -or ($objectId -ne "" -and $objectId -notmatch '^[a-f0-9]{40,64}$')) {
            throw "allowlist-entry-invalid"
        }

        $key = "$ruleId`0$path`0$fingerprint`0$source`0$objectId"
        if (-not $keys.Add($key)) {
            throw "allowlist-entry-duplicate"
        }
    }

    return $entries
}

function Find-AllowlistEntry {
    param([object] $Finding)

    foreach ($entry in $allowlistEntries) {
        if ([string]$entry.ruleId -ne [string]$Finding.ruleId `
            -or [string]$entry.path -ne [string]$Finding.path `
            -or [string]$entry.fingerprint -ne [string]$Finding.fingerprint) {
            continue
        }

        $sourceProperty = $entry.PSObject.Properties["source"]
        if ($null -ne $sourceProperty `
            -and -not [string]::IsNullOrWhiteSpace([string]$sourceProperty.Value) `
            -and [string]$sourceProperty.Value -ne [string]$Finding.source) {
            continue
        }

        $objectIdProperty = $entry.PSObject.Properties["objectId"]
        if ($null -ne $objectIdProperty `
            -and -not [string]::IsNullOrWhiteSpace([string]$objectIdProperty.Value) `
            -and [string]$objectIdProperty.Value -ne [string]$Finding.objectId) {
            continue
        }

        return $entry
    }

    return $null
}

function Add-Finding {
    param(
        [string] $RuleId,
        [string] $Severity,
        [string] $Source,
        [string] $Path,
        [string] $ObjectId,
        [Nullable[int]] $Line,
        [Nullable[int]] $Column,
        [Nullable[long]] $SizeBytes,
        [string] $Fingerprint,
        [string] $Message
    )

    $finding = [pscustomobject][ordered]@{
        ruleId = $RuleId
        severity = $Severity
        source = $Source
        path = $Path
        objectId = $ObjectId
        line = $Line
        column = $Column
        sizeBytes = $SizeBytes
        fingerprint = $Fingerprint
        message = $Message
    }
    $allowlistEntry = Find-AllowlistEntry -Finding $finding
    if ($null -eq $allowlistEntry) {
        [void] $findings.Add($finding)
        return
    }

    [void] $suppressedFindings.Add([pscustomobject][ordered]@{
        ruleId = $finding.ruleId
        severity = $finding.severity
        source = $finding.source
        path = $finding.path
        objectId = $finding.objectId
        line = $finding.line
        column = $finding.column
        sizeBytes = $finding.sizeBytes
        fingerprint = $finding.fingerprint
        message = $finding.message
        allowlistReason = [string]$allowlistEntry.reason
    })
}

function Test-IsSafeRelativePath {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) `
        -or [IO.Path]::IsPathRooted($Path) `
        -or $Path.Contains([char]0) `
        -or $Path -match '(^|/|\\)\.\.(/|\\|$)') {
        return $false
    }

    return $true
}

function Resolve-TreePath {
    param(
        [string] $Root,
        [string] $RelativePath
    )

    if (-not (Test-IsSafeRelativePath -Path $RelativePath)) {
        throw "unsafe-tree-path"
    }

    $platformRelativePath = $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $platformRelativePath))
    $normalizedRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals($candidate, $normalizedRoot, $comparison) `
        -and -not $candidate.StartsWith($normalizedRoot + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw "unsafe-tree-path"
    }

    return $candidate
}

function Get-NullSeparatedValues {
    param([string] $Value)

    return @($Value.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
}

function Test-IsSensitiveCredentialPath {
    param([string] $Path)

    $leaf = [IO.Path]::GetFileName($Path).ToLowerInvariant()
    $extension = [IO.Path]::GetExtension($leaf).ToLowerInvariant()
    if ($leaf -in @(
        ".env", ".npmrc", ".pypirc", ".netrc", "credentials.json",
        "secrets.json", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519"
    )) {
        return $true
    }

    return $extension -in @(".key", ".p12", ".pfx", ".jks", ".keystore")
}

function Test-IsBinaryContent {
    param(
        [byte[]] $Bytes,
        [string] $Path
    )

    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($extension -in @(
        ".7z", ".a", ".avi", ".bc", ".bmp", ".bz2", ".class", ".dat",
        ".dll", ".dylib", ".eot", ".exe", ".flac", ".gif", ".gz", ".ico",
        ".jar", ".jpeg", ".jpg", ".lib", ".mov", ".mp3", ".mp4", ".o",
        ".obj", ".otf", ".pdf", ".pdb", ".png", ".so", ".tar", ".tgz",
        ".ttf", ".wasm", ".wav", ".webm", ".woff", ".woff2", ".xz", ".zip"
    )) {
        return $true
    }

    if ($Bytes.Length -ge 2 `
        -and (($Bytes[0] -eq 0xff -and $Bytes[1] -eq 0xfe) `
            -or ($Bytes[0] -eq 0xfe -and $Bytes[1] -eq 0xff))) {
        return $false
    }

    $limit = [Math]::Min($Bytes.Length, 8192)
    for ($index = 0; $index -lt $limit; $index++) {
        if ($Bytes[$index] -eq 0) {
            return $true
        }
    }

    return $false
}

function Convert-BytesToText {
    param([byte[]] $Bytes)

    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xff -and $Bytes[1] -eq 0xfe) {
        return [Text.Encoding]::Unicode.GetString($Bytes)
    }

    if ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xfe -and $Bytes[1] -eq 0xff) {
        return [Text.Encoding]::BigEndianUnicode.GetString($Bytes)
    }

    return [Text.UTF8Encoding]::new($false, $false).GetString($Bytes)
}

function Test-IsPlaceholderSecret {
    param([string] $Value)

    $trimmed = $Value.Trim().Trim('"', "'")
    if ($trimmed -match '^(\$|%|<|\{|\[|\*+)') {
        return $true
    }

    return $trimmed -match '^(?i:example|sample|placeholder|changeme|change-me|redacted|none|null|your[-_])'
}

function Test-LooksLikeOpaqueSecret {
    param([string] $Value)

    if ($Value.Length -lt 20) {
        return $false
    }

    $characterClassCount = 0
    if ($Value -cmatch '[a-z]') { $characterClassCount++ }
    if ($Value -cmatch '[A-Z]') { $characterClassCount++ }
    if ($Value -match '[0-9]') { $characterClassCount++ }
    if ($Value -match '[^A-Za-z0-9]') { $characterClassCount++ }
    return $characterClassCount -ge 3
}

function Test-IsPrivateIpAddress {
    param([Net.IPAddress] $Address)

    if ([Net.IPAddress]::IsLoopback($Address)) {
        return $true
    }

    if ($Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
        $bytes = $Address.GetAddressBytes()
        return $bytes[0] -eq 10 `
            -or $bytes[0] -eq 127 `
            -or ($bytes[0] -eq 169 -and $bytes[1] -eq 254) `
            -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) `
            -or ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
    }

    if ($Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
        $bytes = $Address.GetAddressBytes()
        return $Address.IsIPv6LinkLocal `
            -or $Address.IsIPv6SiteLocal `
            -or (($bytes[0] -band 0xfe) -eq 0xfc)
    }

    return $false
}

function Add-TextContentFindings {
    param(
        [byte[]] $Bytes,
        [string] $Source,
        [string] $Path,
        [string] $ObjectId
    )

    $text = Convert-BytesToText -Bytes $Bytes
    $patterns = @(
        [pscustomobject]@{
            ruleId = "private-key"
            severity = "error"
            regex = [regex]::new('-----BEGIN[ \t]+(?:RSA|DSA|EC|OPENSSH|PGP)?[ \t]*PRIVATE[ \t]+KEY-----', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            message = "A private-key marker is present."
            valueGroup = ""
        },
        [pscustomobject]@{
            ruleId = "aws-access-key"
            severity = "error"
            regex = [regex]::new('(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])')
            message = "A value shaped like an AWS access-key identifier is present."
            valueGroup = ""
        },
        [pscustomobject]@{
            ruleId = "github-token"
            severity = "error"
            regex = [regex]::new('(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{70,255})(?![A-Za-z0-9_])')
            message = "A value shaped like a GitHub token is present."
            valueGroup = ""
        },
        [pscustomobject]@{
            ruleId = "slack-token"
            severity = "error"
            regex = [regex]::new('(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9-]{10,255}(?![A-Za-z0-9])')
            message = "A value shaped like a Slack token is present."
            valueGroup = ""
        },
        [pscustomobject]@{
            ruleId = "live-secret-key"
            severity = "error"
            regex = [regex]::new('(?<![A-Za-z0-9_])sk_live_[A-Za-z0-9]{16,255}(?![A-Za-z0-9_])')
            message = "A value shaped like a live secret key is present."
            valueGroup = ""
        },
        [pscustomobject]@{
            ruleId = "google-api-key"
            severity = "error"
            regex = [regex]::new('(?<![A-Za-z0-9_-])AIza[0-9A-Za-z_-]{35}(?![A-Za-z0-9_-])')
            message = "A value shaped like a Google API key is present."
            valueGroup = ""
        },
        [pscustomobject]@{
            ruleId = "assigned-secret"
            severity = "error"
            regex = [regex]::new('(?i)\b(?<name>password|passwd|pwd|secret|api[-_]?key|client[-_]?secret|private[-_]?key)\s*[:=]\s*(?:(?<quote>["''])(?<value>[^\s"''`,;#]{8,})\k<quote>|(?<value>[^\s"''`,;#]{16,}))')
            message = "A non-placeholder value is assigned to a credential-like name."
            valueGroup = "value"
        },
        [pscustomobject]@{
            ruleId = "personal-absolute-path"
            severity = "warning"
            regex = [regex]::new('(?<![A-Za-z0-9_])(?:/(?:Users|home)/(?<user>[^/\s"''`<>]+)/[^\s"''`<>]*|[A-Za-z]:\\Users\\(?<user>[^\\\s"''`<>]+)\\[^\s"''`<>]*)')
            message = "A user-specific absolute filesystem path is present."
            valueGroup = ""
        },
        [pscustomobject]@{
            ruleId = "possible-us-ssn"
            severity = "error"
            regex = [regex]::new('(?<!\d)(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}(?!\d)')
            message = "A value shaped like a United States Social Security number is present."
            valueGroup = ""
        }
    )
    $urlRegex = [regex]::new('(?i)\bhttps?://[^\s<>"'']+')
    $reader = [IO.StringReader]::new($text)
    $lineNumber = 0
    while (($line = $reader.ReadLine()) -ne $null) {
        $lineNumber++
        foreach ($pattern in $patterns) {
            foreach ($match in $pattern.regex.Matches($line)) {
                $matchedValue = if ([string]::IsNullOrEmpty($pattern.valueGroup)) {
                    $match.Value
                } else {
                    $match.Groups[$pattern.valueGroup].Value
                }
                if ($pattern.ruleId -eq "assigned-secret" -and (Test-IsPlaceholderSecret -Value $matchedValue)) {
                    continue
                }
                if ($pattern.ruleId -eq "assigned-secret") {
                    $wasQuoted = $match.Groups["quote"].Success
                    if (-not $wasQuoted `
                        -and -not (Test-LooksLikeOpaqueSecret -Value $matchedValue)) {
                        continue
                    }
                }

                if ($pattern.ruleId -eq "personal-absolute-path") {
                    $userName = $match.Groups["user"].Value
                    if ($userName -match '^(?i:shared|public|default|user|username|name|example)$') {
                        continue
                    }
                }

                Add-Finding `
                    -RuleId $pattern.ruleId `
                    -Severity $pattern.severity `
                    -Source $Source `
                    -Path $Path `
                    -ObjectId $ObjectId `
                    -Line $lineNumber `
                    -Column ($match.Index + 1) `
                    -SizeBytes $null `
                    -Fingerprint (Get-FindingFingerprint -RuleId $pattern.ruleId -MatchedValue $matchedValue) `
                    -Message $pattern.message
            }
        }

        foreach ($match in $urlRegex.Matches($line)) {
            $candidate = $match.Value.TrimEnd(')', ']', '}', ',', ';', '.')
            $uri = $null
            if (-not [Uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$uri)) {
                continue
            }

            $ruleId = $null
            $message = $null
            if (-not [string]::IsNullOrWhiteSpace($uri.UserInfo)) {
                $ruleId = "url-embedded-credentials"
                $message = "A URL contains embedded user information."
            } else {
                $urlHost = $uri.DnsSafeHost.ToLowerInvariant()
                $address = $null
                $isPrivateHost = $urlHost -eq "localhost" `
                    -or $urlHost.EndsWith(".localhost", [StringComparison]::Ordinal) `
                    -or $urlHost.EndsWith(".local", [StringComparison]::Ordinal) `
                    -or $urlHost.EndsWith(".internal", [StringComparison]::Ordinal) `
                    -or $urlHost.EndsWith(".corp", [StringComparison]::Ordinal) `
                    -or $urlHost.EndsWith(".lan", [StringComparison]::Ordinal)
                if ([Net.IPAddress]::TryParse($urlHost, [ref]$address)) {
                    $isPrivateHost = Test-IsPrivateIpAddress -Address $address
                }

                if ($isPrivateHost) {
                    $ruleId = "private-url"
                    $message = "A URL names a loopback, private-address, or private-DNS host."
                }
            }

            if ($null -ne $ruleId) {
                Add-Finding `
                    -RuleId $ruleId `
                    -Severity "warning" `
                    -Source $Source `
                    -Path $Path `
                    -ObjectId $ObjectId `
                    -Line $lineNumber `
                    -Column ($match.Index + 1) `
                    -SizeBytes $null `
                    -Fingerprint (Get-FindingFingerprint -RuleId $ruleId -MatchedValue $candidate) `
                    -Message $message
            }
        }
    }
}

function Read-FilePrefix {
    param(
        [string] $Path,
        [int] $Limit = 8192
    )

    $stream = [IO.File]::OpenRead($Path)
    try {
        $length = [Math]::Min([long]$Limit, $stream.Length)
        $bytes = [byte[]]::new([int]$length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) {
                break
            }
            $offset += $read
        }

        if ($offset -eq $bytes.Length) {
            return ,$bytes
        }

        return ,$bytes[0..($offset - 1)]
    } finally {
        $stream.Dispose()
    }
}

function Scan-CurrentTree {
    param([string] $Root)

    $trackedOutput = Invoke-GitText -Root $Root -Arguments @("ls-files", "-z", "--cached")
    $trackedPaths = Get-NullSeparatedValues -Value $trackedOutput
    $trackedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($trackedPath in $trackedPaths) {
        [void] $trackedSet.Add($trackedPath)
    }

    $arguments = @("ls-files", "-z", "--cached")
    if (-not $TrackedOnly) {
        $arguments += @("--others", "--exclude-standard")
    }
    $candidatePaths = @(Get-NullSeparatedValues -Value (Invoke-GitText -Root $Root -Arguments $arguments) | Sort-Object -Unique)

    foreach ($relativePath in $candidatePaths) {
        try {
            $fullPath = Resolve-TreePath -Root $Root -RelativePath $relativePath
            if (-not (Test-Path -LiteralPath $fullPath)) {
                # A tracked deletion in a working tree has no current content to scan.
                continue
            }

            $item = Get-Item -LiteralPath $fullPath -Force
            if ($item.PSIsContainer) {
                Add-ScannerError -Code "unsupported-gitlink" -Path $relativePath
                continue
            }

            $script:currentFileCount++
            $isTracked = $trackedSet.Contains($relativePath)
            $isLink = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
            if ($isLink) {
                $linkTarget = [string]$item.LinkTarget
                $bytes = [Text.Encoding]::UTF8.GetBytes($linkTarget)
                $length = $bytes.LongLength
                $prefix = $bytes
            } else {
                $length = [long]$item.Length
                $prefix = Read-FilePrefix -Path $fullPath
                $bytes = $null
            }

            $isBinary = Test-IsBinaryContent -Bytes $prefix -Path $relativePath
            if (Test-IsSensitiveCredentialPath -Path $relativePath) {
                $contentHash = if ($isLink) {
                    Get-Sha256Hex -Bytes $bytes
                } else {
                    (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
                }
                Add-Finding `
                    -RuleId "credential-file" `
                    -Severity "error" `
                    -Source "current-tree" `
                    -Path $relativePath `
                    -ObjectId $null `
                    -Line $null `
                    -Column $null `
                    -SizeBytes $length `
                    -Fingerprint $contentHash `
                    -Message "A credential-associated filename is present."
            }

            if ($isBinary -and $length -ge $LargeFileThresholdBytes) {
                $ruleId = if ($isTracked) { "large-tracked-binary" } else { "large-untracked-binary" }
                $contentHash = if ($isLink) {
                    Get-Sha256Hex -Bytes $bytes
                } else {
                    (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
                }
                Add-Finding `
                    -RuleId $ruleId `
                    -Severity "warning" `
                    -Source "current-tree" `
                    -Path $relativePath `
                    -ObjectId $null `
                    -Line $null `
                    -Column $null `
                    -SizeBytes $length `
                    -Fingerprint $contentHash `
                    -Message "A binary file exceeds the repository audit size threshold."
            }

            if ($isBinary) {
                continue
            }

            if ($length -gt $MaxContentScanBytes) {
                Add-ScannerError `
                    -Code "text-file-over-scan-limit" `
                    -Path $relativePath `
                    -Message "A text file exceeds the configured content-scan limit."
                continue
            }

            if (-not $isLink) {
                $bytes = [IO.File]::ReadAllBytes($fullPath)
            }
            Add-TextContentFindings `
                -Bytes $bytes `
                -Source "current-tree" `
                -Path $relativePath `
                -ObjectId $null
        } catch {
            Add-ScannerError -Code "current-file-scan-failed" -Path $relativePath
        }
    }
}

function Read-AsciiStreamLine {
    param([IO.Stream] $Stream)

    $bytes = [Collections.Generic.List[byte]]::new()
    while ($true) {
        $value = $Stream.ReadByte()
        if ($value -lt 0) {
            if ($bytes.Count -eq 0) {
                return $null
            }
            throw "unexpected-batch-eof"
        }
        if ($value -eq 10) {
            break
        }
        if ($bytes.Count -ge 1024) {
            throw "batch-header-too-long"
        }
        [void] $bytes.Add([byte]$value)
    }

    return [Text.Encoding]::ASCII.GetString($bytes.ToArray())
}

function Read-ExactStreamBytes {
    param(
        [IO.Stream] $Stream,
        [int] $Count
    )

    $bytes = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($bytes, $offset, $Count - $offset)
        if ($read -eq 0) {
            throw "unexpected-batch-eof"
        }
        $offset += $read
    }
    return ,$bytes
}

function Discard-ExactStreamBytes {
    param(
        [IO.Stream] $Stream,
        [long] $Count
    )

    $buffer = [byte[]]::new(8192)
    $remaining = $Count
    while ($remaining -gt 0) {
        $requested = [int][Math]::Min([long]$buffer.Length, $remaining)
        $read = $Stream.Read($buffer, 0, $requested)
        if ($read -eq 0) {
            throw "unexpected-batch-eof"
        }
        $remaining -= $read
    }
}

function Scan-GitHistory {
    param([string] $Root)

    $objectOutput = Invoke-GitText `
        -Root $Root `
        -Arguments @("rev-list", "--objects", "--all", "--filter=object:type=blob")
    $entries = @()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in ($objectOutput -replace "`r", "" -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line) `
            -or $line -notmatch '^([a-f0-9]{40,64})(?: (.*))?$') {
            continue
        }
        $objectId = $Matches[1]
        if (-not $seen.Add($objectId)) {
            continue
        }
        $path = if ($Matches.Count -ge 3 -and -not [string]::IsNullOrWhiteSpace($Matches[2])) {
            $Matches[2]
        } else {
            $null
        }
        $entries += [pscustomobject]@{ objectId = $objectId; path = $path }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "git"
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @("-C", $Root, "cat-file", "--batch")) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "history-batch-start-failed"
    }

    try {
        $stream = $process.StandardOutput.BaseStream
        foreach ($entry in $entries) {
            $process.StandardInput.WriteLine($entry.objectId)
            $process.StandardInput.Flush()
            $header = Read-AsciiStreamLine -Stream $stream
            if ($null -eq $header `
                -or $header -notmatch '^([a-f0-9]{40,64}) ([a-z]+) ([0-9]+)$' `
                -or $Matches[1] -ne $entry.objectId) {
                throw "history-batch-invalid-header"
            }

            $objectType = $Matches[2]
            $size = [long]$Matches[3]
            if ($objectType -ne "blob") {
                Discard-ExactStreamBytes -Stream $stream -Count $size
                if ($stream.ReadByte() -ne 10) {
                    throw "history-batch-invalid-delimiter"
                }
                continue
            }

            $script:historyBlobCount++
            if ($size -le $MaxContentScanBytes) {
                $bytes = Read-ExactStreamBytes -Stream $stream -Count ([int]$size)
                if ($stream.ReadByte() -ne 10) {
                    throw "history-batch-invalid-delimiter"
                }
                if (Test-IsSensitiveCredentialPath -Path ([string]$entry.path)) {
                    Add-Finding `
                        -RuleId "credential-file" `
                        -Severity "error" `
                        -Source "history" `
                        -Path $entry.path `
                        -ObjectId $entry.objectId `
                        -Line $null `
                        -Column $null `
                        -SizeBytes $size `
                        -Fingerprint (Get-Sha256Hex -Bytes $bytes) `
                        -Message "A credential-associated filename is present."
                }
                $isBinary = Test-IsBinaryContent -Bytes $bytes -Path ([string]$entry.path)
                if ($isBinary -and $size -ge $LargeFileThresholdBytes) {
                    Add-Finding `
                        -RuleId "large-history-binary" `
                        -Severity "warning" `
                        -Source "history" `
                        -Path $entry.path `
                        -ObjectId $entry.objectId `
                        -Line $null `
                        -Column $null `
                        -SizeBytes $size `
                        -Fingerprint (Get-Sha256Hex -Bytes $bytes) `
                        -Message "A historical binary blob exceeds the repository audit size threshold."
                }
                if (-not $isBinary) {
                    Add-TextContentFindings `
                        -Bytes $bytes `
                        -Source "history" `
                        -Path $entry.path `
                        -ObjectId $entry.objectId
                }
                continue
            }

            $prefixLength = [int][Math]::Min([long]8192, $size)
            $prefix = Read-ExactStreamBytes -Stream $stream -Count $prefixLength
            Discard-ExactStreamBytes -Stream $stream -Count ($size - $prefixLength)
            if ($stream.ReadByte() -ne 10) {
                throw "history-batch-invalid-delimiter"
            }
            if (Test-IsSensitiveCredentialPath -Path ([string]$entry.path)) {
                Add-Finding `
                    -RuleId "credential-file" `
                    -Severity "error" `
                    -Source "history" `
                    -Path $entry.path `
                    -ObjectId $entry.objectId `
                    -Line $null `
                    -Column $null `
                    -SizeBytes $size `
                    -Fingerprint (Get-FindingFingerprint -RuleId "credential-file" -MatchedValue $entry.objectId) `
                    -Message "A credential-associated filename is present."
            }
            $isBinary = Test-IsBinaryContent -Bytes $prefix -Path ([string]$entry.path)
            if ($isBinary -and $size -ge $LargeFileThresholdBytes) {
                Add-Finding `
                    -RuleId "large-history-binary" `
                    -Severity "warning" `
                    -Source "history" `
                    -Path $entry.path `
                    -ObjectId $entry.objectId `
                    -Line $null `
                    -Column $null `
                    -SizeBytes $size `
                    -Fingerprint (Get-FindingFingerprint -RuleId "large-history-binary" -MatchedValue $entry.objectId) `
                    -Message "A historical binary blob exceeds the repository audit size threshold."
            } else {
                Add-ScannerError `
                    -Code "history-text-blob-over-scan-limit" `
                    -Path $entry.path `
                    -Message "A historical text blob exceeds the configured content-scan limit."
            }
        }

        $process.StandardInput.Close()
        $process.WaitForExit()
        [void] $process.StandardError.ReadToEnd()
        if ($process.ExitCode -ne 0) {
            throw "history-batch-failed"
        }
    } finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
        $process.Dispose()
    }
}

try {
    $rootCandidate = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        Join-Path $PSScriptRoot ".."
    } else {
        $RepositoryRoot
    }
    $resolvedRoot = (Resolve-Path -LiteralPath $rootCandidate).Path
    [void](Invoke-GitText -Root $resolvedRoot -Arguments @("rev-parse", "--is-inside-work-tree"))

    $resolvedAllowlistPath = if ([string]::IsNullOrWhiteSpace($AllowlistPath)) {
        Join-Path $PSScriptRoot "release-repository-audit-allowlist.json"
    } elseif ([IO.Path]::IsPathRooted($AllowlistPath)) {
        $AllowlistPath
    } else {
        Join-Path $resolvedRoot $AllowlistPath
    }
    $allowlistEntries = @(Read-Allowlist -Path $resolvedAllowlistPath)

    Scan-CurrentTree -Root $resolvedRoot
    if ($IncludeHistory) {
        Scan-GitHistory -Root $resolvedRoot
    }
} catch {
    Add-ScannerError `
        -Code "unexpected-scanner-failure" `
        -Message "The repository audit stopped before it could completely inspect the requested scope."
}

$orderedFindings = @($findings | Sort-Object source, path, line, column, ruleId, objectId)
$orderedSuppressedFindings = @($suppressedFindings | Sort-Object source, path, line, column, ruleId, objectId)
$orderedScannerErrors = @($scannerErrors | Sort-Object code, path)
$status = if ($orderedScannerErrors.Count -gt 0) {
    "scanner-error"
} elseif ($orderedFindings.Count -gt 0) {
    "findings"
} else {
    "pass"
}

$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    status = $status
    scope = [pscustomobject][ordered]@{
        currentTree = $true
        trackedOnly = [bool]$TrackedOnly
        history = [bool]$IncludeHistory
        currentFileCount = $currentFileCount
        historyBlobCount = $historyBlobCount
        largeFileThresholdBytes = $LargeFileThresholdBytes
        maxContentScanBytes = $MaxContentScanBytes
    }
    summary = [pscustomobject][ordered]@{
        findingCount = $orderedFindings.Count
        suppressedFindingCount = $orderedSuppressedFindings.Count
        scannerErrorCount = $orderedScannerErrors.Count
    }
    findings = $orderedFindings
    suppressedFindings = $orderedSuppressedFindings
    scannerErrors = $orderedScannerErrors
}
$json = $report | ConvertTo-Json -Depth 8

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    try {
        $resolvedOutputPath = if ([IO.Path]::IsPathRooted($OutputPath)) {
            [IO.Path]::GetFullPath($OutputPath)
        } else {
            [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputPath))
        }
        $outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
        if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
            [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
        }
        [IO.File]::WriteAllText($resolvedOutputPath, $json, [Text.UTF8Encoding]::new($false))
    } catch {
        [void] $scannerErrors.Add([pscustomobject][ordered]@{
            code = "report-write-failed"
            path = $null
            message = "The machine-readable audit report could not be written to the requested output."
        })
        $report.status = "scanner-error"
        $report.summary.scannerErrorCount = $scannerErrors.Count
        $report.scannerErrors = @($scannerErrors | Sort-Object code, path)
        $json = $report | ConvertTo-Json -Depth 8
    }
}

# Stdout is intentionally JSON-only so CI can capture it without risking secret
# excerpts in logs. Findings contain paths, locations, rule IDs, and one-way
# fingerprints, but never matched values.
Write-Output $json

if (-not $NoFail) {
    if ($report.status -eq "scanner-error") {
        exit 2
    }
    if ($report.status -eq "findings") {
        exit 1
    }
}
exit 0
