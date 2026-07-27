Set-StrictMode -Version Latest

function Enter-Sdl3WorkRootLock {
    param(
        [Parameter(Mandatory = $true)][string] $LockPath,
        [Parameter(Mandatory = $true)][string] $OwnerLabel,
        [Parameter(Mandatory = $true)][string] $TargetId,
        [Parameter(Mandatory = $true)][string] $TargetTriple,
        [Parameter(Mandatory = $true)][string] $OutputRoot,
        [ValidateRange(1, 3600)][int] $TimeoutSeconds = 900,
        [ValidateRange(25, 5000)][int] $PollMilliseconds = 250
    )

    $lockFullPath = [System.IO.Path]::GetFullPath($LockPath)
    $lockParent = Split-Path -Parent $lockFullPath
    New-Item -ItemType Directory -Force -Path $lockParent | Out-Null
    Assert-NoReparsePointPath -Path $lockParent
    Assert-NoReparsePointPath -Path $lockFullPath

    $ownerPath = "$lockFullPath.owner.json"
    $token = [Guid]::NewGuid().ToString("N")
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $announcedWait = $false
    while ($true) {
        try {
            $stream = [System.IO.File]::Open(
                $lockFullPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            try {
                $owner = [ordered]@{
                    schemaVersion = 1
                    token = $token
                    pid = $PID
                    processStartUtc = [System.Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().ToString("O", [Globalization.CultureInfo]::InvariantCulture)
                    acquiredUtc = [DateTimeOffset]::UtcNow.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
                    machine = [Environment]::MachineName
                    ownerLabel = $OwnerLabel
                    targetId = $TargetId
                    targetTriple = $TargetTriple
                    outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
                    lockPath = $lockFullPath
                }
                $ownerJson = ($owner | ConvertTo-Json -Depth 5).Replace("`r`n", "`n") + "`n"
                $ownerBytes = [System.Text.Encoding]::UTF8.GetBytes($ownerJson)
                $stream.SetLength(0)
                $stream.Write($ownerBytes, 0, $ownerBytes.Length)
                $stream.Flush($true)

                $ownerTemporaryPath = "$ownerPath.$token.tmp"
                try {
                    [System.IO.File]::WriteAllText(
                        $ownerTemporaryPath,
                        $ownerJson,
                        [System.Text.UTF8Encoding]::new($false))
                    Move-Item -LiteralPath $ownerTemporaryPath -Destination $ownerPath -Force
                } finally {
                    if (Test-Path -LiteralPath $ownerTemporaryPath -PathType Leaf) {
                        Remove-Item -LiteralPath $ownerTemporaryPath -Force
                    }
                }
                return [pscustomobject]@{
                    Stream = $stream
                    LockPath = $lockFullPath
                    OwnerPath = $ownerPath
                    Token = $token
                }
            } catch {
                $stream.Dispose()
                throw
            }
        } catch [System.IO.IOException] {
            $ownerDescription = "owner metadata is not yet available"
            try {
                if (Test-Path -LiteralPath $ownerPath -PathType Leaf) {
                    $ownerDescription = (Get-Content -LiteralPath $ownerPath -Raw).Trim()
                }
            } catch {
                $ownerDescription = "owner metadata could not be read: $($_.Exception.Message)"
            }
            if (-not $announcedWait) {
                Write-Warning "Waiting for SDL3 stable work-root lock '$lockFullPath'. Current $ownerDescription"
                $announcedWait = $true
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw "Timed out after $TimeoutSeconds seconds waiting for SDL3 stable work-root lock '$lockFullPath' for target '$TargetId' ('$TargetTriple'). Current $ownerDescription. Wait for that process to finish or terminate the reported PID; stale OS file locks are released automatically when their process exits."
            }
            Start-Sleep -Milliseconds $PollMilliseconds
        }
    }
}

function Exit-Sdl3WorkRootLock {
    param([Parameter(Mandatory = $true)][object] $Lock)

    try {
        if (Test-Path -LiteralPath $Lock.OwnerPath -PathType Leaf) {
            try {
                $owner = Get-Content -LiteralPath $Lock.OwnerPath -Raw | ConvertFrom-Json
                if ([string]$owner.token -ceq [string]$Lock.Token) {
                    Remove-Item -LiteralPath $Lock.OwnerPath -Force
                }
            } catch {
                Write-Warning "Could not remove SDL3 work-root lock owner metadata '$($Lock.OwnerPath)': $($_.Exception.Message)"
            }
        }
    } finally {
        $Lock.Stream.Dispose()
    }
}
